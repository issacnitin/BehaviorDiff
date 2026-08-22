namespace BehaviorDiff.Tracer
{
    /// <summary>Entry point from the tracing hot path into <see cref="StructuralDigest"/>.</summary>
    /// <remarks>
    /// The previous implementation walked IEnumerable arguments with foreach, calling GetEnumerator and
    /// MoveNext on the target's own types. That executed arbitrary code inside the process under test -
    /// the exact hazard cited for not calling ToString - so collections now go through curated shape
    /// rules that read fields only.
    /// </remarks>
    internal static class ValueRenderer
    {
        private const int MaxDepth = 6;
        private const int MaxElements = 16;

        internal static DigestResult? RenderArguments(
            string[] parameterNames,
            object[]? args,
            int canonicalCap,
            RedactionPolicy redaction)
        {
            if (args is null || args.Length == 0)
            {
                return null;
            }

            return StructuralDigest.ComputeArguments(
                parameterNames,
                args,
                new DigestOptions(MaxDepth, canonicalCap, MaxElements, redaction));
        }

        internal static DigestResult RenderValue(object? value, int canonicalCap, RedactionPolicy redaction)
        {
            return StructuralDigest.ComputeValue(value, new DigestOptions(MaxDepth, canonicalCap, MaxElements, redaction));
        }
    }
}
