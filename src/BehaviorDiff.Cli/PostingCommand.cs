using System;
using System.IO;
using System.Text.Json;

namespace BehaviorDiff.Cli
{
    internal static class PostingCommand
    {
        internal static int Run(string[] args)
        {
            string? provider = null;
            string? findingsPath = null;
            string gate = "warn-only";

            for (int i = 0; i < args.Length; i++)
            {
                string argument = args[i];
                if (argument.StartsWith("--provider=", StringComparison.Ordinal))
                {
                    provider = argument.Substring("--provider=".Length);
                }
                else if (argument == "--provider")
                {
                    provider = Next(args, ref i);
                }
                else if (argument.StartsWith("--findings=", StringComparison.Ordinal))
                {
                    findingsPath = argument.Substring("--findings=".Length);
                }
                else if (argument == "--findings")
                {
                    findingsPath = Next(args, ref i);
                }
                else if (argument.StartsWith("--gate=", StringComparison.Ordinal))
                {
                    gate = argument.Substring("--gate=".Length);
                }
                else if (argument == "--gate")
                {
                    gate = Next(args, ref i);
                }
                else
                {
                    throw new CliException("Unknown post option '" + argument + "'.");
                }
            }

            if (provider != "azuredevops" && provider != "github")
            {
                throw new CliException("post requires --provider=azuredevops or --provider=github.");
            }

            if (findingsPath is null || !File.Exists(findingsPath))
            {
                throw new CliException("post requires an existing --findings <findings.json> file.");
            }

            if (gate != "warn-only" && gate != "fail-on-findings")
            {
                throw new CliException("--gate must be warn-only or fail-on-findings.");
            }

            try
            {
                using JsonDocument findings = JsonDocument.Parse(File.ReadAllText(findingsPath));
                JsonElement root = findings.RootElement;
                if (String(root, "schema") != "behaviordiff.findings/1")
                {
                    throw new CliException("Unsupported findings schema '" + String(root, "schema") + "'.");
                }

                string status = String(root, "status");
                if (status != "analyzed" && status != "refused" && status != "failed")
                {
                    throw new CliException("Unknown findings status '" + status + "'.");
                }

                if (provider == "azuredevops")
                {
                    new AzureDevOpsPoster().PostAsync(root).GetAwaiter().GetResult();
                }
                else
                {
                    new GitHubPoster().PostAsync(root).GetAwaiter().GetResult();
                }

                if (status != "analyzed")
                {
                    WriteWarning(provider, "BehaviorDiff could not analyze this PR; see the posted reason.");
                    return ExitCodes.NoUnexpected;
                }

                int unexpected = root.GetProperty("summary").GetProperty("unexpectedMembers").GetInt32();
                if (unexpected == 0)
                {
                    return ExitCodes.NoUnexpected;
                }

                if (gate == "fail-on-findings")
                {
                    Console.Error.WriteLine("BehaviorDiff gate failed: " + unexpected + " unexpected member(s).");
                    return ExitCodes.UnexpectedFound;
                }

                WriteWarning(provider, "BehaviorDiff found " + unexpected
                    + " unexpected member(s); warn-only gate did not fail the build.");
                return ExitCodes.NoUnexpected;
            }
            catch (JsonException ex)
            {
                throw new CliException("findings.json is malformed: " + ex.Message);
            }
        }

        private static string Next(string[] args, ref int index)
        {
            if (index + 1 >= args.Length)
            {
                throw new CliException("Missing value for " + args[index] + ".");
            }

            return args[++index];
        }

        private static string String(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        private static void WriteWarning(string provider, string message)
        {
            if (provider == "azuredevops")
            {
                Console.WriteLine("##vso[task.logissue type=warning]" + message);
            }
            else
            {
                Console.WriteLine("::warning::" + message);
            }
        }
    }
}