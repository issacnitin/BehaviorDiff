using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace BehaviorDiff.Tracer
{
    /// <summary>
    /// Process-wide counters for the canonicalizer, surfaced in the coverage manifest.
    /// </summary>
    /// <remarks>
    /// The shape-rule set is curated and therefore incomplete. <see cref="UnruledEnumerables"/> is the
    /// list that says where: any IEnumerable with no rule fell through to raw field digestion, which
    /// exposes incidental state such as spare capacity and mutation counters.
    /// </remarks>
    internal static class DigestStatistics
    {
        private static long s_valuesDigested;
        private static long s_depthLimited;
        private static long s_blocklisted;
        private static long s_errored;
        private static long s_renderedTruncated;

        private static readonly ConcurrentDictionary<string, StrongBox> s_unruled =
            new ConcurrentDictionary<string, StrongBox>();

        internal static long ValuesDigested => Interlocked.Read(ref s_valuesDigested);

        internal static long DepthLimited => Interlocked.Read(ref s_depthLimited);

        internal static long Blocklisted => Interlocked.Read(ref s_blocklisted);

        internal static long Errored => Interlocked.Read(ref s_errored);

        /// <summary>Values whose rendered text exceeded the cap. The hash still covers the full text.</summary>
        internal static long RenderedTruncated => Interlocked.Read(ref s_renderedTruncated);

        internal static void NoteRenderedTruncated()
        {
            Interlocked.Increment(ref s_renderedTruncated);
        }

        internal static void NoteValue()
        {
            Interlocked.Increment(ref s_valuesDigested);
        }

        internal static void NoteDepthLimited()
        {
            Interlocked.Increment(ref s_depthLimited);
        }

        internal static void NoteBlocklisted()
        {
            Interlocked.Increment(ref s_blocklisted);
        }

        internal static void NoteErrored()
        {
            Interlocked.Increment(ref s_errored);
        }

        internal static void NoteUnruledEnumerable(string typeName)
        {
            StrongBox box = s_unruled.GetOrAdd(typeName, static _ => new StrongBox());
            Interlocked.Increment(ref box.Count);
        }

        internal static IReadOnlyList<KeyValuePair<string, long>> UnruledEnumerables()
        {
            var results = new List<KeyValuePair<string, long>>(s_unruled.Count);
            foreach (KeyValuePair<string, StrongBox> entry in s_unruled)
            {
                results.Add(new KeyValuePair<string, long>(entry.Key, Interlocked.Read(ref entry.Value.Count)));
            }

            results.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
            return results;
        }

        private sealed class StrongBox
        {
            internal long Count;
        }
    }
}
