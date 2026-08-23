using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace BehaviorDiff.Cli
{
    /// <summary>Thin Azure Repos transport. All behavior decisions already live in findings.json.</summary>
    internal sealed class AzureDevOpsPoster
    {
        private const string ApiVersion = "7.1";
        private readonly JsonSerializerOptions _json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        internal async Task PostAsync(JsonElement findings)
        {
            string token = Required("SYSTEM_ACCESSTOKEN");
            string collectionUri = RequiredAny("SYSTEM_COLLECTIONURI", "SYSTEM_TEAMFOUNDATIONCOLLECTIONURI");
            string project = Required("SYSTEM_TEAMPROJECT");
            string repositoryId = Required("BUILD_REPOSITORY_ID");
            string pullRequestId = Required("SYSTEM_PULLREQUEST_PULLREQUESTID");

            string root = collectionUri.TrimEnd('/') + "/" + Uri.EscapeDataString(project)
                + "/_apis/git/repositories/" + Uri.EscapeDataString(repositoryId)
                + "/pullRequests/" + Uri.EscapeDataString(pullRequestId);

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            List<ExistingComment> existing = await GetExisting(client, root).ConfigureAwait(false);
            string summaryMarker = "<!-- behaviordiff:pr:" + pullRequestId + ":summary -->";
            await Upsert(
                client,
                root,
                existing,
                summaryMarker,
                RenderSummary(findings, summaryMarker),
                threadContext: null).ConfigureAwait(false);

            if (String(findings, "status") != "analyzed"
                || !findings.TryGetProperty("members", out JsonElement members))
            {
                return;
            }

            foreach (JsonElement member in members.EnumerateArray().Where(member =>
                String(member, "attribution") == "unexpected" && FindingPolicy.IsCommentEligible(findings, member)))
            {
                string? filePath = NullableString(member, "filePath");
                int? line = NullableInt(member, "line");
                if (filePath is null || line is null || line <= 0 || Bool(member, "sourceGenerated"))
                {
                    continue;
                }

                string marker = "<!-- behaviordiff:pr:" + pullRequestId + ":member:"
                    + MemberKey(String(member, "memberName")) + " -->";
                var context = new
                {
                    filePath = "/" + filePath.TrimStart('/'),
                    rightFileStart = new { line, offset = 1 },
                    rightFileEnd = new { line, offset = 1 },
                };

                await Upsert(
                    client,
                    root,
                    existing,
                    marker,
                    RenderMember(member, marker),
                    context).ConfigureAwait(false);
            }
        }

        private async Task<List<ExistingComment>> GetExisting(HttpClient client, string root)
        {
            using JsonDocument response = await Send(client, HttpMethod.Get, root + "/threads?api-version=" + ApiVersion, body: null)
                .ConfigureAwait(false);
            var result = new List<ExistingComment>();
            if (!response.RootElement.TryGetProperty("value", out JsonElement threads))
            {
                return result;
            }

            foreach (JsonElement thread in threads.EnumerateArray())
            {
                int threadId = thread.GetProperty("id").GetInt32();
                if (!thread.TryGetProperty("comments", out JsonElement comments))
                {
                    continue;
                }

                foreach (JsonElement comment in comments.EnumerateArray())
                {
                    if (Bool(comment, "isDeleted"))
                    {
                        continue;
                    }

                    result.Add(new ExistingComment(
                        threadId,
                        comment.GetProperty("id").GetInt32(),
                        String(comment, "content")));
                }
            }

            return result;
        }

        private async Task Upsert(
            HttpClient client,
            string root,
            IReadOnlyList<ExistingComment> existing,
            string marker,
            string content,
            object? threadContext)
        {
            ExistingComment? match = existing.FirstOrDefault(comment => comment.Content.Contains(marker, StringComparison.Ordinal));
            if (match is not null)
            {
                string updateUrl = root + "/threads/" + match.ThreadId + "/comments/" + match.CommentId
                    + "?api-version=" + ApiVersion;
                using JsonDocument _ = await Send(client, HttpMethod.Patch, updateUrl, new { content }).ConfigureAwait(false);
                Console.WriteLine("  updated Azure DevOps PR comment " + match.ThreadId + "/" + match.CommentId);
                return;
            }

            var body = new Dictionary<string, object?>
            {
                ["comments"] = new[] { new { parentCommentId = 0, content, commentType = 1 } },
                ["status"] = 1,
            };
            if (threadContext is not null)
            {
                body["threadContext"] = threadContext;
            }

            using JsonDocument created = await Send(
                client,
                HttpMethod.Post,
                root + "/threads?api-version=" + ApiVersion,
                body).ConfigureAwait(false);
            Console.WriteLine("  created Azure DevOps PR thread " + created.RootElement.GetProperty("id").GetInt32());
        }

        private async Task<JsonDocument> Send(HttpClient client, HttpMethod method, string url, object? body)
        {
            try
            {
                using var request = new HttpRequestMessage(method, url);
                if (body is not null)
                {
                    request.Content = new StringContent(JsonSerializer.Serialize(body, _json), Encoding.UTF8, "application/json");
                }

                using HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false);
                string content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new CliException("Azure DevOps REST " + method + " " + response.StatusCode + ": " + Truncate(content));
                }

                return JsonDocument.Parse(content.Length == 0 ? "{}" : content);
            }
            catch (HttpRequestException ex)
            {
                throw new CliException("Azure DevOps REST request failed: " + ex.Message);
            }
        }

        internal static string RenderSummary(JsonElement findings, string marker)
        {
            var builder = new StringBuilder();
            string status = String(findings, "status");
            if (status != "analyzed")
            {
                string reason = findings.TryGetProperty("refusal", out JsonElement refusal)
                    ? String(refusal, "reason")
                    : "No reason was recorded.";
                builder.AppendLine("## BehaviorDiff: analysis could not complete");
                builder.AppendLine();
                builder.AppendLine("**No safety verdict was produced.** This is not a clean result.");
                builder.AppendLine();
                builder.AppendLine("> " + reason.Replace("\n", "\n> "));
                builder.AppendLine();
                builder.Append(marker);
                return builder.ToString();
            }

            JsonElement summary = findings.GetProperty("summary");
            builder.AppendLine("## BehaviorDiff runtime analysis");
            builder.AppendLine();
            AppendCoverage(builder, findings);
            builder.AppendLine();

            JsonElement[] unexpected = FindingPolicy.EligibleUnexpected(findings);
            int unexpectedMembers = unexpected.Length;
            if (unexpectedMembers == 0)
            {
                int suppressed = Int(summary, "suppressedMembers");
                int lowerConfidence = Math.Max(0, Int(summary, "unexpectedMembers") - suppressed);
                if (lowerConfidence > 0)
                {
                    builder.AppendLine("**No high-confidence findings to comment. " + lowerConfidence
                        + " lower-confidence or nondeterministic finding(s) remain in `findings.json`; "
                        + "use strict mode to include them in comments.**");
                }
                else if (suppressed > 0)
                {
                    builder.AppendLine("**Every unexpected finding was acknowledged or ignored by the repository baseline.**");
                }
                else
                {
                    builder.AppendLine("**No unexpected behavior changes across " + Int(summary, "editedFiles")
                        + " edited files (" + Int(summary, "tracedMembers")
                        + (Int(summary, "tracedMembers") == 1 ? " member, " : " members, ")
                        + Int(summary, "observedCallSites")
                        + (Int(summary, "observedCallSites") == 1 ? " call site observed).**" : " call sites observed).**"));
                }
            }
            else
            {
                JsonElement[] gaps = unexpected
                    .Where(member => Int(member, "untestedCallSiteCount") > 0)
                    .ToArray();
                JsonElement[] covered = unexpected
                    .Where(member => Int(member, "untestedCallSiteCount") == 0)
                    .ToArray();
                if (gaps.Length > 0)
                {
                    builder.AppendLine("**UNASSERTED: " + gaps.Length + " member(s), across "
                        + gaps.Sum(member => Int(member, "callSiteCount")) + " call site(s).**");
                    builder.AppendLine();
                    AppendMembers(builder, findings, "unexpected", "Unasserted behavior gaps", hasUntested: true);
                }

                if (covered.Length > 0)
                {
                    builder.AppendLine("**TEST-COVERED: " + covered.Length + " member(s), across "
                        + covered.Sum(member => Int(member, "callSiteCount")) + " call site(s).**");
                    builder.AppendLine("Every executing test had an assertion react; these are recorded changes, not unasserted gaps.");
                    builder.AppendLine();
                    AppendMembers(builder, findings, "unexpected", "Test-covered changes", hasUntested: false);
                }
            }

            builder.AppendLine();
            builder.AppendLine("**EXPECTED: " + Int(summary, "expectedMembers") + " member(s), across "
                + Int(summary, "expectedCallSites") + " call site(s).**");
            AppendMembers(builder, findings, "expected", "Expected members");
            AppendCommentPolicy(builder, findings);
            AppendBaselinePolicy(builder, findings);
            builder.AppendLine();
            builder.Append(marker);
            return builder.ToString();
        }

        private static void AppendCoverage(StringBuilder builder, JsonElement findings)
        {
            JsonElement coverage = findings.GetProperty("coverage");
            JsonElement summary = coverage.GetProperty("summary");
            builder.AppendLine("### Edited-code coverage");
            builder.AppendLine("**" + Int(summary, "exercisedEditedFiles") + " of "
                + Int(summary, "editedFiles") + " edited files were exercised by tests.**");
            int members = Int(summary, "tracedMembers");
            int callSites = Int(summary, "observedCallSites");
            int calls = Int(summary, "totalCallCount");
            builder.AppendLine(members + (members == 1 ? " member, " : " members, ")
                + callSites + (callSites == 1 ? " call site, and " : " call sites, and ")
                + calls + (calls == 1 ? " total call was" : " total calls were")
                + " observed in representative base/PR runs.");

            JsonElement[] unexercised = coverage.GetProperty("files").EnumerateArray()
                .Where(file => !Bool(file, "exercised"))
                .ToArray();
            if (unexercised.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Not exercised (no behavioral claim): "
                    + string.Join(", ", unexercised.Select(file => "`" + NullableString(file, "filePath") + "`")) + ".");
                builder.AppendLine("Zero observed calls are not evidence that these files did not change behavior.");
            }
        }

        private static void AppendBaselinePolicy(StringBuilder builder, JsonElement findings)
        {
            if (!findings.TryGetProperty("baseline", out JsonElement baseline))
            {
                return;
            }

            string path = String(baseline, "path");
            string reference = "`" + path + "`";
            string? repositoryUri = Environment.GetEnvironmentVariable("BUILD_REPOSITORY_URI");
            if (!string.IsNullOrWhiteSpace(repositoryUri)
                && findings.TryGetProperty("refs", out JsonElement refs))
            {
                string prSha = String(refs, "prSha");
                if (prSha.Length > 0)
                {
                    reference = "[`" + path + "`](" + repositoryUri.TrimEnd('/') + "?path=/"
                        + Uri.EscapeDataString(path) + "&version=GC" + prSha + ")";
                }
            }

            builder.AppendLine();
            builder.AppendLine("Baseline policy " + reference + ": **" + Int(baseline, "suppressedMembers")
                + " member(s), " + Int(baseline, "suppressedCallSites") + " call site(s) suppressed**; **"
                + baseline.GetProperty("staleEntries").GetArrayLength() + " stale, "
                + baseline.GetProperty("expiredEntries").GetArrayLength() + " expired entry/entries**.");
            JsonElement[] stale = baseline.GetProperty("staleEntries").EnumerateArray().Take(10).ToArray();
            if (stale.Length > 0)
            {
                builder.AppendLine("Stale baseline rules: "
                    + string.Join(", ", stale.Select(entry => "`" + String(entry, "ruleId") + "`")) + ".");
            }
        }

        private static void AppendCommentPolicy(StringBuilder builder, JsonElement findings)
        {
            if (!findings.TryGetProperty("commentPolicy", out JsonElement policy))
            {
                return;
            }

            int raw = Int(findings.GetProperty("summary"), "unexpectedMembers");
            int baselineSuppressed = findings.TryGetProperty("baseline", out JsonElement baseline)
                ? Int(baseline, "suppressedMembers")
                : 0;
            int eligible = FindingPolicy.EligibleUnexpected(findings).Length;
            int confidenceSuppressed = Math.Max(0, raw - baselineSuppressed - eligible);
            builder.AppendLine();
            builder.AppendLine("Comment policy: **" + String(policy, "mode") + "**; " + eligible
                + " shown, " + confidenceSuppressed
                + " lower-confidence or nondeterministic finding(s) retained only in `findings.json`.");
        }

        private static void AppendMembers(
            StringBuilder builder,
            JsonElement findings,
            string attribution,
            string heading,
            bool? hasUntested = null)
        {
            if (!findings.TryGetProperty("members", out JsonElement members))
            {
                return;
            }

            JsonElement[] selected = members.EnumerateArray()
                .Where(member => String(member, "attribution") == attribution
                    && (attribution != "unexpected" || FindingPolicy.IsCommentEligible(findings, member))
                    && (hasUntested is null
                        || (Int(member, "untestedCallSiteCount") > 0) == hasUntested.Value))
                .ToArray();
            if (selected.Length == 0)
            {
                return;
            }

            builder.AppendLine("### " + heading);
            builder.AppendLine("| Member | Call sites | Source | Evidence |");
            builder.AppendLine("|---|---:|---|---|");
            foreach (JsonElement member in selected)
            {
                string source = NullableString(member, "filePath") ?? "unresolved";
                int? line = NullableInt(member, "line");
                if (line is not null)
                {
                    source += ":" + line;
                }

                string[] tests = Strings(member, "observingTests").Take(2).ToArray();
                string evidence = string.Join(", ", Strings(member, "symptoms").Take(3));
                if (tests.Length > 0)
                {
                    evidence += "; tests: " + string.Join(", ", tests.Select(test => "`" + Escape(test) + "`"));
                }

                builder.AppendLine("| `" + Escape(String(member, "memberName")) + "` | "
                    + Int(member, "callSiteCount") + " | `" + Escape(source) + "` | "
                    + Escape(evidence) + " |");
            }
        }

        private static string RenderMember(JsonElement member, string marker)
        {
            var builder = new StringBuilder();
            builder.AppendLine("### Unexpected runtime behavior change");
            builder.AppendLine();
            builder.AppendLine("`" + String(member, "memberName") + "`");
            builder.AppendLine();
            builder.AppendLine("This member is in a file the PR did **not** modify, but its runtime behavior changed.");
            builder.AppendLine();
            builder.AppendLine("- Call sites: " + Int(member, "callSiteCount"));
            builder.AppendLine("- Distinct tests: " + Int(member, "distinctTestCount"));
            builder.AppendLine("- Verified frontier: " + Bool(member, "verified").ToString().ToLowerInvariant());
            foreach (string reason in Strings(member, "downgradeReasons").Take(2))
            {
                builder.AppendLine("- Downgrade: " + reason);
            }

            if (member.TryGetProperty("evidence", out JsonElement evidence))
            {
                builder.AppendLine();
                builder.AppendLine("Evidence (up to 5 observations):");
                foreach (JsonElement observation in evidence.EnumerateArray().Take(5))
                {
                    builder.AppendLine("- `" + String(observation, "testId") + "`: "
                        + RenderValue(NullableString(observation, "baseReturn"), NullableString(observation, "baseException"))
                        + " -> " + RenderValue(NullableString(observation, "prReturn"), NullableString(observation, "prException")));
                }
            }

            builder.AppendLine();
            builder.Append(marker);
            return builder.ToString();
        }

        private static string RenderValue(string? value, string? exception) =>
            exception is not null ? "exception `" + exception + "`" : "`" + (value ?? "(not rendered)") + "`";

        private static string MemberKey(string memberName)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(memberName));
            return Convert.ToHexString(hash).Substring(0, 16).ToLowerInvariant();
        }

        private static string Required(string name)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new CliException("Azure DevOps posting requires " + name + ".");
            }

            return value;
        }

        private static string RequiredAny(string first, string second)
        {
            string? value = Environment.GetEnvironmentVariable(first);
            return string.IsNullOrWhiteSpace(value) ? Required(second) : value;
        }

        private static string String(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        private static string? NullableString(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static int Int(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : 0;

        private static int? NullableInt(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : null;

        private static bool Bool(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.True;

        private static IEnumerable<string> Strings(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Select(value => value.GetString() ?? string.Empty)
                : Enumerable.Empty<string>();

        private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ").Replace("\n", " ");

        private static string Truncate(string text) => text.Length <= 1000 ? text : text.Substring(0, 1000);

        private sealed record ExistingComment(int ThreadId, int CommentId, string Content);
    }
}