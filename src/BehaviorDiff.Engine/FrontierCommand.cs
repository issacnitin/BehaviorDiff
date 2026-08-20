using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BehaviorDiff.Engine
{
    internal sealed class FrontierOptions
    {
        internal string Input { get; set; } = string.Empty;

        internal string ChangedFiles { get; set; } = string.Empty;

        internal string Output { get; set; } = string.Empty;

        internal string? RefusalReason { get; set; }
    }

    internal sealed class FrontierNode
    {
        internal string TestId { get; init; } = string.Empty;

        internal string MethodFullName { get; init; } = string.Empty;

        internal string? FilePath { get; init; }

        internal int? Line { get; init; }

        internal bool Verified { get; set; }

        internal List<string> Symptoms { get; } = new();

        internal List<string> DowngradeReasons { get; } = new();

        internal int DescendantKeys { get; set; }

        internal bool Untested { get; set; }

        internal string? Evidence { get; set; }

        internal string Attribution { get; set; } = "UNEXPECTED";
    }

    internal sealed class ChangedFileCoverage
    {
        internal string FilePath { get; init; } = string.Empty;

        internal int TracedMembers { get; init; }

        internal int CallSites { get; init; }

        internal int BaseCallCount { get; init; }

        internal int PrCallCount { get; init; }

        internal int TotalCallCount => BaseCallCount + PrCallCount;

        internal bool Exercised => TotalCallCount > 0;
    }

    /// <summary>
    /// Part 2: locates where each change originated, suppresses everything that merely propagated up from
    /// it, and splits the survivors by whether the file was edited.
    /// </summary>
    internal static class FrontierCommand
    {
        internal static int Run(FrontierOptions options)
        {
            DivergenceSetFile set = DivergenceSetReader.Read(options.Input);
            var refusals = new List<string>();

            Console.WriteLine("=== input ===");
            Console.WriteLine("  matched keys             : " + set.Counts.MatchedKeys);
            Console.WriteLine("  divergences (part 1)     : " + set.Counts.RemainingDivergences);
            Console.WriteLine("  tooling gaps (real)      : " + set.ToolingGaps.Count);
            Console.WriteLine("  ManifestNoise cancelled  : " + set.ManifestNoise.Count);

            Console.WriteLine();
            Console.WriteLine("=== call tree ===");
            var byCallId = new Dictionary<long, CallNodeDto>();
            foreach (CallNodeDto node in set.CallTree)
            {
                byCallId[node.CallId] = node;
            }

            var children = new Dictionary<long, List<CallNodeDto>>();
            var orphans = new List<CallNodeDto>();
            int roots = 0;
            foreach (CallNodeDto node in set.CallTree)
            {
                if (node.ParentCallId is null)
                {
                    roots++;
                    continue;
                }

                if (!byCallId.TryGetValue(node.ParentCallId.Value, out CallNodeDto? parent))
                {
                    orphans.Add(node);
                    continue;
                }

                // A parent from another process would mean the tree is being stitched across trace files.
                if (!string.Equals(parent.Process, node.Process, StringComparison.Ordinal))
                {
                    orphans.Add(node);
                    continue;
                }

                if (!children.TryGetValue(node.ParentCallId.Value, out List<CallNodeDto>? list))
                {
                    list = new List<CallNodeDto>();
                    children[node.ParentCallId.Value] = list;
                }

                list.Add(node);
            }

            Console.WriteLine("  nodes                    : " + set.CallTree.Count);
            Console.WriteLine("  roots (harness)          : " + roots
                + "  of which IsHarness=" + set.CallTree.Count(n => n.ParentCallId is null && n.IsHarness));
            Console.WriteLine("  orphans (parent missing) : " + orphans.Count);
            foreach (var group in orphans.GroupBy(o => o.MethodFullName).OrderByDescending(g => g.Count()).Take(10))
            {
                Console.WriteLine("    " + group.Count() + "x  " + group.Key);
            }

            if (orphans.Count > 0)
            {
                refusals.Add("CALL TREE: " + orphans.Count + " non-root event(s) could not resolve a parent. "
                    + "Descendant sets would be incomplete, which makes every frontier verdict unsound.");
            }

            var divergedKeys = new Dictionary<string, List<DivergenceDto>>(StringComparer.Ordinal);
            foreach (DivergenceDto d in set.Divergences)
            {
                string key = d.TestId + "|" + d.MethodFullName;
                if (!divergedKeys.TryGetValue(key, out List<DivergenceDto>? list))
                {
                    list = new List<DivergenceDto>();
                    divergedKeys[key] = list;
                }

                list.Add(d);
            }

            var confidenceByKey = set.MatchedKeys.ToDictionary(
                m => m.TestId + "|" + m.MethodFullName,
                m => m.DigestConfidence,
                StringComparer.Ordinal);

            var skippedByType = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (CoverageMemberDto member in set.Coverage.Members)
            {
                // Type initializers are excluded. Every type with a static field has one, so including
                // them downgrades essentially every node; and a cctor runs once at type load rather than
                // as a callee of this subtree, so its absence is not a gap in this subtree's coverage.
                if (member.Status == "Skipped"
                    && member.MethodFullName != null
                    && member.SkipReason != "TypeInitializer")
                {
                    skippedByType[DeclaringType(member.MethodFullName)] = member.MethodFullName
                        + (member.SkipReason is null ? string.Empty : " (" + member.SkipReason + ")");
                }
            }

            var memberAssembly = set.Coverage.Members
                .Where(m => m.MethodFullName != null)
                .GroupBy(m => m.MethodFullName!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Assembly, StringComparer.Ordinal);
// Nothing degrades a whole assembly any more: LatePatched was the only such reason and it
                // went with the runtime patcher. Kept as an empty map so the per-member downgrades below,
                // which are still live, do not need restructuring when another assembly-wide reason appears.
                var degradedAssemblies = new Dictionary<string, string>(StringComparer.Ordinal);

            // Per member, not per assembly. SourcePartial is a rollup, so keying on it downgrades every
            // node in a partially-resolved assembly whether or not the descendant in question is the
            // unresolved one. The manifest records each member's own resolution, which is the precise fact.
            var unresolvedMembers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (CoverageMemberDto member in set.Coverage.Members)
            {
                if (member.MethodFullName != null
                    && member.SourceResolution != null
                    && member.SourceResolution != "sequencePoints"
                    && member.SourceResolution != "stateMachine")
                {
                    unresolvedMembers[member.MethodFullName] = member.SourceResolution;
                }
            }

            var harnessDivergedTests = new HashSet<string>(
                set.HarnessDivergences
                    .Where(h => h.IsTestRoot != false)
                    .Select(h => h.TestId),
                StringComparer.Ordinal);

            Console.WriteLine();
            Console.WriteLine("=== frontier ===");
            Console.WriteLine("  diverged keys            : " + divergedKeys.Count);

            var nodes = new List<FrontierNode>();
            var collateral = new List<FrontierNode>();

            foreach (string key in divergedKeys.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                List<DivergenceDto> entries = divergedKeys[key];
                DivergenceDto first = entries[0];

                var node = new FrontierNode
                {
                    TestId = first.TestId,
                    MethodFullName = first.MethodFullName,
                    FilePath = first.FilePath,
                    Line = set.CallTree.FirstOrDefault(call =>
                        string.Equals(call.TestId, first.TestId, StringComparison.Ordinal)
                        && string.Equals(call.MethodFullName, first.MethodFullName, StringComparison.Ordinal))?.Line,
                };

                foreach (DivergenceDto entry in entries.OrderBy(e => e.Kind, StringComparer.Ordinal))
                {
                    node.Symptoms.Add(entry.Kind + ":" + entry.Detail);
                }

                // An added method has no base counterpart and a removed one has no PR counterpart, so
                // "all descendants identical" is not a question that can be answered either way.
                bool lifecycle = entries.Any(e => e.Kind == "MethodAdded" || e.Kind == "MethodRemoved");
                bool unalignable = lifecycle || entries.Any(e => e.Kind == "CallCountChange");

                // Descendants of every call of this key in the base tree.
                var descendantKeys = new HashSet<string>(StringComparer.Ordinal);
                var descendantMethods = new HashSet<string>(StringComparer.Ordinal);
                if (!unalignable)
                {
                    foreach (CallNodeDto call in set.CallTree.Where(n =>
                        string.Equals(n.TestId, first.TestId, StringComparison.Ordinal)
                        && string.Equals(n.MethodFullName, first.MethodFullName, StringComparison.Ordinal)))
                    {
                        CollectDescendants(call, children, descendantKeys, descendantMethods);
                    }
                }

                node.DescendantKeys = descendantKeys.Count;

                bool anyDescendantDiverged = descendantKeys.Any(divergedKeys.ContainsKey);

                if (unalignable)
                {
                    // The subtree cannot be aligned call-for-call, so no claim about descendants is available.
                    node.Verified = false;
                    node.DowngradeReasons.Add(lifecycle
                        ? "MemberLifecycle: the member exists on only one side, so it has no counterpart to compare descendants against. This says the PR added or removed it, not that it caused anything."
                        : "SubtreeUnalignable: call count differs, descendants are not alignable");
                }
                else if (anyDescendantDiverged)
                {
                    collateral.Add(node);
                    continue;
                }

                if (confidenceByKey.TryGetValue(key, out string? ownConfidence) && ownConfidence == "Partial")
                {
                    node.DowngradeReasons.Add("NodePartial: this key's own digest is Partial");
                }

                foreach (string descendantKey in descendantKeys)
                {
                    if (confidenceByKey.TryGetValue(descendantKey, out string? c) && c == "Partial")
                    {
                        node.DowngradeReasons.Add("DescendantPartial: " + descendantKey.Split('|')[1]
                            + " has a Partial digest, so 'identical' does not establish identical behavior");
                        break;
                    }
                }

                foreach (string method in descendantMethods.Concat(new[] { first.MethodFullName }))
                {
                    // Keyed on declaring type, not on the descendant list. A skipped member emits no trace
                    // event, so it is never in the call tree - looking for it there can never match. What
                    // is observable is that a type present in the subtree also declares something the
                    // tracer could not watch, so a call to it from inside that type would be invisible.
                    // Coarser than a real call edge, and stated as such in the reason.
                    string type = DeclaringType(method);
                    if (skippedByType.TryGetValue(type, out string? skipped))
                    {
                        node.DowngradeReasons.Add("DescendantSkipped: " + type + " also declares " + skipped
                            + ", which was never instrumented, so a call to it from this subtree would be invisible");
                        break;
                    }
                }

                foreach (string method in descendantMethods)
                {
                    if (memberAssembly.TryGetValue(method, out string? assembly)
                        && degradedAssemblies.TryGetValue(assembly, out string? why))
                    {
                        node.DowngradeReasons.Add("Descendant" + why + ": " + method + " lives in " + assembly);
                        break;
                    }
                }

                foreach (string method in descendantMethods)
                {
                    if (unresolvedMembers.TryGetValue(method, out string? resolution))
                    {
                        node.DowngradeReasons.Add("DescendantSourceUnresolved: " + method
                            + " resolved its source as '" + resolution + "', so a divergence in it could not be attributed");
                        break;
                    }
                }

                node.Verified = node.DowngradeReasons.Count == 0;

                // Approximation, stated as such: if the test itself behaved identically, nothing in it
                // observed this change. It does not prove the value is unasserted, only that no assertion
                // reacted to it in this run.
                node.Untested = !harnessDivergedTests.Contains(node.TestId);
                node.Evidence = node.Untested
                    ? "no harness event in this test diverged, so no assertion reacted to the change"
                    : "the test's own trace diverged, so an assertion reacted";

                nodes.Add(node);
            }

            Console.WriteLine("  frontier nodes           : " + nodes.Count);
            Console.WriteLine("    verified               : " + nodes.Count(n => n.Verified));
            Console.WriteLine("    frontier_unverified    : " + nodes.Count(n => !n.Verified));
            Console.WriteLine("  collateral (suppressed)  : " + collateral.Count);
            double collapse = nodes.Count == 0 ? 0 : divergedKeys.Count * 1.0 / nodes.Count;
            Console.WriteLine("  collapse                 : " + divergedKeys.Count + " diverged keys -> " + nodes.Count
                + " frontier node(s)" + (nodes.Count == 0 ? string.Empty : "  (" + collapse.ToString("F1", CultureInfo.InvariantCulture) + "x)"));

            Console.WriteLine();
            Console.WriteLine("=== attribution ===");
            var changed = LoadChangedFiles(options.ChangedFiles);
            Console.WriteLine("  changed files            : " + changed.Count);
            foreach (string file in changed.Take(10))
            {
                Console.WriteLine("    " + file);
            }

            List<ChangedFileCoverage> changedFileCoverage = BuildChangedFileCoverage(set, changed);
            PrintChangedFileCoverage(changedFileCoverage);

            if (changed.Count == 0)
            {
                // Only a problem when there is something to attribute. With no frontier nodes there is
                // nothing that could be misclassified, and an empty changed set is the correct input for a
                // same-commit run rather than a missing one.
                if (nodes.Count > 0)
                {
                    refusals.Add("ATTRIBUTION: the changed-file set is empty but there are " + nodes.Count
                        + " frontier node(s). All of them would be classified UNEXPECTED, which reads as a large "
                        + "finding rather than as a missing input.");
                }
                else
                {
                    Console.WriteLine("  (empty, and no frontier nodes to attribute - nothing can be misclassified)");
                }
            }

            var tracePaths = new HashSet<string>(
                set.CallTree.Where(n => !string.IsNullOrEmpty(n.FilePath)).Select(n => n.FilePath!),
                StringComparer.Ordinal);
            var traceRoots = new HashSet<string>(tracePaths.Select(FirstSegment), StringComparer.Ordinal);

            int exactMatches = changed.Count(tracePaths.Contains);
            int namespaceMatches = changed.Count(c => traceRoots.Contains(FirstSegment(c)));
            Console.WriteLine("  changed paths that exactly match a traced file : " + exactMatches);
            Console.WriteLine("  changed paths in the trace path namespace      : " + namespaceMatches);
            if (changed.Count > 0 && namespaceMatches == 0)
            {
                refusals.Add("ATTRIBUTION: no changed path shares a root segment with any traced path. "
                    + "The two sets are in different path formats, so every node would be classified UNEXPECTED "
                    + "and the run would look like a spectacular finding. Sample changed='" + changed.First()
                    + "' traced='" + (tracePaths.FirstOrDefault() ?? "<none>") + "'.");
            }

            // A changed file can be present in the manifest while every member is deliberately skipped by
            // MethodSelector. In that narrow case the zero-call footprint is itself explained; downstream
            // frontiers remain useful. Missing manifest coverage and mixed skip reasons still refuse.
            bool intentionallyUntraced = AreChangedFilesIntentionallyUntraced(set, changed);
            if (changed.Count > 0 && exactMatches == 0 && intentionallyUntraced)
            {
                Console.WriteLine("  all edited members were intentionally skipped by repository-owned tracing policy");
            }

            bool unattributable = changed.Count > 0 && nodes.Count > 0 && exactMatches == 0 && !intentionallyUntraced;
            if (unattributable)
            {
                var reasons = new List<string>();
                foreach (string file in changed)
                {
                    string stem = Path.GetFileNameWithoutExtension(file);
                    if (stem.Length == 0)
                    {
                        continue;
                    }

                    string[] why = set.Coverage.Members
                        .Where(m => m.MethodFullName != null
                            && !string.IsNullOrEmpty(m.SkipReason)
                            && string.Equals(DeclaringTypeSimpleName(m.MethodFullName!), stem, StringComparison.Ordinal))
                        .GroupBy(m => m.SkipReason!, StringComparer.Ordinal)
                        .OrderByDescending(g => g.Count())
                        .Select(g => g.Key + " x" + g.Count())
                        .ToArray();

                    reasons.Add("      " + file + " -> "
                        + (why.Length == 0 ? "no member of this file reached the manifest" : string.Join(", ", why)));
                }

                refusals.Add("ATTRIBUTION: none of the " + changed.Count + " changed file(s) contributed a single "
                    + "traced member, so all " + nodes.Count + " frontier node(s) would be reported as UNEXPECTED "
                    + "changes in files the PR never touched. The edited code was not observed, so this run cannot "
                    + "attribute anything." + Environment.NewLine
                    + "    changed files and why their members were not instrumented:" + Environment.NewLine
                    + string.Join(Environment.NewLine, reasons));
            }

            foreach (FrontierNode node in nodes)
            {
                node.Attribution = node.FilePath != null && changed.Contains(node.FilePath) ? "EXPECTED" : "UNEXPECTED";
            }

            var unexpected = nodes.Where(n => n.Attribution == "UNEXPECTED").ToList();
            var expected = nodes.Where(n => n.Attribution == "EXPECTED").ToList();
            Console.WriteLine("  EXPECTED (edited file)   : " + RollupLine(expected));
            Console.WriteLine("  UNEXPECTED (headline)    : " + RollupLine(unexpected));

            if (refusals.Count > 0)
            {
                options.RefusalReason = string.Join(Environment.NewLine, refusals);
                Console.WriteLine();
                Console.Error.WriteLine("REFUSED to emit a frontier report.");
                foreach (string refusal in refusals)
                {
                    Console.Error.WriteLine("  - " + refusal);
                }

                // An unattributable run is invalid input, not a malformed trace.
                return unattributable ? 3 : 4;
            }

            Console.WriteLine();
            Console.WriteLine("=== HEADLINE: unexpected behavior changes ===");
            if (unexpected.Count == 0)
            {
                Console.WriteLine("  none");
            }

            PrintRollup(unexpected);

            if (expected.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("=== expected (in edited files) ===");
                PrintRollup(expected);
            }

            var untested = nodes.Where(n => n.Untested).ToList();
            Console.WriteLine();
            Console.WriteLine("=== untested subset (approximation) ===");
            Console.WriteLine("  " + untested.Count + " of " + nodes.Count + " frontier node(s) changed behavior with no assertion reacting.");
            Console.WriteLine("  Approximation: a test whose own trace is identical is treated as not having observed the change.");
            Console.WriteLine("  It does not prove the value is unasserted, only that nothing reacted in this run.");
            foreach (FrontierNode node in untested)
            {
                Console.WriteLine("    " + node.MethodFullName + "  [" + node.TestId + "]");
            }

            WriteReport(
                options,
                set,
                nodes,
                collateral,
                divergedKeys.Count,
                changed,
                changedFileCoverage,
                exactMatches,
                namespaceMatches);
            Console.WriteLine();
            Console.WriteLine("Frontier report written: " + Path.GetFullPath(options.Output));
            return 0;
        }

        private static void Print(FrontierNode node)
        {
            Console.WriteLine("  " + (node.Verified ? "frontier" : "frontier_unverified") + "  " + node.MethodFullName);
            Console.WriteLine("    file      : " + (node.FilePath ?? "<unresolved>"));
            Console.WriteLine("    test      : " + node.TestId);
            Console.WriteLine("    symptoms  : " + string.Join("; ", node.Symptoms));
            Console.WriteLine("    descendants compared : " + node.DescendantKeys);
            foreach (string reason in node.DowngradeReasons)
            {
                Console.WriteLine("    downgraded: " + reason);
            }

            Console.WriteLine("    untested  : " + node.Untested + " (" + node.Evidence + ")");
        }

        private static void CollectDescendants(
            CallNodeDto node,
            Dictionary<long, List<CallNodeDto>> children,
            HashSet<string> keys,
            HashSet<string> methods)
        {
            if (!children.TryGetValue(node.CallId, out List<CallNodeDto>? kids))
            {
                return;
            }

            foreach (CallNodeDto child in kids)
            {
                keys.Add(child.TestId + "|" + child.MethodFullName);
                methods.Add(child.MethodFullName);
                CollectDescendants(child, children, keys, methods);
            }
        }

        /// <summary>
        /// Compiler- or generator-emitted source. Such a file is never in a git diff, so it cannot be
        /// attributed to the PR by path even when the PR is what caused it to be generated.
        /// </summary>
        internal static bool IsGeneratedSource(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            return path!.Contains("/obj/", StringComparison.Ordinal)
                || path.StartsWith("obj/", StringComparison.Ordinal)
                || path.EndsWith(".g.cs", StringComparison.Ordinal)
                || path.EndsWith(".generated.cs", StringComparison.Ordinal);
        }

        /// <summary>"11 member(s), across 2962 call site(s)" - members are the finding, call sites the evidence.</summary>
        private static string RollupLine(IReadOnlyList<FrontierNode> nodes)
        {
            int members = nodes.Select(n => n.MethodFullName).Distinct(StringComparer.Ordinal).Count();
            return members + " member(s), across " + nodes.Count + " call site(s)";
        }

        private static void PrintRollup(IReadOnlyList<FrontierNode> nodes)
        {
            foreach (var group in nodes
                .GroupBy(n => n.MethodFullName, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()))
            {
                FrontierNode first = group.First();
                Console.WriteLine("  " + (first.Verified ? "frontier" : "frontier_unverified") + "  " + group.Key);
                Console.WriteLine("    call sites : " + group.Count()
                    + " (" + group.Select(n => n.TestId).Distinct(StringComparer.Ordinal).Count() + " distinct test(s))");
                Console.WriteLine("    file       : " + (first.FilePath ?? "<unresolved>")
                    + (IsGeneratedSource(first.FilePath) ? "   [source-generated, not in the git diff]" : string.Empty));

                foreach (string symptom in group.SelectMany(n => n.Symptoms).Distinct(StringComparer.Ordinal).Take(3))
                {
                    Console.WriteLine("    symptom    : " + symptom);
                }

                foreach (string reason in group.SelectMany(n => n.DowngradeReasons).Distinct(StringComparer.Ordinal).Take(2))
                {
                    Console.WriteLine("    downgraded : " + reason);
                }
            }
        }

        private static string FirstSegment(string path)
        {
            int slash = path.IndexOf('/');
            return slash < 0 ? path : path.Substring(0, slash);
        }

        /// <summary>"NS.Type.Method(args)" and "NS.Type..ctor()" both reduce to "NS.Type".</summary>
        private static string DeclaringType(string methodFullName)
        {
            int paren = methodFullName.IndexOf('(');
            string qualified = paren < 0 ? methodFullName : methodFullName.Substring(0, paren);
            int lastDot = qualified.LastIndexOf('.');
            return lastDot < 0 ? qualified : qualified.Substring(0, lastDot).TrimEnd('.');
        }

        /// <summary>Simple type name without generic arity, so it can be matched against a source file stem.</summary>
        private static string DeclaringTypeSimpleName(string methodFullName)
        {
            string qualified = DeclaringType(methodFullName);
            int dot = qualified.LastIndexOf('.');
            string simple = dot < 0 ? qualified : qualified.Substring(dot + 1);
            int tick = simple.IndexOf('`');
            return tick < 0 ? simple : simple.Substring(0, tick);
        }

        private static bool AreChangedFilesIntentionallyUntraced(
            DivergenceSetFile set,
            IReadOnlyCollection<string> changedFiles)
        {
            if (changedFiles.Count == 0)
            {
                return false;
            }

            foreach (string file in changedFiles)
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                CoverageMemberDto[] members = set.Coverage.Members
                    .Where(member => member.MethodFullName != null
                        && string.Equals(
                            DeclaringTypeSimpleName(member.MethodFullName!),
                            stem,
                            StringComparison.Ordinal))
                    .ToArray();
                if (members.Length == 0 || members.Any(member => member.Status != "Skipped"
                    || (member.SkipReason != "PropertyOrOperator"
                        && member.SkipReason != "TypeInitializer"
                        && member.SkipReason != "ExcludedNamespace")))
                {
                    return false;
                }
            }

            return true;
        }

        internal static HashSet<string> LoadChangedFiles(string path)
        {
            var changed = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return changed;
            }

            foreach (string line in File.ReadAllLines(path))
            {
                string trimmed = line.Trim().Replace('\\', '/');
                if (trimmed.Length > 0)
                {
                    changed.Add(trimmed);
                }
            }

            return changed;
        }

        private static List<ChangedFileCoverage> BuildChangedFileCoverage(
            DivergenceSetFile set,
            IEnumerable<string> changedFiles)
        {
            var result = new List<ChangedFileCoverage>();
            foreach (string file in changedFiles.OrderBy(path => path, StringComparer.Ordinal))
            {
                List<CallNodeDto> baseCalls = set.CallTree
                    .Where(call => !call.IsHarness && string.Equals(call.FilePath, file, StringComparison.Ordinal))
                    .ToList();
                List<CallNodeDto> prCalls = set.PrCallTree
                    .Where(call => !call.IsHarness && string.Equals(call.FilePath, file, StringComparison.Ordinal))
                    .ToList();

                int members = baseCalls.Concat(prCalls)
                    .Select(call => call.MethodFullName)
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                int callSites = baseCalls.Concat(prCalls)
                    .Select(call => call.TestId + "|" + call.MethodFullName)
                    .Distinct(StringComparer.Ordinal)
                    .Count();

                result.Add(new ChangedFileCoverage
                {
                    FilePath = file,
                    TracedMembers = members,
                    CallSites = callSites,
                    BaseCallCount = baseCalls.Count,
                    PrCallCount = prCalls.Count,
                });
            }

            return result;
        }

        private static void PrintChangedFileCoverage(IReadOnlyList<ChangedFileCoverage> coverage)
        {
            Console.WriteLine();
            Console.WriteLine("=== changed-file coverage ===");
            Console.WriteLine("  exercised edited files  : " + coverage.Count(file => file.Exercised)
                + " of " + coverage.Count);
            Console.WriteLine("  traced members          : " + coverage.Sum(file => file.TracedMembers));
            Console.WriteLine("  observed call sites     : " + coverage.Sum(file => file.CallSites));
            Console.WriteLine("  total calls             : " + coverage.Sum(file => file.TotalCallCount)
                + " (base=" + coverage.Sum(file => file.BaseCallCount)
                + " pr=" + coverage.Sum(file => file.PrCallCount) + ")");
            foreach (ChangedFileCoverage file in coverage)
            {
                Console.WriteLine("    " + (file.Exercised ? "EXERCISED    " : "NOT EXERCISED") + "  "
                    + file.FilePath + "  members=" + file.TracedMembers
                    + " callSites=" + file.CallSites
                    + " calls=" + file.TotalCallCount
                    + " (base=" + file.BaseCallCount + " pr=" + file.PrCallCount + ")"
                    + (file.Exercised ? string.Empty : "  [no behavioral claim]"));
            }
        }

        private static void WriteReport(
            FrontierOptions options,
            DivergenceSetFile set,
            List<FrontierNode> nodes,
            List<FrontierNode> collateral,
            int divergedKeyCount,
            HashSet<string> changed,
            List<ChangedFileCoverage> changedFileCoverage,
            int exactMatches,
            int namespaceMatches)
        {
            var report = new
            {
                schema = "behaviordiff.frontierreport/2",
                generatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                counts = new
                {
                    divergedKeys = divergedKeyCount,
                    frontierNodes = nodes.Count,
                    frontierVerified = nodes.Count(n => n.Verified),
                    frontierUnverified = nodes.Count(n => !n.Verified),
                    collateralSuppressed = collateral.Count,
                    unexpected = nodes.Count(n => n.Attribution == "UNEXPECTED"),
                    expected = nodes.Count(n => n.Attribution == "EXPECTED"),
                    untested = nodes.Count(n => n.Untested),
                    manifestNoiseCancelled = set.ManifestNoise.Count,
                    toolingGaps = set.ToolingGaps.Count,
                },
                attributionInputs = new
                {
                    changedFiles = changed.OrderBy(c => c, StringComparer.Ordinal).ToArray(),
                    changedPathsMatchingATracedFile = exactMatches,
                    changedPathsInTracePathNamespace = namespaceMatches,
                },
                changedFileCoverage = new
                {
                    summary = new
                    {
                        editedFiles = changedFileCoverage.Count,
                        exercisedEditedFiles = changedFileCoverage.Count(file => file.Exercised),
                        tracedMembers = changedFileCoverage.Sum(file => file.TracedMembers),
                        observedCallSites = changedFileCoverage.Sum(file => file.CallSites),
                        baseCallCount = changedFileCoverage.Sum(file => file.BaseCallCount),
                        prCallCount = changedFileCoverage.Sum(file => file.PrCallCount),
                        totalCallCount = changedFileCoverage.Sum(file => file.TotalCallCount),
                    },
                    files = changedFileCoverage.Select(file => new
                    {
                        filePath = file.FilePath,
                        exercised = file.Exercised,
                        tracedMembers = file.TracedMembers,
                        observedCallSites = file.CallSites,
                        baseCallCount = file.BaseCallCount,
                        prCallCount = file.PrCallCount,
                        totalCallCount = file.TotalCallCount,
                        interpretation = file.Exercised
                            ? "executed by tests in the representative base or PR run"
                            : "not observed; zero calls are not evidence of unchanged behavior",
                    }).ToArray(),
                },
                frontier = nodes.Select(Describe).ToArray(),

                // Retained, not reported: these diverged only because a frontier below them did.
                collateral = collateral.Select(Describe).ToArray(),
                untestedApproximation = "A frontier node is reported untested when no harness event in its test "
                    + "diverged. That means no assertion reacted to the change in this run; it is not proof that "
                    + "the value is unasserted.",
            };

            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            string? directory = Path.GetDirectoryName(Path.GetFullPath(options.Output));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(options.Output, json);
        }

        private static object Describe(FrontierNode node) => new
        {
            classification = node.Verified ? "frontier" : "frontier_unverified",
            attribution = node.Attribution,
            testId = node.TestId,
            methodFullName = node.MethodFullName,
            filePath = node.FilePath,
            line = node.Line,
            symptoms = node.Symptoms,
            downgradeReasons = node.DowngradeReasons,
            descendantKeysCompared = node.DescendantKeys,
            untested = node.Untested,
            untestedEvidence = node.Evidence,
        };
    }
}
