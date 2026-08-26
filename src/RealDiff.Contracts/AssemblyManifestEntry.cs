namespace RealDiff.Contracts
{
    /// <summary>
    /// How the tracer first saw an assembly. Single-valued since the runtime patcher was removed; kept as an
    /// enum so the wire format stays self-describing and TryParseLine can reject the retired values.
    /// </summary>
    public enum AssemblyDiscovery
    {
        /// <summary>
        /// Instrumented at build time by the weaver. No patcher ran, so there is no discovery moment and no
        /// window between load and instrumentation: every call in the process was observable.
        /// </summary>
        BuildTimeWeave,

        /// <summary>Java bytecode transformed by a java.lang.instrument agent during class definition.</summary>
        JavaAgentTransform,

        /// <summary>JavaScript or TypeScript transformed through a CommonJS or ESM loader hook.</summary>
        NodeAstTransform,

        /// <summary>Go source transformed through the module-aware AST rewriter.</summary>
        GoAstRewrite,

        /// <summary>Rust source transformed into a content-addressed Cargo build cache.</summary>
        RustAstRewrite,
    }

    /// <summary>
    /// Per-assembly provenance: when the tracer saw it, when it finished patching it, and how much of it
    /// ended up observable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Assembly load order is not guaranteed to match between two runs of the same suite. If an assembly
    /// is patched on time in one run and late in the other, calls appear in one trace and not the other,
    /// which looks exactly like a behavior change but is a tracer artifact. Recording provenance lets the
    /// engine degrade those divergences to unverified rather than reporting them, the same way it degrades
    /// a frontier whose descendants were skipped.
    /// </para>
    /// <para>
    /// <b>Calls that happen before a member is patched are unobservable by construction.</b> An unpatched
    /// member runs no tracer code, so nothing counts it. No field here reports how many calls were missed,
    /// because the tracer cannot know. <see cref="TracedCalls"/> is what was seen, not what happened, and a
    /// about that assembly rather than that nothing ran.
    /// </para>
    /// </remarks>
    public sealed class AssemblyManifestEntry
    {
        /// <summary>Simple assembly name.</summary>
        public string Assembly { get; init; } = string.Empty;

        public AssemblyDiscovery Discovery { get; init; }

        /// <summary>The tracer enumerated this assembly's types. Says nothing about whether anything matched.</summary>
        public bool Scanned { get; init; }

        /// <summary>At least one member was actually patched. Strictly stronger than <see cref="Scanned"/>.</summary>
        public bool Instrumented { get; init; }

        /// <summary>Members successfully patched in this assembly.</summary>
        public int PatchedMembers { get; init; }

        /// <summary>All members considered in this module.</summary>
        public int DiscoveredMembers { get; init; }

        /// <summary>Members deliberately not instrumented.</summary>
        public int SkippedMembers { get; init; }

        /// <summary>
        /// Members selected for patching where the patch threw or did not register. Non-zero alongside a
        /// zero <see cref="PatchedMembers"/> means the tracer failed outright on this assembly, which is
        /// neither a coverage gap nor a clean result.
        /// </summary>
        public int PatchFailedMembers { get; init; }

        /// <summary>Milliseconds from tracer start to the assembly being queued for patching.</summary>
        public long QueuedAtMs { get; init; }

        /// <summary>Milliseconds from tracer start to patching completing. Null if it never completed.</summary>
        public long? PatchedAtMs { get; init; }

        /// <summary>
        /// Patched after startup enumeration had finished, so target code could already have been running.
        /// The engine treats divergences involving these assemblies as unverified.
        /// </summary>

        /// <summary>
        /// Trace events emitted from members of this assembly over the whole run. A count of what was
        /// observed; it is not a measure of what was missed.
        /// </summary>
        public long TracedCalls { get; init; }

        /// <summary>
        /// patching completing cannot be seen. Absent otherwise, meaning the assembly was patched during
        /// startup enumeration. Absence is not a claim that no calls were missed, only that the unobserved
        /// window closed before the drain thread started.
        /// </summary>

        /// <summary>
        /// This assembly references a test framework, so everything it declares is harness code.
        /// </summary>
        public bool IsTestAssembly { get; init; }

        /// <summary>
        /// The referenced assembly name that triggered <see cref="IsTestAssembly"/>, e.g. "xunit.core".
        /// Recorded so a misclassification is inspectable rather than silent: a production assembly that
        /// happens to ship test helpers will show up here with the reference that caused it.
        /// </summary>
        public string? TestFrameworkReference { get; init; }

        /// <summary>Members whose source line came from real sequence points, exactly or via a state machine.</summary>
        public int MembersWithExactSource { get; init; }

        /// <summary>
        /// Instrumented, but no member resolved a real source line - a DebugType=full or PDB-less assembly.
        /// </summary>
        /// <remarks>
        /// This does not degrade the output, it inverts it. The engine attributes a divergence by matching
        /// FilePath against the changed-file set and classifies an unmatched path as EXPECTED, so an
        /// assembly with no resolvable source reports "no unexpected behavior changes" for code that was
        /// never analysable - the same failure class as an empty divergence list because zero tests ran.
        /// A run touching such an assembly must fail rather than report. Remedy: build it with
        /// <c>&lt;DebugType&gt;portable&lt;/DebugType&gt;</c>.
        /// </remarks>
        public bool SourceUnavailable { get; init; }

        /// <summary>Members with a real source line, as a percentage of patched members.</summary>
        public int ExactSourcePercent { get; init; }

        /// <summary>
        /// Which rule produced <see cref="SourceUnavailable"/>: see <see cref="SourceRule"/>. Present so a
        /// verdict reached on three members is never read as a percentage of a real population.
        /// </summary>
        public string SourceRuleApplied { get; init; } = SourceRule.NotApplicable;

        /// <summary>
        /// Most but not all members have attributable source. Divergences on the members that lack it are
        /// degraded to frontier_unverified rather than reported.
        /// </summary>
        public bool SourcePartial { get; init; }

        /// <summary>Why the assembly was not scanned, or what went wrong.</summary>
        public string? Detail { get; init; }
    }
}
