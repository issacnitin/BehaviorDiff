using System.Globalization;

namespace BehaviorDiff.Contracts
{
    /// <summary>
    /// One completed method call observed by the Tracer.
    /// </summary>
    /// <remarks>
    /// Because a single event carries both <see cref="ArgsDigest"/> (known at entry) and
    /// <see cref="ReturnDigest"/>/<see cref="ExceptionType"/> (known at exit), an event is emitted
    /// when the call <em>completes</em>. Calls that never complete (process kill, hang, stack overflow)
    /// therefore leave no record.
    /// </remarks>
    public sealed class TraceEvent
    {
        /// <summary>Stable identity of the test that was executing, e.g. <c>Some.Namespace.MyTests.Works</c>.</summary>
        public string TestId { get; init; } = string.Empty;

        /// <summary>Fully qualified method, e.g. <c>Ns.Type+Nested.Method(System.Int32)</c>.</summary>
        public string MethodFullName { get; init; } = string.Empty;

        /// <summary>Source file the method was declared in, or <see langword="null"/> when unresolvable.</summary>
        public string? FilePath { get; init; }

        /// <summary>
        /// How <see cref="FilePath"/> was reached; see <see cref="SourceResolution"/>. Always populated,
        /// so an unresolved path is explicit rather than a null the engine would read as "unchanged file".
        /// </summary>
        public string FilePathResolution { get; init; } = SourceResolution.Unresolved;

        /// <summary>1-based source line, or 0 when unknown.</summary>
        public int Line { get; init; }

        /// <summary>Depth of this call within its thread's traced call stack; roots are 0.</summary>
        public int CallDepth { get; init; }

        /// <summary>The <see cref="CallId"/> of the calling frame, or <see langword="null"/> for a root call.</summary>
        public long? ParentCallId { get; init; }

        /// <summary>Identifier of this call, unique within a single trace file.</summary>
        public long CallId { get; init; }

        /// <summary>Zero-based entry order within this event's (TestId, MethodFullName) key.</summary>
        public int Ordinal { get; init; }

        /// <summary>Hash of the canonical rendering of the arguments, or null when not captured.</summary>
        public string? ArgsDigest { get; init; }

        /// <summary>
        /// Canonical text the <see cref="ArgsDigest"/> hash was computed from, capped for readability.
        /// The hash is always taken over the full text, so a value differing only past the cap still
        /// produces a different hash.
        /// </summary>
        public string? ArgsRendered { get; init; }

        /// <summary>Hash of the canonical rendering of the return value, or null for void/throwing calls.</summary>
        public string? ReturnDigest { get; init; }

        /// <summary>Canonical text the <see cref="ReturnDigest"/> hash was computed from.</summary>
        public string? ReturnRendered { get; init; }

        /// <summary>Fully qualified type of the escaping exception, or <see langword="null"/> if the call returned normally.</summary>
        public string? ExceptionType { get; init; }

        /// <summary>Managed thread id the call ran on.</summary>
        public int ThreadId { get; init; }

        /// <summary>
        /// The declaring assembly is a test assembly, so this call is harness rather than subject.
        /// </summary>
        /// <remarks>
        /// Harness events stay in the trace because call-tree reconstruction needs them as roots, but they
        /// are excluded from frontier candidacy outright, at any depth and regardless of TestId. Derived
        /// from whether the declaring assembly references a test framework, not from naming, depth, or the
        /// absence of a TestId - all of which are proxies that fail in one direction or the other.
        /// </remarks>
        public bool IsHarness { get; init; }

        /// <inheritdoc />
        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "#{0} d{1} t{2} {3} {4}",
                CallId,
                CallDepth,
                ThreadId,
                MethodFullName,
                ExceptionType is null ? "-> " + (ReturnDigest ?? "void") : "throws " + ExceptionType);
        }
    }
}
