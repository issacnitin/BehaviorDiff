using System.Threading;
using BehaviorDiff.Contracts;

namespace BehaviorDiff.Tracer
{
    /// <summary>
    /// Mutable provenance for one assembly.
    /// </summary>
    /// <remarks>
    /// <see cref="NoteTracedCall"/> is driven from the tracing hot path, so it only ever sees calls into
    /// members that were already patched. It counts what was observed and cannot count what was missed;
    /// see the remarks on <see cref="AssemblyManifestEntry"/>.
    /// </remarks>
    internal sealed class AssemblyCoverage
    {
        /// <summary>Below this share of members with real source lines, the assembly is not analysable.</summary>
        internal const int ExactSourceThresholdPercent = 80;

        /// <summary>
        /// Fewer patched members than this and the percentage is not a measurement of the assembly.
        /// </summary>
        /// <remarks>
        /// At four members one unresolved member moves the ratio 25 points, so the threshold would be
        /// deciding on the divisor. Below the floor the rollup falls back to any/none.
        /// </remarks>
        internal const int MinimumMembersForRatio = 5;

        private long _tracedCalls;
        private int _patchedMembers;
        private int _patchFailedMembers;
        private int _membersWithExactSource;
        private int _patchingComplete;

        internal AssemblyCoverage(string name, AssemblyDiscovery discovery, long queuedAtMs)
        {
            Name = name;
            Discovery = discovery;
            QueuedAtMs = queuedAtMs;
        }

        internal string Name { get; }

        internal AssemblyDiscovery Discovery { get; }

        internal long QueuedAtMs { get; }

        internal long? PatchedAtMs { get; private set; }


        internal bool Scanned { get; private set; }

        internal string? Detail { get; set; }

        internal bool IsTestAssembly { get; set; }

        internal string? TestFrameworkReference { get; set; }

        internal bool PatchingComplete => Volatile.Read(ref _patchingComplete) != 0;

        internal void NoteTracedCall()
        {
            Interlocked.Increment(ref _tracedCalls);
        }

        internal void NotePatchedMember()
        {
            Interlocked.Increment(ref _patchedMembers);
        }

        internal void NotePatchFailed()
        {
            Interlocked.Increment(ref _patchFailedMembers);
        }

        internal int PatchFailedMembers => Volatile.Read(ref _patchFailedMembers);

        internal void NoteMemberSource(string resolution)
        {
            if (resolution == SourceResolution.SequencePoints || resolution == SourceResolution.StateMachine)
            {
                Interlocked.Increment(ref _membersWithExactSource);
            }
        }

        internal long TracedCalls => Interlocked.Read(ref _tracedCalls);

        internal int PatchedMembers => Volatile.Read(ref _patchedMembers);

        internal int MembersWithExactSource => Volatile.Read(ref _membersWithExactSource);

        /// <summary>Members with a real source line, as a percentage of patched members.</summary>
        internal int ExactSourcePercent =>
            PatchedMembers == 0 ? 100 : (int)(MembersWithExactSource * 100L / PatchedMembers);

        /// <summary>Which rule produced <see cref="SourceUnavailable"/> for this assembly.</summary>
        internal string SourceRuleApplied =>
            PatchedMembers == 0 ? SourceRule.NotApplicable
            : PatchedMembers < MinimumMembersForRatio ? SourceRule.AnyNone
            : SourceRule.Ratio;

        /// <summary>
        /// Too little of the assembly has attributable source to trust any verdict about it.
        /// </summary>
        /// <remarks>
        /// A ratio, not any/none, once there are enough members to measure. An assembly where a handful of
        /// members resolve and most do not would clear an any/none check and then classify its
        /// unattributable divergences as EXPECTED, which inverts the output rather than degrading it.
        /// Below <see cref="MinimumMembersForRatio"/> the ratio is noise, so only a total absence of
        /// source counts.
        /// </remarks>
        internal bool SourceUnavailable =>
            SourceRuleApplied switch
            {
                SourceRule.Ratio => ExactSourcePercent < ExactSourceThresholdPercent,
                SourceRule.AnyNone => MembersWithExactSource == 0,
                _ => false,
            };

        /// <summary>
        /// Most but not all members have attributable source. Divergences on the members that do not get
        /// </summary>
        /// <remarks>Only meaningful under the ratio rule; below the floor the band would be noise too.</remarks>
        internal bool SourcePartial =>
            SourceRuleApplied == SourceRule.Ratio
            && ExactSourcePercent >= ExactSourceThresholdPercent
            && ExactSourcePercent < 100;

        internal void MarkComplete(long patchedAtMs, bool afterStartup, bool scanned)
        {
            PatchedAtMs = patchedAtMs;
            Scanned = scanned;
            Volatile.Write(ref _patchingComplete, 1);
        }

        internal AssemblyManifestEntry ToManifestEntry()
        {
            int patched = Volatile.Read(ref _patchedMembers);

            return new AssemblyManifestEntry
            {
                Assembly = Name,
                Discovery = Discovery,
                Scanned = Scanned,
                Instrumented = patched > 0,
                PatchedMembers = patched,
                PatchFailedMembers = PatchFailedMembers,
                QueuedAtMs = QueuedAtMs,
                PatchedAtMs = PatchedAtMs,
                TracedCalls = Interlocked.Read(ref _tracedCalls),
                MembersWithExactSource = Volatile.Read(ref _membersWithExactSource),
                ExactSourcePercent = ExactSourcePercent,
                SourceRuleApplied = SourceRuleApplied,
                SourceUnavailable = SourceUnavailable,
                SourcePartial = SourcePartial,
                IsTestAssembly = IsTestAssembly,
                TestFrameworkReference = TestFrameworkReference,
                Detail = Detail,
            };
        }
    }
}
