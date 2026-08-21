using System.Threading;

namespace BehaviorDiff.Tracer
{
    /// <summary>How a patched method hands back its result, decided once at patch time.</summary>
    internal enum ReturnKind
    {
        Void,
        Sync,
        Task,
        TaskOfT,
        ValueTask,
        ValueTaskOfT,
    }

    /// <summary>
    /// Everything about a patched method that never changes, resolved once at patch time so the
    /// per-call hot path does no reflection and no PDB work.
    /// </summary>
    internal sealed class MethodTraceInfo
    {
        internal MethodTraceInfo(
            string fullName,
            string? filePath,
            int line,
            string sourceResolution,
            ReturnKind returnKind,
            string[] parameterNames,
            AssemblyCoverage coverage,
            bool isTestRoot)
        {
            FullName = fullName;
            FilePath = filePath;
            Line = line;
            SourceResolution = sourceResolution;
            ReturnKind = returnKind;
            ParameterNames = parameterNames;
            Coverage = coverage;
            IsTestRoot = isTestRoot;
        }

        internal string FullName { get; }

        internal string? FilePath { get; }

        internal int Line { get; }

        internal string SourceResolution { get; }

        internal ReturnKind ReturnKind { get; }

        internal string[] ParameterNames { get; }

        /// <summary>Provenance of the declaring assembly, so calls arriving mid-patch can be counted.</summary>
        internal AssemblyCoverage Coverage { get; }

        /// <summary>Carries a test attribute, so a call to it opens a test's extent.</summary>
        internal bool IsTestRoot { get; }
    }

    /// <summary>
    /// One in-flight call. Instances are handed prefix -> postfix -> finalizer through Harmony's
    /// <c>__state</c>, and captured by the continuation for async methods.
    /// </summary>
    internal sealed class CallFrame
    {
        private int _emitted;

        internal CallFrame(long callId, int ordinal, CallFrame? parent, MethodTraceInfo info, string testId, DigestResult? args, int threadId)
        {
            CallId = callId;
            Ordinal = ordinal;
            Parent = parent;
            Depth = parent is null ? 0 : parent.Depth + 1;
            Info = info;
            TestId = testId;
            Args = args;
            ThreadId = threadId;
        }

        internal long CallId { get; }

        internal int Ordinal { get; }

        internal CallFrame? Parent { get; }

        internal int Depth { get; }

        internal MethodTraceInfo Info { get; }

        internal string TestId { get; }

        internal DigestResult? Args { get; }

        internal int ThreadId { get; }

        /// <summary>Set by the postfix for async methods so the finalizer knows the continuation owns emission.</summary>
        internal bool DeferredToContinuation { get; set; }

        /// <summary>This frame opened the current test's extent and must close it again on exit.</summary>
        internal bool OwnsTestId { get; set; }

        /// <summary>The test id in force before this frame opened one, restored when it exits.</summary>
        internal string? PreviousTestId { get; set; }

        /// <summary>Exactly one of postfix / finalizer / continuation gets to emit this frame.</summary>
        internal bool TryClaimEmit()
        {
            return Interlocked.Exchange(ref _emitted, 1) == 0;
        }
    }
}
