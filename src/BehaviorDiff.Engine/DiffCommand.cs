using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BehaviorDiff.Engine
{
    internal sealed class DiffOptions
    {
        internal string Base1 { get; set; } = string.Empty;

        internal string Base2 { get; set; } = string.Empty;

        /// <summary>Optional third base run. Sampling showed the third run finds most of what two miss.</summary>
        internal string Base3 { get; set; } = string.Empty;

        /// <summary>Needed to tell an added method in an edited file from one the weaver simply saw differently.</summary>
        internal string ChangedFiles { get; set; } = string.Empty;

        internal string Pr { get; set; } = string.Empty;

        internal string? BaseRoot { get; set; }

        internal string? PrRoot { get; set; }

        internal string Output { get; set; } = string.Empty;

        internal string? RefusalReason { get; set; }
    }

    /// <summary>
    /// Steps 0-4: normalize paths, diff manifests, build a noise baseline from two base runs, match, and
    /// classify digest trustworthiness. Frontier detection and attribution are not done here.
    /// </summary>
    internal static class DiffCommand
    {
        private const int MinimumMatchedKeys = 100;
        private const double MaximumBaseCountDriftPercent = 10.0;
        private const double MaximumNoisePercent = 20.0;
        private const double MinimumPathOverlapPercent = 50.0;

        internal static int Run(DiffOptions options)
        {
            var refusals = new List<string>();

            var base1Report = new LoadReport();
            var base2Report = new LoadReport();
            var base3Report = new LoadReport();
            var prReport = new LoadReport();

            RunData base1 = RunLoader.Load("base_run1", options.Base1, options.BaseRoot, base1Report);
            RunData base2 = RunLoader.Load("base_run2", options.Base2, options.BaseRoot, base2Report);
            RunData? base3 = options.Base3.Length == 0
                ? null
                : RunLoader.Load("base_run3", options.Base3, options.BaseRoot, base3Report);
            RunData pr = RunLoader.Load("pr_run", options.Pr, options.PrRoot, prReport);

            var baseRuns = new List<RunData> { base1, base2 };
            if (base3 != null)
            {
                baseRuns.Add(base3);
            }

            Console.WriteLine("=== STEP 0: path normalization ===");
            WriteLoad(base1, base1Report);
            WriteLoad(base2, base2Report);
            if (base3 != null)
            {
                WriteLoad(base3, base3Report);
            }

            WriteLoad(pr, prReport);

            int absoluteRemaining = base1Report.AbsolutePathsRemaining + base2Report.AbsolutePathsRemaining
                + base3Report.AbsolutePathsRemaining + prReport.AbsolutePathsRemaining;
            Console.WriteLine("  absolute paths remaining : " + absoluteRemaining + " (must be 0)");
            if (absoluteRemaining > 0)
            {
                refusals.Add("STEP 0: " + absoluteRemaining + " FilePath(s) are still absolute after normalization. "
                    + "Every subsequent comparison would treat identical files as different. Pass --base-root/--pr-root.");
            }

            var basePaths = RelativePaths(base1);
            var prPaths = RelativePaths(pr);
            int shared = basePaths.Intersect(prPaths, StringComparer.Ordinal).Count();
            double overlap = basePaths.Count == 0 ? 0 : shared * 100.0 / basePaths.Count;
            Console.WriteLine("  distinct relative paths  : base=" + basePaths.Count + " pr=" + prPaths.Count + " shared=" + shared);
            Console.WriteLine("  base/PR path overlap     : " + overlap.ToString("F1", CultureInfo.InvariantCulture) + "% (must be >= "
                + MinimumPathOverlapPercent.ToString("F0", CultureInfo.InvariantCulture) + "%)");
            if (overlap < MinimumPathOverlapPercent)
            {
                refusals.Add("STEP 0: base and PR relative path sets overlap only "
                    + overlap.ToString("F1", CultureInfo.InvariantCulture) + "%. Normalization is wrong, so every comparison below it is garbage.");
            }

            Console.WriteLine();
            Console.WriteLine("=== STEP 1: manifest diff (tooling gaps) ===");

            // Same three-run structure as the event baseline. A manifest difference that also shows up
            // between two runs of the same build is nondeterministic tracer coverage, not a coverage gap
            // introduced by the PR. Left in, a flaky gap on a real assembly would intermittently suppress
            // genuine findings with nothing in the output to say it happened.
            var noiseSignatures = new HashSet<string>(StringComparer.Ordinal);
            var manifestNoise = new List<ToolingGap>();
            for (int i = 0; i < baseRuns.Count; i++)
            {
                for (int j = i + 1; j < baseRuns.Count; j++)
                {
                    foreach (ToolingGap gap in ManifestDiff.Compute(baseRuns[i], baseRuns[j]))
                    {
                        if (noiseSignatures.Add(GapSignature(gap)))
                        {
                            manifestNoise.Add(gap);
                        }
                    }
                }
            }

            IReadOnlyList<ToolingGap> allGaps = ManifestDiff.Compute(base1, pr);
            var gaps = allGaps.Where(g => !noiseSignatures.Contains(GapSignature(g))).ToList();

            // A member that appeared or disappeared inside a file the PR edited is the PR's doing, not the
            // weaver's. Only absent <-> Patched qualifies: Patched <-> Skipped is MethodSelector reaching a
            // different verdict about the same member, which is a change in what we can see, not in the code.
            var changedFiles = FrontierCommand.LoadChangedFiles(options.ChangedFiles);
            var methodFiles = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (RunData run in new[] { base1, pr })
            {
                foreach (LoadedEvent traced in run.Events)
                {
                    if (traced.Event.MethodFullName is { Length: > 0 } name
                        && traced.RelativePath is { Length: > 0 } path
                        && !methodFiles.ContainsKey(name))
                    {
                        methodFiles[name] = path;
                    }
                }
            }

            var lifecycle = new List<ToolingGap>();
            var generatedLifecycle = new List<ToolingGap>();
            var remainingGaps = new List<ToolingGap>();
            foreach (ToolingGap gap in gaps)
            {
                string? file = gap.MethodFullName is { Length: > 0 } m
                    && methodFiles.TryGetValue(m, out string? found) ? found : null;

                if (IsMethodLifecycle(gap) && file != null && changedFiles.Contains(file))
                {
                    lifecycle.Add(gap);
                }
                else
                {
                    // A generated file is never in a git diff, so path attribution cannot reach it. Say so
                    // rather than letting the member disappear into the gap list unexplained.
                    if (IsMethodLifecycle(gap) && FrontierCommand.IsGeneratedSource(file))
                    {
                        generatedLifecycle.Add(gap);
                    }

                    remainingGaps.Add(gap);
                }
            }

            gaps = remainingGaps;

            // Suppression still covers the promoted members: the matcher would otherwise report the same
            // one-sided member again as MissingInBase/MissingInPr, and the lifecycle kind is the better name.
            HashSet<string> gapMethods = ManifestDiff.AffectedMethods(gaps.Concat(lifecycle).ToList());

            Console.WriteLine("  raw gaps (base vs PR)    : " + allGaps.Count);
            Console.WriteLine("  ManifestNoise (base runs): " + manifestNoise.Count);
            foreach (ToolingGap gap in manifestNoise)
            {
                Console.WriteLine("    noise: " + (gap.MethodFullName ?? gap.Assembly) + "  " + gap.Scope
                    + "  " + gap.BaseState + " -> " + gap.PrState);
            }

            Console.WriteLine("  tooling gaps (real)      : " + gaps.Count);
            Console.WriteLine("  promoted to behavior     : " + lifecycle.Count
                + "  (member added/removed inside a changed file)");
            foreach (ToolingGap gap in lifecycle.Take(10))
            {
                Console.WriteLine("    " + gap.BaseState + " -> " + gap.PrState + "  " + gap.MethodFullName);
            }

            if (generatedLifecycle.Count > 0)
            {
                Console.WriteLine("  source-generated members : " + generatedLifecycle.Count
                    + "  (added or removed, but emitted into obj/ so no git diff can name them)");
                foreach (ToolingGap gap in generatedLifecycle.Take(10))
                {
                    string file = gap.MethodFullName is { Length: > 0 } m && methodFiles.TryGetValue(m, out string? f)
                        ? f
                        : "<unresolved>";
                    Console.WriteLine("    " + gap.BaseState + " -> " + gap.PrState + "  " + gap.MethodFullName);
                    Console.WriteLine("      source-generated, not in the git diff: " + file);
                }
            }
            foreach (var group in gaps.GroupBy(g => g.Scope).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                Console.WriteLine("    " + group.Key + " : " + group.Count());
            }

            foreach (ToolingGap gap in gaps.Take(10))
            {
                Console.WriteLine("    " + (gap.MethodFullName ?? gap.Assembly) + "  " + gap.BaseState + " -> " + gap.PrState);
            }

            Console.WriteLine();
            Console.WriteLine("=== STEP 2: noise baseline (" + baseRuns.Count + " base runs, all pairs) ===");
            var baseIndexes = baseRuns.Select(Matcher.Index).ToList();
            var base1Index = baseIndexes[0];

            // Union over every pair, not just consecutive ones: a key can agree in one pair and disagree in
            // another, and any disagreement at all disqualifies it as evidence.
            var noiseKeys = new HashSet<CallKey>();
            var noiseDivergences = new List<Divergence>();
            int baseKeysCompared = 0;
            int pairsCompared = 0;
            for (int i = 0; i < baseIndexes.Count; i++)
            {
                for (int j = i + 1; j < baseIndexes.Count; j++)
                {
                    (List<Divergence> pairDivergences, List<MatchedKey> pairMatched, _) =
                        Matcher.Compare(baseIndexes[i], baseIndexes[j]);
                    baseKeysCompared = Math.Max(baseKeysCompared, pairMatched.Count);
                    pairsCompared++;
                    foreach (Divergence divergence in pairDivergences)
                    {
                        if (noiseKeys.Add(divergence.Key))
                        {
                            noiseDivergences.Add(divergence);
                        }
                    }
                }
            }

            Console.WriteLine("  base keys compared       : " + baseKeysCompared + " (" + pairsCompared + " pair(s))");
            Console.WriteLine("  nondeterministic keys    : " + noiseKeys.Count);

            // Sampling on FluentValidation gave 2,122 keys from 2 runs and 2,388 from 4, so the set is still
            // growing when we stop. Whatever it misses is charged to the PR as a divergence.
            Console.WriteLine("  RESIDUAL: this exclusion set is a sample from " + baseRuns.Count
                + " base run(s), not a complete characterisation of what varies between runs.");
            Console.WriteLine("            Keys that happened to agree across these runs but vary in general");
            Console.WriteLine("            remain in the comparison and will be reported as divergences.");
            if (baseRuns.Count < 3)
            {
                Console.WriteLine("            Two base runs is a single sample of each key; pass --base3 for a better one.");
            }

            foreach (var group in noiseDivergences.GroupBy(d => d.Key.MethodFullName)
                .OrderByDescending(g => g.Count()).Take(15))
            {
                Console.WriteLine("    " + group.Count().ToString(CultureInfo.InvariantCulture).PadLeft(5) + "x  " + group.Key);
            }

            Console.WriteLine();
            Console.WriteLine("=== STEP 3: match base vs PR ===");
            var prIndex = Matcher.Index(pr);
            (List<Divergence> raw, List<MatchedKey> matched, List<Divergence> harnessDivergences) = Matcher.Compare(base1Index, prIndex);

            Console.WriteLine("  matched keys             : " + matched.Count);
            Console.WriteLine("  raw differences          : " + raw.Count);

            var afterNoise = raw.Where(d => !noiseKeys.Contains(d.Key)).ToList();
            Console.WriteLine("  excluded as noise        : " + (raw.Count - afterNoise.Count));

            var remaining = afterNoise.Where(d => !gapMethods.Contains(d.Key.MethodFullName)).ToList();
            Console.WriteLine("  excluded as tooling gap  : " + (afterNoise.Count - remaining.Count));

            // One divergence per test that reached the member, so the frontier can place it in a call tree.
            var lifecycleDivergences = new List<Divergence>();
            foreach (ToolingGap gap in lifecycle)
            {
                bool added = string.Equals(gap.PrState, "Patched", StringComparison.Ordinal);
                RunData side = added ? pr : base1;
                string method = gap.MethodFullName!;

                foreach (string testId in side.Events
                    .Where(e => string.Equals(e.Event.MethodFullName, method, StringComparison.Ordinal))
                    .Select(e => e.Event.TestId ?? string.Empty)
                    .Distinct(StringComparer.Ordinal))
                {
                    lifecycleDivergences.Add(new Divergence
                    {
                        Key = new CallKey(testId, method),
                        Ordinal = -1,
                        Kind = added ? DivergenceKind.MethodAdded : DivergenceKind.MethodRemoved,
                        Detail = added
                            ? "method is absent from the base manifest and instrumented in the PR's"
                            : "method is instrumented in the base manifest and absent from the PR's",
                        RelativePath = methodFiles.TryGetValue(method, out string? p) ? p : null,
                    });
                }
            }

            if (lifecycleDivergences.Count > 0)
            {
                Console.WriteLine("  added/removed members    : " + lifecycleDivergences.Count
                    + " divergence(s) across " + lifecycle.Count + " member(s)");
                remaining.AddRange(lifecycleDivergences);
            }

            Console.WriteLine("  remaining divergences    : " + remaining.Count);

            foreach (var group in remaining.GroupBy(d => d.Kind).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal))
            {
                Console.WriteLine("    " + group.Key + " : " + group.Count());
            }

            Console.WriteLine();
            Console.WriteLine("=== STEP 4: digest trustworthiness ===");
            int partialKeys = matched.Count(m => m.Confidence == DigestConfidence.Partial);
            Console.WriteLine("  matched keys Exact       : " + (matched.Count - partialKeys));
            Console.WriteLine("  matched keys Partial     : " + partialKeys
                + "  (an identical Partial digest does not prove the values were identical)");
            foreach (var group in matched.Where(m => m.Confidence == DigestConfidence.Partial)
                .SelectMany(m => m.PartialMarkers).GroupBy(m => m, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()))
            {
                Console.WriteLine("    marker " + group.Key + " : " + group.Count() + " key(s)");
            }

            Console.WriteLine("  divergences Partial      : " + remaining.Count(d => d.Confidence == DigestConfidence.Partial));

            Console.WriteLine();
            Console.WriteLine("=== volume preconditions ===");
            CheckVolume(refusals, base1, base2, pr, matched, noiseKeys);

            if (refusals.Count > 0)
            {
                options.RefusalReason = string.Join(Environment.NewLine, refusals);
                Console.WriteLine();
                Console.Error.WriteLine("REFUSED to emit a DivergenceSet. An empty or degenerate comparison compares equal,");
                Console.Error.WriteLine("and a clean report produced from no data is indistinguishable from a clean result.");
                foreach (string refusal in refusals)
                {
                    Console.Error.WriteLine("  - " + refusal);
                }

                return 4;
            }

            WriteArtifact(options, base1, base2, pr, gaps, manifestNoise, noiseDivergences, noiseKeys, matched, raw, remaining, harnessDivergences);
            Console.WriteLine();
            Console.WriteLine("DivergenceSet written: " + Path.GetFullPath(options.Output));
            return 0;
        }

        /// <summary>Only absent &lt;-&gt; Patched. A Skipped transition is the weaver's verdict changing.</summary>
        private static bool IsMethodLifecycle(ToolingGap gap)
        {
            if (!string.Equals(gap.Scope, "member", StringComparison.Ordinal))
            {
                return false;
            }

            return (string.Equals(gap.BaseState, "absent", StringComparison.Ordinal)
                    && string.Equals(gap.PrState, "Patched", StringComparison.Ordinal))
                || (string.Equals(gap.BaseState, "Patched", StringComparison.Ordinal)
                    && string.Equals(gap.PrState, "absent", StringComparison.Ordinal));
        }

        private static string GapSignature(ToolingGap gap) =>
            gap.Scope + "|" + gap.Assembly + "|" + (gap.MethodFullName ?? string.Empty);

        private static void CheckVolume(
            List<string> refusals,
            RunData base1,
            RunData base2,
            RunData pr,
            List<MatchedKey> matched,
            HashSet<CallKey> noiseKeys)
        {
            int b1 = base1.SubjectEventCount;
            int b2 = base2.SubjectEventCount;
            int p = pr.SubjectEventCount;

            Console.WriteLine("  subject events           : base1=" + b1 + " base2=" + b2 + " pr=" + p);
            foreach ((string name, int count) in new[] { ("base_run1", b1), ("base_run2", b2), ("pr_run", p) })
            {
                if (count == 0)
                {
                    refusals.Add("VOLUME: run '" + name + "' has zero subject events.");
                }
            }

            double drift = b1 == 0 ? 100 : Math.Abs(b1 - b2) * 100.0 / b1;
            Console.WriteLine("  base run drift           : " + drift.ToString("F2", CultureInfo.InvariantCulture)
                + "% (max " + MaximumBaseCountDriftPercent.ToString("F0", CultureInfo.InvariantCulture) + "%)");
            if (drift > MaximumBaseCountDriftPercent)
            {
                refusals.Add("VOLUME: base_run1 and base_run2 subject event counts differ by "
                    + drift.ToString("F2", CultureInfo.InvariantCulture) + "%, above the "
                    + MaximumBaseCountDriftPercent.ToString("F0", CultureInfo.InvariantCulture)
                    + "% limit. The two base runs are not comparable, so the noise baseline is not a baseline.");
            }

            Console.WriteLine("  matched keys             : " + matched.Count + " (min " + MinimumMatchedKeys + ")");
            if (matched.Count < MinimumMatchedKeys)
            {
                refusals.Add("VOLUME: only " + matched.Count + " matched key(s), below the minimum of "
                    + MinimumMatchedKeys + ". Too little was compared for a clean result to mean anything.");
            }

            double noisePercent = matched.Count == 0 ? 100 : noiseKeys.Count * 100.0 / matched.Count;
            Console.WriteLine("  noise share of keys      : " + noisePercent.ToString("F2", CultureInfo.InvariantCulture)
                + "% (max " + MaximumNoisePercent.ToString("F0", CultureInfo.InvariantCulture) + "%)");
            if (noisePercent > MaximumNoisePercent)
            {
                refusals.Add("VOLUME: the noise exclusion set covers "
                    + noisePercent.ToString("F2", CultureInfo.InvariantCulture) + "% of keys, above the "
                    + MaximumNoisePercent.ToString("F0", CultureInfo.InvariantCulture)
                    + "% limit. Excluding that much would hide real changes rather than cancel noise.");
            }
        }

        private static HashSet<string> RelativePaths(RunData run)
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (LoadedEvent loaded in run.Events)
            {
                if (!loaded.Event.IsHarness && !string.IsNullOrEmpty(loaded.RelativePath))
                {
                    paths.Add(loaded.RelativePath!);
                }
            }

            return paths;
        }

        private static void WriteLoad(RunData run, LoadReport report)
        {
            Console.WriteLine("  " + run.Name.PadRight(10)
                + " files=" + run.TraceFiles.Count
                + " events=" + run.Events.Count
                + " subject=" + run.SubjectEventCount
                + " harness=" + run.HarnessEventCount
                + " malformed=" + report.MalformedLines);
            Console.WriteLine("    root " + (report.RootWasInferred ? "(inferred)" : "(explicit)") + ": " + run.Root);
            Console.WriteLine("    normalized=" + report.PathsNormalized
                + " alreadyRelative=" + report.PathsAlreadyRelative
                + " missing=" + report.PathsMissing
                + " stillAbsolute=" + report.AbsolutePathsRemaining);
        }

        private static void WriteArtifact(
            DiffOptions options,
            RunData base1,
            RunData base2,
            RunData pr,
            IReadOnlyList<ToolingGap> gaps,
            IReadOnlyList<ToolingGap> manifestNoise,
            List<Divergence> noiseDivergences,
            HashSet<CallKey> noiseKeys,
            List<MatchedKey> matched,
            List<Divergence> raw,
            List<Divergence> remaining,
            List<Divergence> harnessDivergences)
        {
            var artifact = new
            {
                schema = "behaviordiff.divergenceset/2",
                generatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                runs = new
                {
                    base1 = Describe(base1),
                    base2 = Describe(base2),
                    pr = Describe(pr),
                },
                counts = new
                {
                    matchedKeys = matched.Count,
                    rawDifferences = raw.Count,
                    noiseExcludedKeys = noiseKeys.Count,
                    noiseExcludedDifferences = raw.Count(d => noiseKeys.Contains(d.Key)),
                    toolingGaps = gaps.Count,
                    remainingDivergences = remaining.Count,
                    matchedKeysPartial = matched.Count(m => m.Confidence == DigestConfidence.Partial),
                },
                matchedKeys = matched.Select(m => new
                {
                    testId = m.Key.TestId,
                    methodFullName = m.Key.MethodFullName,
                    filePath = m.RelativePath,
                    baseCalls = m.BaseCalls,
                    prCalls = m.PrCalls,
                    digestConfidence = m.Confidence.ToString(),
                    partialMarkers = m.PartialMarkers,
                }).ToArray(),
                divergences = remaining.Select(Describe).ToArray(),
                noiseExclusions = noiseKeys.OrderBy(k => k.ToString(), StringComparer.Ordinal).Select(k => new
                {
                    testId = k.TestId,
                    methodFullName = k.MethodFullName,
                    differences = noiseDivergences.Count(d => d.Key.Equals(k)),
                }).ToArray(),
                toolingGaps = gaps.Select(g => new
                {
                    scope = g.Scope,
                    assembly = g.Assembly,
                    methodFullName = g.MethodFullName,
                    baseState = g.BaseState,
                    prState = g.PrState,
                    reason = g.Reason,
                }).ToArray(),
                manifestNoise = manifestNoise.Select(g => new
                {
                    scope = g.Scope,
                    assembly = g.Assembly,
                    methodFullName = g.MethodFullName,
                    run1State = g.BaseState,
                    run2State = g.PrState,
                    reason = "nondeterministic tracer coverage: differs between two runs of the same build",
                }).ToArray(),

                // Part 2 needs these; it must not re-derive matching from the traces.
                harnessDivergences = harnessDivergences.Select(d => new
                {
                    testId = d.Key.TestId,
                    methodFullName = d.Key.MethodFullName,
                    kind = d.Kind.ToString(),
                    detail = d.Detail,
                    isTestRoot = base1.Members.TryGetValue(d.Key.MethodFullName, out var member)
                        ? member.IsTestRoot
                        : (bool?)null,
                }).ToArray(),
                coverage = new
                {
                    members = base1.Members.Values.Select(m => new
                    {
                        methodFullName = m.MethodFullName,
                        assembly = m.Assembly,
                        status = m.Status.ToString(),
                        skipReason = m.SkipReason,
                        sourceResolution = m.SourceResolution,
                        isTestRoot = m.IsTestRoot,
                    }).ToArray(),
                    assemblies = base1.Assemblies.Values.Select(a => new
                    {
                        assembly = a.Assembly,
                        instrumented = a.Instrumented,
                        sourcePartial = a.SourcePartial,
                        sourceUnavailable = a.SourceUnavailable,
                    }).ToArray(),
                },
                callTree = DescribeCallTree(base1),
                prCallTree = DescribeCallTree(pr),
            };

            string json = JsonSerializer.Serialize(artifact, new JsonSerializerOptions { WriteIndented = true });
            string? directory = Path.GetDirectoryName(Path.GetFullPath(options.Output));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(options.Output, json);
        }

        private static object Describe(RunData run) => new
        {
            name = run.Name,
            root = run.Root,
            traceFiles = run.TraceFiles.Count,
            events = run.Events.Count,
            subjectEvents = run.SubjectEventCount,
            harnessEvents = run.HarnessEventCount,
        };

        private static object[] DescribeCallTree(RunData run)
        {
            Dictionary<LoadedEvent, int> ordinals = Matcher.Index(run)
                .SelectMany(pair => pair.Value)
                .ToDictionary(call => call.Loaded, call => call.Ordinal);

            return run.Events.Select(e => (object)new
            {
                callId = e.Event.CallId,
                parentCallId = e.Event.ParentCallId,
                testId = e.Event.TestId,
                methodFullName = e.Event.MethodFullName,
                ordinal = ordinals[e],
                isHarness = e.Event.IsHarness,
                filePath = e.RelativePath,
                line = e.Event.Line,
                process = e.ProcessKey,
            }).ToArray();
        }

        private static object Describe(Divergence d) => new
        {
            testId = d.Key.TestId,
            methodFullName = d.Key.MethodFullName,
            filePath = d.RelativePath,
            ordinal = d.Ordinal,
            kind = d.Kind.ToString(),
            detail = d.Detail,
            digestConfidence = d.Confidence.ToString(),
            partialMarkers = d.PartialMarkers,
            baseArgsDigest = d.BaseEvent?.ArgsDigest,
            prArgsDigest = d.PrEvent?.ArgsDigest,
            baseArgsRendered = d.BaseEvent?.ArgsRendered,
            prArgsRendered = d.PrEvent?.ArgsRendered,
            baseReturnDigest = d.BaseEvent?.ReturnDigest,
            prReturnDigest = d.PrEvent?.ReturnDigest,
            baseReturnRendered = d.BaseEvent?.ReturnRendered,
            prReturnRendered = d.PrEvent?.ReturnRendered,
            baseExceptionType = d.BaseEvent?.ExceptionType,
            prExceptionType = d.PrEvent?.ExceptionType,
        };
    }
}
