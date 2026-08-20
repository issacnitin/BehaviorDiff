namespace BehaviorDiff.Contracts
{
    /// <summary>Which rule produced an assembly's source verdict.</summary>
    /// <remarks>
    /// Recorded per assembly so a small-assembly result is never read as a ratio result. "2 of 3 members"
    /// and "200 of 300 members" are both 66%, but only the second is a measurement of the assembly; the
    /// first is mostly a measurement of the divisor.
    /// </remarks>
    public static class SourceRule
    {
        /// <summary>No members patched, so there is nothing to judge.</summary>
        public const string NotApplicable = "notApplicable";

        /// <summary>Enough members to make a percentage meaningful; the threshold was applied.</summary>
        public const string Ratio = "ratio";

        /// <summary>Below the member floor: only a total absence of source counts as unavailable.</summary>
        public const string AnyNone = "anyNone";
    }

    /// <summary>
    /// How a member's <see cref="TraceEvent.FilePath"/> was arrived at.
    /// </summary>
    /// <remarks>
    /// The engine attributes a divergence by matching FilePath against the changed-file set, and treats an
    /// unmatched path as EXPECTED. A null path is therefore indistinguishable from "not in a changed file"
    /// and silently empties the headline output, so every event says explicitly how - or whether - its path
    /// was resolved.
    /// </remarks>
    public static class SourceResolution
    {
        /// <summary>From the member's own first non-hidden sequence point. Line is exact.</summary>
        public const string SequencePoints = "sequencePoints";

        /// <summary>From the generated state machine's MoveNext, for an async or iterator kickoff. Line is exact.</summary>
        public const string StateMachine = "stateMachine";

        /// <summary>
        /// From a sibling member of the declaring type, because this member has no sequence points -
        /// an implicit constructor, typically. File is correct, line is unknown and reported as 0.
        /// </summary>
        public const string DeclaringType = "declaringType";

        /// <summary>No portable PDB was available for the assembly.</summary>
        public const string NoPdb = "noPdb";

        /// <summary>A PDB was present but nothing in the declaring type resolved.</summary>
        public const string Unresolved = "unresolved";

        /// <summary>True when the outcome gives the engine no usable file to match against.</summary>
        public static bool IsUsable(string? resolution)
        {
            return resolution == SequencePoints || resolution == StateMachine || resolution == DeclaringType;
        }
    }
}
