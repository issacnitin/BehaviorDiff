using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using BehaviorDiff.Engine;

namespace BehaviorDiff.Cli
{
    internal enum AnalysisEngine
    {
        CSharp,
        Rust,
    }

    internal static class EngineDispatch
    {
        internal static bool TryParse(string value, out AnalysisEngine engine)
        {
            switch (value.ToLowerInvariant())
            {
                case "csharp": engine = AnalysisEngine.CSharp; return true;
                case "rust": engine = AnalysisEngine.Rust; return true;
                default: engine = AnalysisEngine.CSharp; return false;
            }
        }

        internal static string Name(AnalysisEngine engine) => engine switch
        {
            AnalysisEngine.CSharp => "csharp",
            AnalysisEngine.Rust => "rust",
            _ => throw new ArgumentOutOfRangeException(nameof(engine)),
        };

        internal static int RunDiff(AnalysisEngine engine, DiffOptions options)
        {
            if (engine == AnalysisEngine.CSharp)
            {
                return DiffCommand.Run(options);
            }

            string executable = ResolveRustEngine();
            ProcessResult result = Shell.Run(
                executable,
                Arguments(options),
                Environment.CurrentDirectory);
            Console.Write(result.Output);

            if (result.ExitCode == 2)
            {
                const string prefix = "Input error: ";
                string? inputError = Lines(result.Output).SingleOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal));
                throw new DiffInputException(inputError is null
                    ? "The Rust diff engine rejected its input without a structured error."
                    : inputError.Substring(prefix.Length));
            }

            if (result.ExitCode == 4)
            {
                string[] reasons = Lines(result.Output)
                    .Where(line => line.StartsWith("  - ", StringComparison.Ordinal))
                    .Select(line => line.Substring(4))
                    .ToArray();
                options.RefusalReason = reasons.Length == 0
                    ? "The Rust comparison was refused before a DivergenceSet was produced."
                    : string.Join(Environment.NewLine, reasons);
            }
            else if (result.ExitCode != 0)
            {
                throw new CliException(
                    "The Rust diff engine exited " + result.ExitCode + "." + Environment.NewLine + Shell.Tail(result.Output, 20));
            }

            return result.ExitCode;
        }

        internal static int RunFrontier(AnalysisEngine engine, FrontierOptions options)
        {
            if (engine == AnalysisEngine.CSharp)
            {
                return FrontierCommand.Run(options);
            }

            ProcessResult result = Shell.Run(
                ResolveRustEngine(),
                FrontierArguments(options),
                Environment.CurrentDirectory);
            Console.Write(result.Output);
            if (result.ExitCode == 0)
            {
                return 0;
            }

            if (result.ExitCode == 2)
            {
                options.RefusalReason = InputError(result.Output)
                    ?? "The Rust frontier analysis was refused before a report was produced.";
                return ExitCodes.RunInvalid;
            }

            throw new CliException(
                "The Rust frontier engine exited " + result.ExitCode + "." + Environment.NewLine
                + Shell.Tail(result.Output, 20));
        }

        private static IEnumerable<string> Arguments(DiffOptions options)
        {
            yield return "stream-diff";
            yield return "--base1";
            yield return options.Base1;
            yield return "--base2";
            yield return options.Base2;
            if (options.Base3.Length > 0)
            {
                yield return "--base3";
                yield return options.Base3;
            }

            yield return "--pr";
            yield return options.Pr;
            if (!string.IsNullOrWhiteSpace(options.BaseRoot))
            {
                yield return "--base-root";
                yield return options.BaseRoot;
            }

            if (!string.IsNullOrWhiteSpace(options.PrRoot))
            {
                yield return "--pr-root";
                yield return options.PrRoot;
            }

            if (options.ChangedFiles.Length > 0)
            {
                yield return "--changed-files";
                yield return options.ChangedFiles;
            }

            yield return "--out";
            yield return options.Output;
        }

        private static IEnumerable<string> FrontierArguments(FrontierOptions options)
        {
            yield return "frontier";
            yield return "--in";
            yield return options.Input;
            yield return "--changed-files";
            yield return options.ChangedFiles;
            yield return "--out";
            yield return options.Output;
        }

        private static string? InputError(string output)
        {
            const string prefix = "Input error: ";
            int index = output.LastIndexOf(prefix, StringComparison.Ordinal);
            return index < 0 ? null : output.Substring(index + prefix.Length).Trim();
        }

        private static string ResolveRustEngine()
        {
            string? configured = Environment.GetEnvironmentVariable("BEHAVIORDIFF_RUST_ENGINE");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                string fullPath = Path.GetFullPath(configured);
                if (!File.Exists(fullPath))
                {
                    throw new CliException("BEHAVIORDIFF_RUST_ENGINE does not name an existing executable: " + fullPath);
                }

                return EnsureExecutable(fullPath);
            }

            string fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "behaviordiff-engine.exe"
                : "behaviordiff-engine";
            string rid = OperatingSystemName() + "-" + ArchitectureName();
            string packaged = Path.Combine(AppContext.BaseDirectory, "engines", "rust", rid, fileName);
            if (File.Exists(packaged))
            {
                return EnsureExecutable(packaged);
            }

            foreach (string root in CandidateSourceRoots())
            {
                string source = Path.Combine(root, "src", "BehaviorDiff.Engine.Rust", "target", "release", fileName);
                if (File.Exists(source))
                {
                    return EnsureExecutable(source);
                }
            }

            throw new CliException(
                "BehaviorDiff Rust engine was not found. Set BEHAVIORDIFF_RUST_ENGINE, install "
                + packaged + ", or run cargo build --release for src/BehaviorDiff.Engine.Rust.");
        }

        private static IEnumerable<string> CandidateSourceRoots()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            {
                DirectoryInfo? current = new DirectoryInfo(start);
                while (current != null)
                {
                    if (seen.Add(current.FullName))
                    {
                        yield return current.FullName;
                    }

                    current = current.Parent;
                }
            }
        }

        private static string EnsureExecutable(string path)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                UnixFileMode mode = File.GetUnixFileMode(path);
                File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }

            return path;
        }

        private static IEnumerable<string> Lines(string text) =>
            text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        private static string OperatingSystemName()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "win";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "osx";
            throw new CliException("The Rust engine is not packaged for this operating system.");
        }

        private static string ArchitectureName() => RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new CliException("The Rust engine is not packaged for architecture " + RuntimeInformation.OSArchitecture + "."),
        };
    }
}