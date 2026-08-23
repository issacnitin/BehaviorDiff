using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BehaviorDiff.Cli
{
    internal static class BaselineCommand
    {
        private static readonly ISerializer Yaml = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .DisableAliases()
            .Build();

        internal static int Run(string[] args)
        {
            if (args.Length == 0 || (args[0] != "write" && args[0] != "apply"))
            {
                throw new CliException("baseline requires write or apply.");
            }

            string operation = args[0];
            string? findings = null;
            string? baseline = null;
            string repository = Directory.GetCurrentDirectory();
            int expiryDays = 30;
            bool noExpiry = false;
            for (int index = 1; index < args.Length; index++)
            {
                string argument = args[index];
                switch (argument)
                {
                    case "--findings": findings = Next(args, ref index); break;
                    case "--baseline": baseline = Next(args, ref index); break;
                    case "--output": baseline = Next(args, ref index); break;
                    case "--repo": repository = Next(args, ref index); break;
                    case "--expires": expiryDays = ParseDays(Next(args, ref index)); break;
                    case "--no-expiry": noExpiry = true; break;
                    default: throw new CliException("Unknown baseline option '" + argument + "'.");
                }
            }

            if (findings is null || !File.Exists(findings))
            {
                throw new CliException("baseline " + operation + " requires an existing --findings <findings.json> file.");
            }

            string baselinePath = Path.GetFullPath(baseline
                ?? Path.Combine(repository, ".behaviordiff", "baseline.yml"));
            if (operation == "apply")
            {
                BaselineResult result = BaselinePolicy.Apply(Path.GetFullPath(findings), baselinePath);
                Report(result, baselinePath);
                return result.ExitCode;
            }

            return Write(Path.GetFullPath(findings), baselinePath, noExpiry ? null : expiryDays);
        }

        internal static void Report(BaselineResult result, string path)
        {
            Console.WriteLine("  baseline            : " + path);
            Console.WriteLine("  actionable findings : " + result.ActionableMembers + " member(s), "
                + result.ActionableCallSites + " call site(s)");
            Console.WriteLine("  suppressed          : " + result.SuppressedMembers + " member(s), "
                + result.SuppressedCallSites + " call site(s)");
            Console.WriteLine("  stale / expired     : " + result.StaleEntries + " / " + result.ExpiredEntries);
        }

        private static int Write(string findingsPath, string baselinePath, int? expiryDays)
        {
            using JsonDocument findings = JsonDocument.Parse(File.ReadAllText(findingsPath));
            JsonElement root = findings.RootElement;
            if (Text(root, "schema") != "behaviordiff.findings/1"
                || Text(root, "status") != "analyzed"
                || !root.TryGetProperty("members", out JsonElement members))
            {
                throw new CliException("baseline write requires analyzed behaviordiff.findings/1 input.");
            }

            BaselineDocument baseline = File.Exists(baselinePath)
                ? BaselinePolicy.Read(baselinePath)
                : new BaselineDocument();
            var existing = new HashSet<string>(
                baseline.Acknowledgements.Select(rule => Key(rule.Member, rule.Path)),
                StringComparer.Ordinal);
            var ids = new HashSet<string>(
                baseline.Acknowledgements.Select(rule => rule.Id)
                    .Concat(baseline.IgnorePaths.Select(rule => rule.Id))
                    .Concat(baseline.IgnoreMembers.Select(rule => rule.Id)),
                StringComparer.Ordinal);
            string? expiry = expiryDays is int days
                ? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(days)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : null;
            int added = 0;
            foreach (JsonElement member in members.EnumerateArray().Where(member =>
                Text(member, "attribution") == "unexpected" && !member.TryGetProperty("suppression", out _)))
            {
                string memberName = Text(member, "memberName");
                string? filePath = NullableText(member, "filePath");
                if (!existing.Add(Key(memberName, filePath)))
                {
                    continue;
                }

                baseline.Acknowledgements.Add(new BaselineAcknowledgement
                {
                    Id = UniqueId(ids, memberName, filePath),
                    Member = memberName,
                    Path = filePath,
                    Reason = "Acknowledged from findings.json; review before committing.",
                    Expires = expiry,
                });
                added++;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
            File.WriteAllText(baselinePath, Yaml.Serialize(baseline));
            Console.WriteLine("baseline written: " + baselinePath);
            Console.WriteLine("  acknowledgements added: " + added);
            Console.WriteLine("  acknowledgements total: " + baseline.Acknowledgements.Count);
            Console.WriteLine("  default expiry          : " + (expiry ?? "none"));
            return ExitCodes.NoUnexpected;
        }

        private static string UniqueId(HashSet<string> ids, string member, string? path)
        {
            string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Key(member, path))))
                .ToLowerInvariant().Substring(0, 12);
            string candidate = "ack-" + digest;
            int suffix = 2;
            while (!ids.Add(candidate))
            {
                candidate = "ack-" + digest + "-" + suffix.ToString(CultureInfo.InvariantCulture);
                suffix++;
            }

            return candidate;
        }

        private static string Key(string member, string? path) => member + "\n" + (path ?? string.Empty);

        private static int ParseDays(string value)
        {
            if (value.Length < 2
                || char.ToLowerInvariant(value[value.Length - 1]) != 'd'
                || !int.TryParse(value.Substring(0, value.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int days)
                || days <= 0)
            {
                throw new CliException("--expires must be a positive whole-day duration such as 30d.");
            }

            return days;
        }

        private static string Next(string[] args, ref int index)
        {
            if (index + 1 >= args.Length)
            {
                throw new CliException("Missing value for " + args[index] + ".");
            }

            return args[++index];
        }

        private static string Text(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        private static string? NullableText(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}