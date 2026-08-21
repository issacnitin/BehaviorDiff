namespace BehaviorDiff.Contracts
{
    /// <summary>Outcome of considering one member for patching.</summary>
    public enum PatchStatus
    {
        /// <summary>Instrumented; calls to it produce trace events.</summary>
        Patched,

        /// <summary>Deliberately not instrumented. <see cref="ManifestEntry.SkipReason"/> says why.</summary>
        Skipped,

        /// <summary>Selected for patching, but the patch threw. <see cref="ManifestEntry.Detail"/> has the error.</summary>
        PatchFailed,

        /// <summary>An assembly-level record: its types could not be enumerated, so its members are unknown.</summary>
        EnumerationFailed,
    }

    /// <summary>
    /// One line of the coverage manifest: what the tracer found and whether it is actually observable.
    /// </summary>
    /// <remarks>
    /// This exists so the diff engine can tell "these calls behaved identically" apart from "these calls
    /// were never watched". Without it the frontier rule is unsound: a method whose descendants were all
    /// silent looks like the origin of a change even when the real origin was an untraced descendant.
    /// Two uses downstream:
    /// a method patched in one build and skipped in the other is a tooling gap and must never be reported
    /// as a behavior change; and a diverged node with any skipped descendant is frontier_unverified rather
    /// than frontier.
    /// </remarks>
    public sealed class ManifestEntry
    {
        /// <summary>Simple name of the assembly the member was found in.</summary>
        public string Assembly { get; init; } = string.Empty;

        /// <summary>
        /// Fully qualified member, matching <see cref="TraceEvent.MethodFullName"/> so the engine can join
        /// the two. Null only on an <see cref="PatchStatus.EnumerationFailed"/> record.
        /// </summary>
        public string? MethodFullName { get; init; }

        public PatchStatus Status { get; init; }

        /// <summary>Language-neutral <see cref="NeutralSkipReason"/> value. Null unless skipped.</summary>
        public string? SkipReason { get; init; }

        /// <summary>How the member returns its result: Void, Sync, Task, TaskOfT, ValueTask, ValueTaskOfT.</summary>
        public string? ReturnKind { get; init; }

        /// <summary>
        /// A test entry point. These are traced because the depth-0 roots are needed to rebuild the call
        /// tree, but they are not frontier candidates: every test root would otherwise diverge whenever
        /// anything beneath it changed.
        /// </summary>
        public bool IsTestRoot { get; init; }

        /// <summary>How this member's source path was resolved; see <see cref="SourceResolution"/>.</summary>
        public string? SourceResolution { get; init; }

        /// <summary>Language-specific skip reason, or error text for a failed patch/enumeration.</summary>
        public string? Detail { get; init; }
    }
}
