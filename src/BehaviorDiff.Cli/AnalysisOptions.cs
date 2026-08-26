using System;

namespace BehaviorDiff.Cli
{
    internal sealed class DiffOptions
    {
        internal string Base1 { get; set; } = string.Empty;

        internal string Base2 { get; set; } = string.Empty;

        internal string Base3 { get; set; } = string.Empty;

        internal string ChangedFiles { get; set; } = string.Empty;

        internal string Pr { get; set; } = string.Empty;

        internal string? BaseRoot { get; set; }

        internal string? PrRoot { get; set; }

        internal string Output { get; set; } = string.Empty;

        internal string? RefusalReason { get; set; }
    }

    internal sealed class FrontierOptions
    {
        internal string Input { get; set; } = string.Empty;

        internal string ChangedFiles { get; set; } = string.Empty;

        internal string Output { get; set; } = string.Empty;

        internal string? RefusalReason { get; set; }
    }

    internal sealed class DiffInputException : Exception
    {
        internal DiffInputException(string message)
            : base(message)
        {
        }
    }
}
