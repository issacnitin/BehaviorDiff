using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BehaviorDiff.Engine
{
    /// <summary>
    /// The one consumer-facing artifact. Engine details remain in DivergenceSet and FrontierReport;
    /// this command only projects their already-decided results into a stable member-level schema.
    /// </summary>
    internal static class FindingsCommand
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        internal static void WriteAnalyzed(
            string divergenceSetPath,
            string frontierReportPath,
            string output,
            int exitCode,
            string baseSha,
            string prSha,
            string mergeBaseSha,
            string cacheStatus = "unreported",
            string cacheKey = "",
            string cacheBackend = "none",
            long cacheSavedWallClockMilliseconds = 0,
            long buildMilliseconds = 0,
            long weaveMilliseconds = 0,
            long instrumentedRunMilliseconds = 0,
            long cacheRestoreMilliseconds = 0,
            long cacheStoreMilliseconds = 0,
            long diffMilliseconds = 0,
            long frontierMilliseconds = 0,
            bool strictCommentPolicy = false)
        {
            using JsonDocument divergenceSet = JsonDocument.Parse(File.ReadAllText(divergenceSetPath));
            using JsonDocument frontierReport = JsonDocument.Parse(File.ReadAllText(frontierReportPath));

            JsonElement frontier = frontierReport.RootElement.GetProperty("frontier");
            JsonElement divergences = divergenceSet.RootElement.GetProperty("divergences");
            JsonElement coverage = frontierReport.RootElement.GetProperty("changedFileCoverage").Clone();
            JsonElement coverageSummary = coverage.GetProperty("summary");
            var nodes = frontier.EnumerateArray().ToList();
            var observations = divergences.EnumerateArray().ToList();
            var baseCallTree = divergenceSet.RootElement.GetProperty("callTree").EnumerateArray().ToList();
            var prCallTree = divergenceSet.RootElement.GetProperty("prCallTree").EnumerateArray().ToList();
            var changedFiles = new HashSet<string>(
                frontierReport.RootElement.GetProperty("attributionInputs").GetProperty("changedFiles")
                    .EnumerateArray().Select(value => value.GetString() ?? string.Empty),
                StringComparer.Ordinal);
            var noiseMethods = Methods(divergenceSet.RootElement, "noiseExclusions");
            var manifestNoiseMethods = Methods(divergenceSet.RootElement, "manifestNoise");

            var members = nodes
                .GroupBy(node => String(node, "methodFullName"), StringComparer.Ordinal)
                .OrderBy(group => String(group.First(), "attribution") == "UNEXPECTED" ? 0 : 1)
                .ThenByDescending(group => group.Count())
                .Select(group => DescribeMember(
                    group,
                    observations,
                    baseCallTree,
                    prCallTree,
                    changedFiles,
                    noiseMethods,
                    manifestNoiseMethods))
                .ToArray();

            int unexpectedMembers = members.Count(member => member.Attribution == "unexpected");
            int expectedMembers = members.Count(member => member.Attribution == "expected");
            int unexpectedCallSites = members.Where(member => member.Attribution == "unexpected").Sum(member => member.CallSiteCount);
            int expectedCallSites = members.Where(member => member.Attribution == "expected").Sum(member => member.CallSiteCount);
            int defaultEligibleMembers = members.Count(member => member.Attribution == "unexpected" && member.DefaultCommentEligible);
            int defaultEligibleCallSites = members.Where(member => member.Attribution == "unexpected" && member.DefaultCommentEligible)
                .Sum(member => member.CallSiteCount);
            int commentEligibleMembers = strictCommentPolicy ? unexpectedMembers : defaultEligibleMembers;
            int commentEligibleCallSites = strictCommentPolicy ? unexpectedCallSites : defaultEligibleCallSites;

            var artifact = new
            {
                schema = "behaviordiff.findings/1",
                generatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                status = "analyzed",
                verdict = unexpectedMembers == 0 ? "clean" : "findings",
                isCleanResult = unexpectedMembers == 0,
                exitCode,
                exitReason = unexpectedMembers == 0 ? "analyzed_no_unexpected" : "unexpected_findings",
                refs = new { baseSha, prSha, mergeBaseSha },
                commentPolicy = new
                {
                    mode = strictCommentPolicy ? "strict" : "high-confidence",
                    eligibleUnexpectedMembers = commentEligibleMembers,
                    eligibleUnexpectedCallSites = commentEligibleCallSites,
                    suppressedUnexpectedMembers = unexpectedMembers - commentEligibleMembers,
                    suppressedUnexpectedCallSites = unexpectedCallSites - commentEligibleCallSites,
                },
                baseTraceCache = new
                {
                    status = cacheStatus,
                    key = cacheKey,
                    backend = cacheBackend,
                    savedWallClockMilliseconds = cacheSavedWallClockMilliseconds,
                },
                timings = new
                {
                    buildMilliseconds,
                    weaveMilliseconds,
                    instrumentedRunMilliseconds,
                    cacheRestoreMilliseconds,
                    cacheStoreMilliseconds,
                    diffMilliseconds,
                    frontierMilliseconds,
                    measuredTotalMilliseconds = buildMilliseconds + weaveMilliseconds + instrumentedRunMilliseconds
                        + cacheRestoreMilliseconds + cacheStoreMilliseconds + diffMilliseconds + frontierMilliseconds,
                },
                summary = new
                {
                    unexpectedMembers,
                    unexpectedCallSites,
                    highConfidenceUnexpectedMembers = defaultEligibleMembers,
                    highConfidenceUnexpectedCallSites = defaultEligibleCallSites,
                    nondeterministicUnexpectedMembers = members.Count(member =>
                        member.Attribution == "unexpected" && member.Nondeterminism.Classification != "none"),
                    defaultCommentSuppressedMembers = unexpectedMembers - defaultEligibleMembers,
                    defaultCommentSuppressedCallSites = unexpectedCallSites - defaultEligibleCallSites,
                    expectedMembers,
                    expectedCallSites,
                    untestedMembers = members.Count(member => member.UntestedCallSiteCount > 0),
                    editedFiles = Int(coverageSummary, "editedFiles"),
                    exercisedEditedFiles = Int(coverageSummary, "exercisedEditedFiles"),
                    tracedMembers = Int(coverageSummary, "tracedMembers"),
                    observedCallSites = Int(coverageSummary, "observedCallSites"),
                    totalCallCount = Int(coverageSummary, "totalCallCount"),
                },
                coverage,
                members,
            };

            Write(output, artifact);
        }

        /// <summary>
        /// Invalid runs intentionally have no members property. A consumer cannot deserialize a refusal
        /// as an analyzed result with an empty array; it must first choose the status arm of the schema.
        /// </summary>
        internal static void WriteInvalid(
            string output,
            string status,
            int exitCode,
            string reason,
            string? baseSha = null,
            string? prSha = null,
            string? mergeBaseSha = null)
        {
            if (status != "refused" && status != "failed")
            {
                throw new ArgumentException("Invalid findings status '" + status + "'.", nameof(status));
            }

            var artifact = new
            {
                schema = "behaviordiff.findings/1",
                generatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                status,
                verdict = "could_not_analyze",
                isCleanResult = false,
                exitCode,
                exitReason = status == "refused" ? "analysis_refused" : "analysis_failed",
                refs = new { baseSha, prSha, mergeBaseSha },
                refusal = new { reason },
            };

            Write(output, artifact);
        }

        private static FindingMember DescribeMember(
            IGrouping<string, JsonElement> group,
            IReadOnlyList<JsonElement> divergences,
            IReadOnlyList<JsonElement> baseCallTree,
            IReadOnlyList<JsonElement> prCallTree,
            IReadOnlySet<string> changedFiles,
            IReadOnlySet<string> noiseMethods,
            IReadOnlySet<string> manifestNoiseMethods)
        {
            JsonElement first = group.First();
            string? filePath = NullableString(first, "filePath");
            bool sourceGenerated = FrontierCommand.IsGeneratedSource(filePath);
            var frontierByTest = group
                .GroupBy(node => String(node, "testId"), StringComparer.Ordinal)
                .ToDictionary(nodes => nodes.Key, nodes => nodes.First(), StringComparer.Ordinal);
            var evidence = divergences
                .Where(divergence => string.Equals(String(divergence, "methodFullName"), group.Key, StringComparison.Ordinal))
                .Select(divergence => DescribeEvidence(
                    divergence,
                    frontierByTest,
                    baseCallTree,
                    prCallTree,
                    changedFiles))
                .ToArray();
            string[] observingTests = evidence.Select(item => item.TestId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(test => test, StringComparer.Ordinal)
                .ToArray();
            int executingTestCount = baseCallTree.Concat(prCallTree)
                .Where(node => string.Equals(String(node, "methodFullName"), group.Key, StringComparison.Ordinal))
                .Select(node => String(node, "testId"))
                .Distinct(StringComparer.Ordinal)
                .Count();
            int testsWithAssertionReaction = observingTests.Count(test =>
                frontierByTest.TryGetValue(test, out JsonElement node) && !Bool(node, "untested"));
            FindingConsequence[] consequences = DescribeConsequences(
                group.Key,
                evidence,
                divergences,
                frontierByTest,
                baseCallTree,
                prCallTree,
                changedFiles);
            string[] changedFilesReachingMember = evidence.SelectMany(item => item.ChangedFilesOnPath)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            bool verified = String(first, "classification") == "frontier";
            bool exact = evidence.Length > 0
                && evidence.All(item => string.Equals(item.DigestConfidence, "Exact", StringComparison.Ordinal));
            bool reachable = changedFilesReachingMember.Length > 0;
            var nondeterminismReasons = new List<string>();
            if (noiseMethods.Contains(group.Key))
            {
                nondeterminismReasons.Add("same member varied between baseline runs");
            }

            if (manifestNoiseMethods.Contains(group.Key))
            {
                nondeterminismReasons.Add("same member's instrumentation state varied between baseline runs");
            }

            string[] commentSuppressionReasons = new[]
            {
                verified ? null : "unverified_frontier",
                exact ? null : "non_exact_digest",
                reachable ? null : "no_edited_file_reachability",
                nondeterminismReasons.Count == 0 ? null : "baseline_nondeterminism",
            }.Where(reason => reason is not null).Select(reason => reason!).ToArray();
            bool defaultCommentEligible = commentSuppressionReasons.Length == 0;
            string confidence = defaultCommentEligible
                ? "high"
                : nondeterminismReasons.Count > 0 || !exact ? "low" : "medium";

            return new FindingMember
            {
                MemberName = group.Key,
                Attribution = String(first, "attribution").ToLowerInvariant(),
                FilePath = filePath,
                Line = NullableInt(first, "line"),
                SourceGenerated = sourceGenerated,
                SourceGeneratedNote = sourceGenerated
                    ? "Generated source is not present in the git diff, so path attribution cannot classify it as edited."
                    : null,
                CallSiteCount = group.Count(),
                DistinctTestCount = executingTestCount,
                ObservingTests = observingTests,
                TestsWithAssertionReaction = testsWithAssertionReaction,
                AssertionReactionSummary = AssertionReactionSummary(executingTestCount, testsWithAssertionReaction),
                ChangedFilesReachingMember = changedFilesReachingMember,
                Verified = verified,
                Confidence = confidence,
                ConfidenceFactors = new FindingConfidenceFactors
                {
                    VerifiedFrontier = verified,
                    ExactDigest = exact,
                    EditedFileReachable = reachable,
                },
                DefaultCommentEligible = defaultCommentEligible,
                CommentSuppressionReasons = commentSuppressionReasons,
                Nondeterminism = new FindingNondeterminism
                {
                    Classification = nondeterminismReasons.Count == 0 ? "none" : "baseline-observed",
                    Reasons = nondeterminismReasons.ToArray(),
                },
                Symptoms = group.SelectMany(node => Strings(node, "symptoms")).Distinct(StringComparer.Ordinal).ToArray(),
                DowngradeReasons = group.SelectMany(node => Strings(node, "downgradeReasons")).Distinct(StringComparer.Ordinal).ToArray(),
                DescendantsCompared = group.Sum(node => Int(node, "descendantKeysCompared")),
                UntestedCallSiteCount = group.Count(node => Bool(node, "untested")),
                Evidence = evidence,
                Consequences = consequences,
            };
        }

        private static FindingConsequence[] DescribeConsequences(
            string frontierMember,
            IReadOnlyList<FindingEvidence> evidence,
            IReadOnlyList<JsonElement> divergences,
            IReadOnlyDictionary<string, JsonElement> frontierByTest,
            IReadOnlyList<JsonElement> baseCallTree,
            IReadOnlyList<JsonElement> prCallTree,
            IReadOnlySet<string> changedFiles)
        {
            var candidates = evidence
                .Select(item => new
                {
                    item.TestId,
                    MemberName = OutermostProductAncestor(item.BaseCallPath, frontierMember)
                        ?? OutermostProductAncestor(item.PrCallPath, frontierMember),
                })
                .Where(item => item.MemberName is not null)
                .DistinctBy(item => item.TestId + "|" + item.MemberName, StringComparer.Ordinal)
                .ToArray();

            var consequences = new List<FindingConsequence>();
            foreach (var candidate in candidates)
            {
                JsonElement divergence = divergences.FirstOrDefault(item =>
                    string.Equals(String(item, "testId"), candidate.TestId, StringComparison.Ordinal)
                    && string.Equals(String(item, "methodFullName"), candidate.MemberName, StringComparison.Ordinal)
                    && (NullableInt(item, "ordinal") ?? -1) >= 0
                    && (!string.Equals(
                            NullableString(item, "baseReturnRendered"),
                            NullableString(item, "prReturnRendered"),
                            StringComparison.Ordinal)
                        || !string.Equals(
                            NullableString(item, "baseExceptionType"),
                            NullableString(item, "prExceptionType"),
                            StringComparison.Ordinal)));
                if (divergence.ValueKind == JsonValueKind.Undefined)
                {
                    continue;
                }

                consequences.Add(new FindingConsequence
                {
                    MemberName = candidate.MemberName!,
                    Evidence = DescribeEvidence(
                        divergence,
                        frontierByTest,
                        baseCallTree,
                        prCallTree,
                        changedFiles),
                });
            }

            return consequences.ToArray();
        }

        private static string? OutermostProductAncestor(
            IReadOnlyList<FindingPathNode>? path,
            string frontierMember)
        {
            if (path is null)
            {
                return null;
            }

            return path.FirstOrDefault(node =>
                !node.IsHarness
                && !string.Equals(node.MemberName, frontierMember, StringComparison.Ordinal))?.MemberName;
        }

        private static FindingEvidence DescribeEvidence(
            JsonElement divergence,
            IReadOnlyDictionary<string, JsonElement> frontierByTest,
            IReadOnlyList<JsonElement> baseCallTree,
            IReadOnlyList<JsonElement> prCallTree,
            IReadOnlySet<string> changedFiles)
        {
            string testId = String(divergence, "testId");
            string memberName = String(divergence, "methodFullName");
            int ordinal = NullableInt(divergence, "ordinal") ?? -1;
            bool exactOccurrence = ordinal >= 0;
            FindingPathNode[] basePath = FindCallPath(baseCallTree, testId, memberName, ordinal);
            FindingPathNode[] prPath = FindCallPath(prCallTree, testId, memberName, ordinal);
            bool? assertionReacted = frontierByTest.TryGetValue(testId, out JsonElement frontierNode)
                ? !Bool(frontierNode, "untested")
                : null;

            return new FindingEvidence
            {
                TestId = testId,
                Ordinal = ordinal,
                Kind = String(divergence, "kind"),
                Detail = String(divergence, "detail"),
                DigestConfidence = String(divergence, "digestConfidence"),
                BaseArgs = exactOccurrence ? NullableString(divergence, "baseArgsRendered") : null,
                PrArgs = exactOccurrence ? NullableString(divergence, "prArgsRendered") : null,
                BaseReturn = exactOccurrence ? NullableString(divergence, "baseReturnRendered") : null,
                PrReturn = exactOccurrence ? NullableString(divergence, "prReturnRendered") : null,
                BaseException = exactOccurrence ? NullableString(divergence, "baseExceptionType") : null,
                PrException = exactOccurrence ? NullableString(divergence, "prExceptionType") : null,
                BaseCallPath = exactOccurrence ? basePath : null,
                PrCallPath = exactOccurrence ? prPath : null,
                AssertionReacted = assertionReacted,
                AssertionEvidence = frontierByTest.TryGetValue(testId, out frontierNode)
                    ? NullableString(frontierNode, "untestedEvidence")
                    : null,
                ChangedFilesOnPath = basePath.Concat(prPath)
                    .Select(node => node.FilePath)
                    .Where(path => path is not null && changedFiles.Contains(path))
                    .Select(path => path!)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray(),
            };
        }

        private static FindingPathNode[] FindCallPath(
            IReadOnlyList<JsonElement> callTree,
            string testId,
            string memberName,
            int ordinal)
        {
            if (ordinal < 0)
            {
                return Array.Empty<FindingPathNode>();
            }

            JsonElement? target = callTree
                .Where(node => string.Equals(String(node, "testId"), testId, StringComparison.Ordinal)
                    && string.Equals(String(node, "methodFullName"), memberName, StringComparison.Ordinal))
                .FirstOrDefault(node => NullableInt(node, "ordinal") == ordinal);
            if (target is null || target.Value.ValueKind == JsonValueKind.Undefined)
            {
                return Array.Empty<FindingPathNode>();
            }

            var byCall = callTree.ToDictionary(
                node => CallIdentity(String(node, "process"), Long(node, "callId")),
                node => node,
                StringComparer.Ordinal);
            var path = new List<FindingPathNode>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            JsonElement current = target.Value;
            while (true)
            {
                string identity = CallIdentity(String(current, "process"), Long(current, "callId"));
                if (!visited.Add(identity))
                {
                    return Array.Empty<FindingPathNode>();
                }

                path.Add(new FindingPathNode
                {
                    MemberName = String(current, "methodFullName"),
                    FilePath = NullableString(current, "filePath"),
                    Line = NullableInt(current, "line"),
                    IsHarness = Bool(current, "isHarness"),
                });

                long? parentCallId = NullableLong(current, "parentCallId");
                if (parentCallId is null)
                {
                    break;
                }

                string parentIdentity = CallIdentity(String(current, "process"), parentCallId.Value);
                if (!byCall.TryGetValue(parentIdentity, out current))
                {
                    return Array.Empty<FindingPathNode>();
                }
            }

            path.Reverse();
            return path.ToArray();
        }

        private static string AssertionReactionSummary(int observingTests, int testsWithAssertionReaction) =>
            observingTests + (observingTests == 1 ? " test executed this; " : " tests executed this; ")
            + (testsWithAssertionReaction == 0
                ? "none asserted on the changed value."
                : testsWithAssertionReaction + (testsWithAssertionReaction == 1
                    ? " test had an assertion react."
                    : " tests had an assertion react."));

        private static string CallIdentity(string process, long callId) => process + "|" + callId;

        private static void Write(string output, object artifact)
        {
            string fullPath = Path.GetFullPath(output);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, JsonSerializer.Serialize(artifact, Json));
            Console.WriteLine("Findings written: " + fullPath);
        }

        private static string String(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        private static string? NullableString(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static int Int(JsonElement element, string property) =>
            NullableInt(element, property) ?? 0;

        private static int? NullableInt(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out int number)
                    ? number
                    : null;

        private static bool Bool(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.True;

        private static long Long(JsonElement element, string property) => NullableLong(element, property) ?? 0;

        private static long? NullableLong(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out long number)
                    ? number
                    : null;

        private static IEnumerable<string> Strings(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Select(value => value.GetString() ?? string.Empty)
                : Enumerable.Empty<string>();

        private static HashSet<string> Methods(JsonElement root, string property) =>
            root.TryGetProperty(property, out JsonElement values) && values.ValueKind == JsonValueKind.Array
                ? new HashSet<string>(values.EnumerateArray().Select(value => String(value, "methodFullName")), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

        private sealed class FindingMember
        {
            public string MemberName { get; init; } = string.Empty;
            public string Attribution { get; init; } = string.Empty;
            public string? FilePath { get; init; }
            public int? Line { get; init; }
            public bool SourceGenerated { get; init; }
            public string? SourceGeneratedNote { get; init; }
            public int CallSiteCount { get; init; }
            public int DistinctTestCount { get; init; }
            public string[] ObservingTests { get; init; } = Array.Empty<string>();
            public int TestsWithAssertionReaction { get; init; }
            public string AssertionReactionSummary { get; init; } = string.Empty;
            public string[] ChangedFilesReachingMember { get; init; } = Array.Empty<string>();
            public bool Verified { get; init; }
            public string Confidence { get; init; } = string.Empty;
            public FindingConfidenceFactors ConfidenceFactors { get; init; } = new();
            public bool DefaultCommentEligible { get; init; }
            public string[] CommentSuppressionReasons { get; init; } = Array.Empty<string>();
            public FindingNondeterminism Nondeterminism { get; init; } = new();
            public string[] Symptoms { get; init; } = Array.Empty<string>();
            public string[] DowngradeReasons { get; init; } = Array.Empty<string>();
            public int DescendantsCompared { get; init; }
            public int UntestedCallSiteCount { get; init; }
            public FindingEvidence[] Evidence { get; init; } = Array.Empty<FindingEvidence>();
            public FindingConsequence[] Consequences { get; init; } = Array.Empty<FindingConsequence>();
        }

        private sealed class FindingConfidenceFactors
        {
            public bool VerifiedFrontier { get; init; }
            public bool ExactDigest { get; init; }
            public bool EditedFileReachable { get; init; }
        }

        private sealed class FindingNondeterminism
        {
            public string Classification { get; init; } = "none";
            public string[] Reasons { get; init; } = Array.Empty<string>();
        }

        private sealed class FindingConsequence
        {
            public string MemberName { get; init; } = string.Empty;
            public FindingEvidence Evidence { get; init; } = new();
        }

        private sealed class FindingEvidence
        {
            public string TestId { get; init; } = string.Empty;
            public int Ordinal { get; init; }
            public string Kind { get; init; } = string.Empty;
            public string Detail { get; init; } = string.Empty;
            public string DigestConfidence { get; init; } = string.Empty;
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? BaseArgs { get; init; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PrArgs { get; init; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? BaseReturn { get; init; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PrReturn { get; init; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? BaseException { get; init; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PrException { get; init; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public FindingPathNode[]? BaseCallPath { get; init; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public FindingPathNode[]? PrCallPath { get; init; }
            public bool? AssertionReacted { get; init; }
            public string? AssertionEvidence { get; init; }
            public string[] ChangedFilesOnPath { get; init; } = Array.Empty<string>();
        }

        private sealed class FindingPathNode
        {
            public string MemberName { get; init; } = string.Empty;
            public string? FilePath { get; init; }
            public int? Line { get; init; }
            public bool IsHarness { get; init; }
        }
    }
}