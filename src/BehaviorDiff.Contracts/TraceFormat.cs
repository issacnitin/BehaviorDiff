namespace BehaviorDiff.Contracts
{
    /// <summary>Language-independent constants for trace format version 1.</summary>
    public static class TraceFormat
    {
        public const string Schema = "behaviordiff.trace/1";

        public const string DotNetLanguage = "dotnet";

        public const string JavaLanguage = "java";

        public const string NodeLanguage = "node";

        public const string GoLanguage = "go";

        public const string RustLanguage = "rust";
    }

    /// <summary>Language-neutral reasons a discovered member was not instrumented.</summary>
    public static class NeutralSkipReason
    {
        public const string Unobservable = "Unobservable";

        public const string CompilerGenerated = "CompilerGenerated";

        public const string ExcludedByScope = "ExcludedByScope";

        public const string UnsupportedShape = "UnsupportedShape";

        public const string DeclaredExternally = "DeclaredExternally";
    }
}