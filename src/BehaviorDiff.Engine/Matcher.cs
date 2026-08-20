using System;
using System.Collections.Generic;
using System.Linq;
using BehaviorDiff.Contracts;

namespace BehaviorDiff.Engine
{
    /// <summary>Whether an "identical" verdict on a digest can be believed.</summary>
    internal enum DigestConfidence
    {
        /// <summary>The canonical text covered the whole value.</summary>
        Exact,

        /// <summary>
        /// Part of the value was elided. Differences that DIFFER are still real, but two Partial digests
        /// being equal does not mean the values were equal: the difference can sit inside the elided
        /// region. Nothing downstream may claim "all children identical" from a Partial match.
        /// </summary>
        Partial,
    }

    internal sealed class CallRecord
    {
        internal CallRecord(LoadedEvent loaded, int ordinal)
        {
            Loaded = loaded;
            Ordinal = ordinal;
        }

        internal LoadedEvent Loaded { get; }

        internal int Ordinal { get; }

        internal TraceEvent Event => Loaded.Event;
    }

    internal readonly struct CallKey : IEquatable<CallKey>
    {
        internal CallKey(string testId, string methodFullName)
        {
            TestId = testId;
            MethodFullName = methodFullName;
        }

        internal string TestId { get; }

        internal string MethodFullName { get; }

        public bool Equals(CallKey other) =>
            string.Equals(TestId, other.TestId, StringComparison.Ordinal)
            && string.Equals(MethodFullName, other.MethodFullName, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is CallKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(TestId, MethodFullName);

        public override string ToString() => TestId + "|" + MethodFullName;
    }

    internal enum DivergenceKind
    {
        DigestDiff,
        CallCountChange,
        MissingInPr,
        MissingInBase,

        /// <summary>
        /// The PR added this method: absent from the base manifest, instrumented in the PR's. Kept apart
        /// from DigestDiff because there is no base counterpart, so it carries no comparable evidence.
        /// </summary>
        MethodAdded,

        /// <summary>The PR removed this method: instrumented in the base manifest, absent from the PR's.</summary>
        MethodRemoved,
    }

    internal sealed class Divergence
    {
        internal CallKey Key { get; init; }

        internal int Ordinal { get; init; }

        internal DivergenceKind Kind { get; init; }

        internal string Detail { get; init; } = string.Empty;

        internal DigestConfidence Confidence { get; init; }

        internal IReadOnlyList<string> PartialMarkers { get; init; } = Array.Empty<string>();

        internal TraceEvent? BaseEvent { get; init; }

        internal TraceEvent? PrEvent { get; init; }

        internal string? RelativePath { get; init; }
    }

    internal sealed class MatchedKey
    {
        internal CallKey Key { get; init; }

        internal int BaseCalls { get; init; }

        internal int PrCalls { get; init; }

        internal DigestConfidence Confidence { get; init; }

        internal IReadOnlyList<string> PartialMarkers { get; init; } = Array.Empty<string>();

        internal string? RelativePath { get; init; }
    }

    internal static class Matcher
    {
        private static readonly string[] PartialMarkerPrefixes = { "<skipped:", "<depth:", "<error:", "<truncated>" };

        /// <summary>
        /// Groups a run's events by (TestId, MethodFullName) with an ordinal per key.
        /// </summary>
        /// <remarks>
        /// Ordinal is position in file order within one process. A test runs in a single process, so a key
        /// never spans processes; ordering by (process, line) keeps it deterministic when several
        /// processes are merged. Harness events are indexed too - the call tree needs them as roots - and
        /// are filtered out of candidacy separately.
        /// </remarks>
        internal static Dictionary<CallKey, List<CallRecord>> Index(RunData run)
        {
            var index = new Dictionary<CallKey, List<CallRecord>>();

            foreach (LoadedEvent loaded in run.Events
                .OrderBy(e => e.ProcessKey, StringComparer.Ordinal)
                .ThenBy(e => e.LineNumber))
            {
                var key = new CallKey(loaded.Event.TestId, loaded.Event.MethodFullName);
                if (!index.TryGetValue(key, out List<CallRecord>? calls))
                {
                    calls = new List<CallRecord>();
                    index[key] = calls;
                }

                calls.Add(new CallRecord(loaded, calls.Count));
            }

            return index;
        }

        internal static (DigestConfidence Confidence, IReadOnlyList<string> Markers) Classify(params TraceEvent?[] events)
        {
            var markers = new List<string>();
            foreach (TraceEvent? traceEvent in events)
            {
                if (traceEvent is null)
                {
                    continue;
                }

                CollectMarkers(traceEvent.ArgsRendered, markers);
                CollectMarkers(traceEvent.ReturnRendered, markers);
            }

            return markers.Count == 0
                ? (DigestConfidence.Exact, Array.Empty<string>())
                : (DigestConfidence.Partial, markers.Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal).ToArray());
        }

        private static void CollectMarkers(string? rendered, List<string> markers)
        {
            if (string.IsNullOrEmpty(rendered))
            {
                return;
            }

            foreach (string prefix in PartialMarkerPrefixes)
            {
                if (rendered.Contains(prefix, StringComparison.Ordinal))
                {
                    markers.Add(prefix.TrimEnd('>').TrimStart('<').TrimEnd(':'));
                }
            }
        }

        /// <summary>Compares two runs. Harness keys are compared into a separate list, never reported as findings.</summary>
        internal static (List<Divergence> Divergences, List<MatchedKey> Matched, List<Divergence> HarnessDivergences) Compare(
            Dictionary<CallKey, List<CallRecord>> baseIndex,
            Dictionary<CallKey, List<CallRecord>> prIndex)
        {
            var divergences = new List<Divergence>();
            var harnessDivergences = new List<Divergence>();
            var matched = new List<MatchedKey>();

            var keys = new HashSet<CallKey>(baseIndex.Keys);
            keys.UnionWith(prIndex.Keys);

            foreach (CallKey key in keys.OrderBy(k => k.ToString(), StringComparer.Ordinal))
            {
                baseIndex.TryGetValue(key, out List<CallRecord>? baseCalls);
                prIndex.TryGetValue(key, out List<CallRecord>? prCalls);

                bool isHarness = (baseCalls ?? prCalls)![0].Event.IsHarness;
                List<Divergence> sink = isHarness ? harnessDivergences : divergences;

                string? relativePath = (baseCalls ?? prCalls)![0].Loaded.RelativePath;

                if (baseCalls is null || prCalls is null)
                {
                    List<CallRecord> present = (baseCalls ?? prCalls)!;
                    (DigestConfidence confidence, IReadOnlyList<string> markers) = Classify(present[0].Event);
                    sink.Add(new Divergence
                    {
                        Key = key,
                        Ordinal = -1,
                        Kind = baseCalls is null ? DivergenceKind.MissingInBase : DivergenceKind.MissingInPr,
                        Detail = baseCalls is null
                            ? "key absent from base, " + present.Count + " call(s) in PR"
                            : "key absent from PR, " + present.Count + " call(s) in base",
                        Confidence = confidence,
                        PartialMarkers = markers,
                        BaseEvent = baseCalls?[0].Event,
                        PrEvent = prCalls?[0].Event,
                        RelativePath = relativePath,
                    });
                    continue;
                }

                (DigestConfidence keyConfidence, IReadOnlyList<string> keyMarkers) =
                    Classify(baseCalls[0].Event, prCalls[0].Event);

                if (!isHarness)
                {
                    matched.Add(new MatchedKey
                    {
                        Key = key,
                        BaseCalls = baseCalls.Count,
                        PrCalls = prCalls.Count,
                        Confidence = keyConfidence,
                        PartialMarkers = keyMarkers,
                        RelativePath = relativePath,
                    });
                }

                if (baseCalls.Count != prCalls.Count)
                {
                    sink.Add(new Divergence
                    {
                        Key = key,
                        Ordinal = -1,
                        Kind = DivergenceKind.CallCountChange,
                        Detail = "called " + baseCalls.Count + " time(s) in base, " + prCalls.Count + " in PR",
                        Confidence = keyConfidence,
                        PartialMarkers = keyMarkers,
                        BaseEvent = baseCalls[0].Event,
                        PrEvent = prCalls[0].Event,
                        RelativePath = relativePath,
                    });
                }

                int common = Math.Min(baseCalls.Count, prCalls.Count);
                for (int i = 0; i < common; i++)
                {
                    TraceEvent b = baseCalls[i].Event;
                    TraceEvent p = prCalls[i].Event;

                    string? difference = FirstDifference(b, p);
                    if (difference is null)
                    {
                        continue;
                    }

                    (DigestConfidence confidence, IReadOnlyList<string> markers) = Classify(b, p);
                    sink.Add(new Divergence
                    {
                        Key = key,
                        Ordinal = i,
                        Kind = DivergenceKind.DigestDiff,
                        Detail = difference,
                        Confidence = confidence,
                        PartialMarkers = markers,
                        BaseEvent = b,
                        PrEvent = p,
                        RelativePath = baseCalls[i].Loaded.RelativePath,
                    });
                }
            }

            return (divergences, matched, harnessDivergences);
        }

        private static string? FirstDifference(TraceEvent b, TraceEvent p)
        {
            if (!string.Equals(b.ArgsDigest, p.ArgsDigest, StringComparison.Ordinal))
            {
                return "argsDigest";
            }

            if (!string.Equals(b.ReturnDigest, p.ReturnDigest, StringComparison.Ordinal))
            {
                return "returnDigest";
            }

            if (!string.Equals(b.ExceptionType, p.ExceptionType, StringComparison.Ordinal))
            {
                return "exceptionType";
            }

            return null;
        }
    }
}
