using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace BehaviorDiff.Cli
{
    internal sealed class ProcessResult
    {
        internal int ExitCode { get; init; }

        internal string Output { get; init; } = string.Empty;

        internal bool Ok => ExitCode == 0;
    }

    internal static class Shell
    {
        internal static ProcessResult Run(
            string fileName,
            IEnumerable<string> arguments,
            string workingDirectory,
            IDictionary<string, string>? environment = null,
            bool echo = false)
        {
            var info = new ProcessStartInfo(fileName)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (string argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            if (environment != null)
            {
                foreach (KeyValuePair<string, string> pair in environment)
                {
                    info.Environment[pair.Key] = pair.Value;
                }
            }

            var output = new StringBuilder();
            using var process = new Process { StartInfo = info };
            process.OutputDataReceived += (_, e) => Append(output, e.Data, echo);
            process.ErrorDataReceived += (_, e) => Append(output, e.Data, echo);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();

            return new ProcessResult { ExitCode = process.ExitCode, Output = output.ToString() };
        }

        private static void Append(StringBuilder builder, string? line, bool echo)
        {
            if (line is null)
            {
                return;
            }

            lock (builder)
            {
                builder.AppendLine(line);
            }

            if (echo)
            {
                Console.WriteLine("    " + line);
            }
        }

        internal static string Git(string repo, params string[] arguments)
        {
            ProcessResult result = Run("git", arguments, repo);
            if (!result.Ok)
            {
                throw new CliException("git " + string.Join(' ', arguments) + " failed:" + Environment.NewLine + result.Output);
            }

            return result.Output.Trim();
        }

        /// <summary>Last few lines of a build or test log, which is where the actual error usually is.</summary>
        internal static string Tail(string text, int lines)
        {
            string[] all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)).Select(l => "    " + l.TrimEnd()));
        }
    }

    internal sealed class CliException : Exception
    {
        internal CliException(string message, int exitCode = ExitCodes.BuildOrTestFailure)
            : base(message)
        {
            ExitCode = exitCode;
        }

        internal int ExitCode { get; }
    }

    internal static class ExitCodes
    {
        internal const int NoUnexpected = 0;
        internal const int UnexpectedFound = 1;
        internal const int RunInvalid = 3;
        internal const int BuildOrTestFailure = 4;

        /// <summary>The repository fails to build before instrumentation, so nothing here is our doing.</summary>
        internal const int RepoDoesNotBuild = 5;
    }
}
