using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace RealDiff.Tracer
{
    /// <summary>Tracer configuration. Environment driven so no target-repo code has to change.</summary>
    public sealed class TracerOptions
    {
        public const string TracePathVariable = "REALDIFF_TRACE";
        public const string IncludeNamespacesVariable = "REALDIFF_NAMESPACES";
        public const string ExcludeNamespacesVariable = "REALDIFF_EXCLUDE_NAMESPACES";
        public const string TestAttributesVariable = "REALDIFF_TEST_ATTRIBUTES";
        public const string MaxDigestVariable = "REALDIFF_MAX_DIGEST";
        public const string VerboseVariable = "REALDIFF_VERBOSE";
        public const string RedactNamesVariable = "REALDIFF_REDACT_NAMES";
        public const string RedactTypesVariable = "REALDIFF_REDACT_TYPES";
        public const string RedactPathsVariable = "REALDIFF_REDACT_PATHS";

        private static readonly char[] ListSeparators = { ';', ',' };

        private static readonly string[] DefaultTestAttributes =
        {
            "Xunit.FactAttribute",
            "Xunit.TheoryAttribute",
            "NUnit.Framework.TestAttribute",
            "NUnit.Framework.TestCaseAttribute",
            "Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute",
        };

        private static readonly int CurrentProcessId = GetProcessId();

        /// <summary>Base trace path. The real files fold in the process id; see <see cref="ResolveTracePath"/>.</summary>
        public string TracePath { get; set; } = "realdiff.ndjson";

        /// <summary>Namespaces to instrument. A type matches if its namespace equals or starts with a prefix.</summary>
        public IReadOnlyList<string> IncludeNamespacePrefixes { get; set; } = new string[0];

        /// <summary>
        /// Namespaces left alone even though they match an include prefix. Members excluded this way are
        /// still recorded in the manifest, so a configuration difference between two runs shows up as a
        /// tooling gap rather than as a behavior change.
        /// </summary>
        public IReadOnlyList<string> ExcludeNamespacePrefixes { get; set; } = new string[0];

        /// <summary>Attribute type names marking a test entry point, matched by name up the inheritance chain.</summary>
        public IReadOnlyList<string> TestAttributeNames { get; set; } = DefaultTestAttributes;

        /// <summary>
        /// Safety bound on canonical digest text. Not a readability cap: rendered text is capped
        /// separately at <see cref="StructuralDigest.RenderedCap"/>, and the hash is taken over the full
        /// canonical text so two values differing only past the readability cap still hash differently.
        /// </summary>
        public int MaxDigestLength { get; set; } = 65536;

        /// <summary>Queue depth before traced threads block. Blocking is deliberate: dropping events would corrupt a diff.</summary>
        public int QueueCapacity { get; set; } = 100_000;

        /// <summary>Emit per-member patch decisions to the diagnostic log.</summary>
        public bool Verbose { get; set; }

        internal RedactionPolicy Redaction { get; set; } = RedactionPolicy.FromEnvironment();

        /// <summary>True when tracing is configured at all.</summary>
        public bool IsEnabled => IncludeNamespacePrefixes.Count > 0;

        public static TracerOptions FromEnvironment()
        {
            var options = new TracerOptions();

            string? path = Environment.GetEnvironmentVariable(TracePathVariable);
            if (!string.IsNullOrWhiteSpace(path))
            {
                options.TracePath = path!;
            }

            options.IncludeNamespacePrefixes = ReadList(IncludeNamespacesVariable, options.IncludeNamespacePrefixes);
            options.ExcludeNamespacePrefixes = ReadList(ExcludeNamespacesVariable, options.ExcludeNamespacePrefixes);
            options.TestAttributeNames = ReadList(TestAttributesVariable, options.TestAttributeNames);

            string? maxDigest = Environment.GetEnvironmentVariable(MaxDigestVariable);
            if (!string.IsNullOrWhiteSpace(maxDigest)
                && int.TryParse(maxDigest, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                && parsed > 0)
            {
                options.MaxDigestLength = parsed;
            }

            string? verbose = Environment.GetEnvironmentVariable(VerboseVariable);
            options.Verbose = verbose == "1" || string.Equals(verbose, "true", StringComparison.OrdinalIgnoreCase);

            return options;
        }

        /// <summary>
        /// Trace file for this process: <c>run.ndjson</c> becomes <c>run.12345.ndjson</c>. One file per
        /// process, because concurrent appends from several processes are not guaranteed to keep lines
        /// intact; the engine globs and merges by TestId.
        /// </summary>
        public string ResolveTracePath()
        {
            return Decorate(string.Empty);
        }

        /// <summary>Coverage manifest for this process: <c>run.12345.manifest.ndjson</c>.</summary>
        public string ResolveManifestPath()
        {
            return Decorate(".manifest");
        }

        private string Decorate(string suffix)
        {
            string fullPath = Path.GetFullPath(TracePath);
            string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            string name = Path.GetFileNameWithoutExtension(fullPath);
            string extension = Path.GetExtension(fullPath);
            string decorated = name + "." + CurrentProcessId.ToString(CultureInfo.InvariantCulture) + suffix + extension;

            return directory.Length == 0 ? decorated : Path.Combine(directory, decorated);
        }

        private static IReadOnlyList<string> ReadList(string variable, IReadOnlyList<string> fallback)
        {
            string? raw = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            return raw!.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries);
        }

        private static int GetProcessId()
        {
            // Environment.ProcessId is net5.0+; netstandard2.0 has to go through Process.
            using Process current = Process.GetCurrentProcess();
            return current.Id;
        }
    }
}
