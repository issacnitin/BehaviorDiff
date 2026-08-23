using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BehaviorDiff.Cli
{
    internal sealed class BaselineDocument
    {
        public string Schema { get; set; } = "behaviordiff.baseline/1";

        public List<BaselineAcknowledgement> Acknowledgements { get; set; } = new();

        public List<BaselineIgnore> IgnorePaths { get; set; } = new();

        public List<BaselineIgnore> IgnoreMembers { get; set; } = new();
    }

    internal sealed class BaselineAcknowledgement
    {
        public string Id { get; set; } = string.Empty;

        public string Member { get; set; } = string.Empty;

        public string? Path { get; set; }

        public string Reason { get; set; } = string.Empty;

        public string? Expires { get; set; }
    }

    internal sealed class BaselineIgnore
    {
        public string Id { get; set; } = string.Empty;

        public string Pattern { get; set; } = string.Empty;

        public string Reason { get; set; } = string.Empty;

        public string? Expires { get; set; }
    }

    internal sealed record BaselineResult(
        int ActionableMembers,
        int ActionableCallSites,
        int SuppressedMembers,
        int SuppressedCallSites,
        int StaleEntries,
        int ExpiredEntries)
    {
        internal int ExitCode => ActionableMembers == 0 ? ExitCodes.NoUnexpected : ExitCodes.UnexpectedFound;
    }

    internal static class BaselinePolicy
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        private static readonly IDeserializer Yaml = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        internal static BaselineResult Apply(
            string findingsPath,
            string baselinePath,
            DateTimeOffset? utcNow = null)
        {
            BaselineDocument baseline = Read(baselinePath);
            DateOnly today = DateOnly.FromDateTime((utcNow ?? DateTimeOffset.UtcNow).UtcDateTime);
            List<RuleState> rules = Rules(baseline, today);
            JsonObject root = JsonNode.Parse(File.ReadAllText(findingsPath))?.AsObject()
                ?? throw new CliException("findings.json is empty: " + findingsPath);
            if (!string.Equals(root["schema"]?.GetValue<string>(), "behaviordiff.findings/1", StringComparison.Ordinal)
                || !string.Equals(root["status"]?.GetValue<string>(), "analyzed", StringComparison.Ordinal))
            {
                throw new CliException("A baseline can only be applied to analyzed behaviordiff.findings/1 artifacts.");
            }

            JsonArray members = root["members"]?.AsArray()
                ?? throw new CliException("Analyzed findings have no members array.");
            int actionableMembers = 0;
            int actionableCallSites = 0;
            int suppressedMembers = 0;
            int suppressedCallSites = 0;
            foreach (JsonObject member in members.OfType<JsonObject>())
            {
                if (!string.Equals(Text(member, "attribution"), "unexpected", StringComparison.Ordinal))
                {
                    continue;
                }

                member.Remove("suppression");
                RuleState? match = rules.FirstOrDefault(rule => !rule.Expired && rule.Matches(member));
                int callSites = Number(member, "callSiteCount");
                if (match is null)
                {
                    actionableMembers++;
                    actionableCallSites += callSites;
                    continue;
                }

                match.MatchCount++;
                suppressedMembers++;
                suppressedCallSites += callSites;
                member["suppression"] = new JsonObject
                {
                    ["ruleId"] = match.Id,
                    ["kind"] = match.Kind,
                    ["reason"] = match.Reason,
                    ["expires"] = match.Expires,
                };
            }

            JsonArray stale = new(rules
                .Where(rule => !rule.Expired && rule.MatchCount == 0)
                .Select(rule => (JsonNode)new JsonObject
                {
                    ["ruleId"] = rule.Id,
                    ["kind"] = rule.Kind,
                    ["reason"] = rule.Reason,
                }).ToArray());
            JsonArray expired = new(rules
                .Where(rule => rule.Expired)
                .Select(rule => (JsonNode)new JsonObject
                {
                    ["ruleId"] = rule.Id,
                    ["kind"] = rule.Kind,
                    ["reason"] = rule.Reason,
                    ["expires"] = rule.Expires,
                }).ToArray());

            JsonObject summary = root["summary"]?.AsObject()
                ?? throw new CliException("Analyzed findings have no summary object.");
            summary["actionableUnexpectedMembers"] = actionableMembers;
            summary["actionableUnexpectedCallSites"] = actionableCallSites;
            summary["suppressedMembers"] = suppressedMembers;
            summary["suppressedCallSites"] = suppressedCallSites;
            JsonObject? commentPolicy = root["commentPolicy"] as JsonObject;
            if (commentPolicy is not null)
            {
                bool strict = string.Equals(commentPolicy["mode"]?.GetValue<string>(), "strict", StringComparison.Ordinal);
                JsonObject[] commentEligible = members.OfType<JsonObject>().Where(member =>
                    string.Equals(Text(member, "attribution"), "unexpected", StringComparison.Ordinal)
                    && member["suppression"] is null
                    && (strict || member["defaultCommentEligible"]?.GetValue<bool>() != false)).ToArray();
                int eligibleCallSites = commentEligible.Sum(member => Number(member, "callSiteCount"));
                commentPolicy["eligibleUnexpectedMembers"] = commentEligible.Length;
                commentPolicy["eligibleUnexpectedCallSites"] = eligibleCallSites;
                commentPolicy["suppressedUnexpectedMembers"] = actionableMembers + suppressedMembers - commentEligible.Length;
                commentPolicy["suppressedUnexpectedCallSites"] = actionableCallSites + suppressedCallSites - eligibleCallSites;
            }

            root["baseline"] = new JsonObject
            {
                ["schema"] = baseline.Schema,
                ["path"] = DisplayPath(baselinePath),
                ["suppressedMembers"] = suppressedMembers,
                ["suppressedCallSites"] = suppressedCallSites,
                ["actionableUnexpectedMembers"] = actionableMembers,
                ["actionableUnexpectedCallSites"] = actionableCallSites,
                ["staleEntries"] = stale,
                ["expiredEntries"] = expired,
            };
            root["policyVerdict"] = actionableMembers == 0 ? "clean" : "findings";
            root["policyExitCode"] = actionableMembers == 0 ? ExitCodes.NoUnexpected : ExitCodes.UnexpectedFound;

            File.WriteAllText(findingsPath, root.ToJsonString(Json) + Environment.NewLine);
            return new BaselineResult(
                actionableMembers,
                actionableCallSites,
                suppressedMembers,
                suppressedCallSites,
                stale.Count,
                expired.Count);
        }

        internal static BaselineDocument Read(string path)
        {
            if (!File.Exists(path))
            {
                throw new CliException("Baseline file does not exist: " + path);
            }

            try
            {
                BaselineDocument baseline = Yaml.Deserialize<BaselineDocument>(File.ReadAllText(path))
                    ?? throw new CliException("Baseline file is empty: " + path);
                Validate(baseline);
                return baseline;
            }
            catch (YamlDotNet.Core.YamlException ex)
            {
                throw new CliException("Baseline YAML is malformed: " + ex.Message);
            }
        }

        internal static bool IsSuppressed(JsonElement member) =>
            member.TryGetProperty("suppression", out JsonElement suppression)
            && suppression.ValueKind == JsonValueKind.Object;

        internal static int ActionableUnexpectedMembers(JsonElement summary) =>
            summary.TryGetProperty("actionableUnexpectedMembers", out JsonElement actionable)
            && actionable.ValueKind == JsonValueKind.Number
                ? actionable.GetInt32()
                : summary.GetProperty("unexpectedMembers").GetInt32();

        internal static int ActionableUnexpectedCallSites(JsonElement summary) =>
            summary.TryGetProperty("actionableUnexpectedCallSites", out JsonElement actionable)
            && actionable.ValueKind == JsonValueKind.Number
                ? actionable.GetInt32()
                : summary.GetProperty("unexpectedCallSites").GetInt32();

        private static List<RuleState> Rules(BaselineDocument baseline, DateOnly today)
        {
            var rules = new List<RuleState>();
            rules.AddRange(baseline.Acknowledgements.Select(rule => RuleState.Acknowledgement(rule, today)));
            rules.AddRange(baseline.IgnorePaths.Select(rule => RuleState.Ignore(rule, "path", today)));
            rules.AddRange(baseline.IgnoreMembers.Select(rule => RuleState.Ignore(rule, "member", today)));
            return rules;
        }

        private static void Validate(BaselineDocument baseline)
        {
            if (!string.Equals(baseline.Schema, "behaviordiff.baseline/1", StringComparison.Ordinal))
            {
                throw new CliException("Unsupported baseline schema '" + baseline.Schema + "'.");
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (BaselineAcknowledgement rule in baseline.Acknowledgements)
            {
                Required(rule.Id, "acknowledgement id");
                Required(rule.Member, "acknowledgement member");
                Required(rule.Reason, "acknowledgement reason");
                Unique(ids, rule.Id);
                ParseExpiry(rule.Expires, rule.Id);
            }

            foreach ((BaselineIgnore rule, string kind) in baseline.IgnorePaths.Select(rule => (rule, "path"))
                .Concat(baseline.IgnoreMembers.Select(rule => (rule, "member"))))
            {
                Required(rule.Id, kind + " ignore id");
                Required(rule.Pattern, kind + " ignore pattern");
                Required(rule.Reason, kind + " ignore reason");
                Unique(ids, rule.Id);
                ParseExpiry(rule.Expires, rule.Id);
            }
        }

        private static void Required(string value, string field)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new CliException("Baseline " + field + " is required.");
            }
        }

        private static void Unique(HashSet<string> ids, string id)
        {
            if (!ids.Add(id))
            {
                throw new CliException("Baseline rule id '" + id + "' is duplicated.");
            }
        }

        private static DateOnly? ParseExpiry(string? value, string id)
        {
            if (value is null)
            {
                return null;
            }

            if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly expiry))
            {
                throw new CliException("Baseline rule '" + id + "' expiry must use YYYY-MM-DD.");
            }

            return expiry;
        }

        private static string Text(JsonObject value, string property) =>
            value[property]?.GetValue<string>() ?? string.Empty;

        private static int Number(JsonObject value, string property) =>
            value[property]?.GetValue<int>() ?? 0;

        private static string DisplayPath(string path)
        {
            string normalized = Path.GetFullPath(path).Replace('\\', '/');
            int marker = normalized.LastIndexOf("/.behaviordiff/", StringComparison.Ordinal);
            return marker >= 0 ? normalized.Substring(marker + 1) : Path.GetFileName(path);
        }

        private sealed class RuleState
        {
            private readonly Func<JsonObject, bool> _matches;

            private RuleState(
                string id,
                string kind,
                string reason,
                string? expires,
                bool expired,
                Func<JsonObject, bool> matches)
            {
                Id = id;
                Kind = kind;
                Reason = reason;
                Expires = expires;
                Expired = expired;
                _matches = matches;
            }

            internal string Id { get; }

            internal string Kind { get; }

            internal string Reason { get; }

            internal string? Expires { get; }

            internal bool Expired { get; }

            internal int MatchCount { get; set; }

            internal bool Matches(JsonObject member) => _matches(member);

            internal static RuleState Acknowledgement(BaselineAcknowledgement rule, DateOnly today)
            {
                string? expectedPath = rule.Path?.Replace('\\', '/');
                return new RuleState(
                    rule.Id,
                    "acknowledgement",
                    rule.Reason,
                    rule.Expires,
                    IsExpired(rule.Expires, rule.Id, today),
                    member => string.Equals(Text(member, "memberName"), rule.Member, StringComparison.Ordinal)
                        && (expectedPath is null
                            || string.Equals(Text(member, "filePath").Replace('\\', '/'), expectedPath, StringComparison.Ordinal)));
            }

            internal static RuleState Ignore(BaselineIgnore rule, string kind, DateOnly today)
            {
                Regex pattern = Glob(rule.Pattern, path: kind == "path");
                return new RuleState(
                    rule.Id,
                    kind + "Ignore",
                    rule.Reason,
                    rule.Expires,
                    IsExpired(rule.Expires, rule.Id, today),
                    member => pattern.IsMatch(kind == "path"
                        ? Text(member, "filePath").Replace('\\', '/')
                        : Text(member, "memberName")));
            }

            private static bool IsExpired(string? value, string id, DateOnly today) =>
                ParseExpiry(value, id) is DateOnly expiry && expiry < today;

            private static Regex Glob(string value, bool path)
            {
                var expression = new System.Text.StringBuilder("^");
                for (int index = 0; index < value.Length; index++)
                {
                    char current = value[index];
                    if (current == '*')
                    {
                        bool recursive = index + 1 < value.Length && value[index + 1] == '*';
                        if (recursive)
                        {
                            index++;
                            if (path && index + 1 < value.Length && value[index + 1] == '/')
                            {
                                index++;
                                expression.Append("(?:.*/)?");
                            }
                            else
                            {
                                expression.Append(".*");
                            }
                        }
                        else
                        {
                            expression.Append(path ? "[^/]*" : ".*");
                        }
                    }
                    else if (current == '?')
                    {
                        expression.Append(path ? "[^/]" : ".");
                    }
                    else
                    {
                        expression.Append(Regex.Escape(current.ToString()));
                    }
                }

                expression.Append('$');
                return new Regex(expression.ToString(), RegexOptions.CultureInvariant);
            }
        }
    }
}