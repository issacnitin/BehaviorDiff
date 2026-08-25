using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BehaviorDiff.Contracts;

namespace BehaviorDiff.Engine
{
    internal static class Program
    {
        private const int ExitOk = 0;
        private const int ExitMalformedTrace = 1;
        private const int ExitUsage = 2;

        internal static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return ExitUsage;
            }

            try
            {
                switch (args[0])
                {
                    case "read":
                        return Read(args);
                    case "normalize":
                        return Normalize(args);
                    case "diff":
                        return Diff(args);
                    case "frontier":
                        return Frontier(args);
                    case "findings":
                        return Findings(args);
                    case "findings-invalid":
                        return InvalidFindings(args);
                    case "semantic-hash":
                        return SemanticHash(args);
                    case "--help":
                    case "-h":
                        PrintUsage();
                        return ExitOk;
                    default:
                        Console.Error.WriteLine("Unknown command '" + args[0] + "'.");
                        PrintUsage();
                        return ExitUsage;
                }
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine("I/O error: " + ex.Message);
                return ExitUsage;
            }
            catch (DiffInputException ex)
            {
                Console.Error.WriteLine("Input error: " + ex.Message);
                return ExitUsage;
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine("Access denied: " + ex.Message);
                return ExitUsage;
            }
        }

        private static int Findings(string[] args)
        {
            string divergenceSet = string.Empty;
            string frontierReport = string.Empty;
            string output = string.Empty;
            string baseSha = string.Empty;
            string prSha = string.Empty;
            string mergeBaseSha = string.Empty;
            int exitCode = 0;
            bool strict = false;

            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--strict")
                {
                    strict = true;
                    continue;
                }

                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("Missing value for " + args[i]);
                    return ExitUsage;
                }

                switch (args[i])
                {
                    case "--divergences": divergenceSet = args[++i]; break;
                    case "--frontier": frontierReport = args[++i]; break;
                    case "--out": output = args[++i]; break;
                    case "--exit-code": exitCode = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--base-sha": baseSha = args[++i]; break;
                    case "--pr-sha": prSha = args[++i]; break;
                    case "--merge-base": mergeBaseSha = args[++i]; break;
                    default:
                        Console.Error.WriteLine("Unknown option " + args[i]);
                        return ExitUsage;
                }
            }

            if (divergenceSet.Length == 0 || frontierReport.Length == 0 || output.Length == 0)
            {
                Console.Error.WriteLine("usage: findings --divergences <set.json> --frontier <report.json> --out <findings.json>");
                return ExitUsage;
            }

            FindingsCommand.WriteAnalyzed(
                divergenceSet,
                frontierReport,
                output,
                exitCode,
                baseSha,
                prSha,
                mergeBaseSha,
                strictCommentPolicy: strict);
            return ExitOk;
        }

        private static int SemanticHash(string[] args)
        {
            string input = string.Empty;
            bool topLevel = false;
            string extract = string.Empty;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--top-level")
                {
                    topLevel = true;
                    continue;
                }

                if (args[i] == "--extract" && i + 1 < args.Length)
                {
                    extract = args[++i];
                    continue;
                }

                if (args[i] == "--in" && i + 1 < args.Length)
                {
                    input = args[++i];
                }
                else
                {
                    Console.Error.WriteLine("usage: semantic-hash --in <artifact.json>");
                    return ExitUsage;
                }
            }

            if (input.Length == 0)
            {
                Console.Error.WriteLine("usage: semantic-hash --in <artifact.json>");
                return ExitUsage;
            }

            if (extract.Length > 0)
            {
                Console.WriteLine(JsonSemanticHash.ExtractTopLevel(input, extract));
            }
            else if (topLevel)
            {
                foreach (KeyValuePair<string, string> property in JsonSemanticHash.ComputeTopLevel(input))
                {
                    Console.WriteLine(property.Key + "=" + property.Value);
                }
            }
            else
            {
                Console.WriteLine(JsonSemanticHash.Compute(input));
            }
            return ExitOk;
        }

        private static int InvalidFindings(string[] args)
        {
            string output = string.Empty;
            string status = string.Empty;
            string reason = string.Empty;
            string baseSha = string.Empty;
            string prSha = string.Empty;
            string mergeBaseSha = string.Empty;
            int exitCode = 0;

            for (int i = 1; i < args.Length; i++)
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("Missing value for " + args[i]);
                    return ExitUsage;
                }

                switch (args[i])
                {
                    case "--out": output = args[++i]; break;
                    case "--status": status = args[++i]; break;
                    case "--reason": reason = args[++i]; break;
                    case "--exit-code": exitCode = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--base-sha": baseSha = args[++i]; break;
                    case "--pr-sha": prSha = args[++i]; break;
                    case "--merge-base": mergeBaseSha = args[++i]; break;
                    default:
                        Console.Error.WriteLine("Unknown option " + args[i]);
                        return ExitUsage;
                }
            }

            if (output.Length == 0 || status.Length == 0 || reason.Length == 0)
            {
                Console.Error.WriteLine("usage: findings-invalid --status <refused|failed> --exit-code <code> --reason <text> --out <findings.json>");
                return ExitUsage;
            }

            FindingsCommand.WriteInvalid(output, status, exitCode, reason, baseSha, prSha, mergeBaseSha);
            return ExitOk;
        }

        private static int Frontier(string[] args)
        {
            var options = new FrontierOptions();
            for (int i = 1; i < args.Length; i++)
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("Missing value for " + args[i]);
                    return ExitUsage;
                }

                switch (args[i])
                {
                    case "--in": options.Input = args[++i]; break;
                    case "--changed-files": options.ChangedFiles = args[++i]; break;
                    case "--out": options.Output = args[++i]; break;
                    default:
                        Console.Error.WriteLine("Unknown option " + args[i]);
                        return ExitUsage;
                }
            }

            if (options.Input.Length == 0 || options.Output.Length == 0)
            {
                Console.Error.WriteLine("usage: frontier --in <divergence-set.json> --changed-files <list.txt> --out <report.json>");
                Console.Error.WriteLine("       <list.txt> is one repo-relative path per line, e.g. from git diff --name-only base..pr");
                return ExitUsage;
            }

            return FrontierCommand.Run(options);
        }

        private static int Diff(string[] args)
        {
            var options = new DiffOptions();
            for (int i = 1; i < args.Length; i++)
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("Missing value for " + args[i]);
                    return ExitUsage;
                }

                switch (args[i])
                {
                    case "--base1": options.Base1 = args[++i]; break;
                    case "--base2": options.Base2 = args[++i]; break;
                    case "--base3": options.Base3 = args[++i]; break;
                    case "--changed-files": options.ChangedFiles = args[++i]; break;
                    case "--pr": options.Pr = args[++i]; break;
                    case "--base-root": options.BaseRoot = args[++i]; break;
                    case "--pr-root": options.PrRoot = args[++i]; break;
                    case "--out": options.Output = args[++i]; break;
                    default:
                        Console.Error.WriteLine("Unknown option " + args[i]);
                        return ExitUsage;
                }
            }

            if (options.Base1.Length == 0 || options.Base2.Length == 0 || options.Pr.Length == 0 || options.Output.Length == 0)
            {
                Console.Error.WriteLine("usage: diff --base1 <dir> --base2 <dir> --pr <dir> --out <file.json>");
                Console.Error.WriteLine("            [--base3 <dir>] [--base-root <path>] [--pr-root <path>]");
                return ExitUsage;
            }

            return DiffCommand.Run(options);
        }

        private static int Read(string[] args)
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("usage: read <trace.ndjson>");
                return ExitUsage;
            }

            string path = args[1];
            if (!File.Exists(path))
            {
                Console.Error.WriteLine("No such file: " + path);
                return ExitUsage;
            }

            long total = 0;
            long valid = 0;
            long withException = 0;
            long roots = 0;
            int maxDepth = 0;
            var tests = new HashSet<string>(StringComparer.Ordinal);
            var methods = new HashSet<string>(StringComparer.Ordinal);
            var threads = new HashSet<int>();
            var callIds = new HashSet<long>();
            long duplicateCallIds = 0;
            var failures = new List<TraceLineResult>();

            foreach (TraceLineResult result in NdjsonTraceReader.ReadWithDiagnostics(path))
            {
                total++;

                if (!result.IsValid)
                {
                    if (failures.Count < 10)
                    {
                        failures.Add(result);
                    }

                    continue;
                }

                TraceEvent traceEvent = result.Event!;
                valid++;
                tests.Add(traceEvent.TestId);
                methods.Add(traceEvent.MethodFullName);
                threads.Add(traceEvent.ThreadId);

                if (!callIds.Add(traceEvent.CallId))
                {
                    duplicateCallIds++;
                }

                if (traceEvent.ExceptionType != null)
                {
                    withException++;
                }

                if (traceEvent.ParentCallId is null)
                {
                    roots++;
                }

                if (traceEvent.CallDepth > maxDepth)
                {
                    maxDepth = traceEvent.CallDepth;
                }
            }

            long malformed = total - valid;

            Console.WriteLine("file              : " + Path.GetFullPath(path));
            Console.WriteLine("bytes             : " + new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("non-blank lines   : " + total.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("events parsed     : " + valid.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("malformed lines   : " + malformed.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("distinct tests    : " + tests.Count.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("distinct methods  : " + methods.Count.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("distinct threads  : " + threads.Count.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("root calls        : " + roots.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("max call depth    : " + maxDepth.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("threw             : " + withException.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("duplicate callIds : " + duplicateCallIds.ToString(CultureInfo.InvariantCulture));

            if (failures.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("first " + failures.Count.ToString(CultureInfo.InvariantCulture) + " malformed line(s):");
                foreach (TraceLineResult failure in failures)
                {
                    Console.WriteLine("  line " + failure.LineNumber.ToString(CultureInfo.InvariantCulture) + ": " + failure.Error);
                    Console.WriteLine("    " + Truncate(failure.RawLine ?? string.Empty, 160));
                }
            }

            return malformed == 0 ? ExitOk : ExitMalformedTrace;
        }

        private static int Normalize(string[] args)
        {
            string? input = null;
            string? output = null;
            bool force = false;

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-o":
                    case "--out":
                        if (i + 1 >= args.Length)
                        {
                            Console.Error.WriteLine("Missing value for " + args[i] + ".");
                            return ExitUsage;
                        }

                        output = args[++i];
                        break;
                    case "--force":
                        force = true;
                        break;
                    default:
                        if (input != null)
                        {
                            Console.Error.WriteLine("Unexpected argument '" + args[i] + "'.");
                            return ExitUsage;
                        }

                        input = args[i];
                        break;
                }
            }

            if (input is null || output is null)
            {
                Console.Error.WriteLine("usage: normalize <trace.ndjson> -o <out.ndjson> [--force]");
                return ExitUsage;
            }

            if (!File.Exists(input))
            {
                Console.Error.WriteLine("No such file: " + input);
                return ExitUsage;
            }

            string fullInput = Path.GetFullPath(input);
            string fullOutput = Path.GetFullPath(output);

            if (string.Equals(fullInput, fullOutput, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Input and output must differ.");
                return ExitUsage;
            }

            if (File.Exists(fullOutput))
            {
                if (!force)
                {
                    Console.Error.WriteLine(fullOutput + " already exists; pass --force to overwrite.");
                    return ExitUsage;
                }

                File.Delete(fullOutput);
            }

            long written = 0;
            long skipped = 0;

            using (var writer = new NdjsonTraceWriter(fullOutput))
            {
                foreach (TraceLineResult result in NdjsonTraceReader.ReadWithDiagnostics(fullInput))
                {
                    if (!result.IsValid)
                    {
                        skipped++;
                        Console.Error.WriteLine("skipping line " + result.LineNumber.ToString(CultureInfo.InvariantCulture) + ": " + result.Error);
                        continue;
                    }

                    writer.Write(result.Event!);
                    written++;
                }
            }

            Console.WriteLine("wrote " + written.ToString(CultureInfo.InvariantCulture) + " event(s) to " + fullOutput);
            if (skipped > 0)
            {
                Console.WriteLine("skipped " + skipped.ToString(CultureInfo.InvariantCulture) + " malformed line(s)");
            }

            return skipped == 0 ? ExitOk : ExitMalformedTrace;
        }

        private static string Truncate(string value, int max)
        {
            return value.Length <= max ? value : value.Substring(0, max) + "\u2026";
        }

        private static void PrintUsage()
        {
            Console.WriteLine("BehaviorDiff.Engine");
            Console.WriteLine();
            Console.WriteLine("  read <trace.ndjson>");
            Console.WriteLine("      Parse a trace and print a summary. Exit 1 if any line is malformed.");
            Console.WriteLine();
            Console.WriteLine("  normalize <trace.ndjson> -o <out.ndjson> [--force]");
            Console.WriteLine("      Re-emit a trace in canonical field order. Exit 1 if any line was skipped.");
        }
    }
}
