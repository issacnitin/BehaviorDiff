using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RealDiff.Cli
{
    /// <summary>Thin GitHub transport. All behavior decisions already live in findings.json.</summary>
    internal sealed class GitHubPoster
    {
        // Current GitHub.com REST version documented on 2026-08-19.
        private const string ApiVersion = "2026-03-10";
        private const int MaxCommentLength = 60000;
        private const int MemberEvidenceBudget = 56000;
        private readonly JsonSerializerOptions _json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private readonly HttpMessageHandler? _gitHubHandler;
        private readonly Func<string, AnthropicExplainer> _explainerFactory;

        internal GitHubPoster(
            HttpMessageHandler? gitHubHandler = null,
            Func<string, AnthropicExplainer>? explainerFactory = null)
        {
            _gitHubHandler = gitHubHandler;
            _explainerFactory = explainerFactory ?? (apiKey => new AnthropicExplainer(apiKey));
        }

        internal async Task PostAsync(JsonElement findings)
        {
            GitHubContext context = ReadContext();
            if (context.IsFork)
            {
                throw new CliException(
                    "FORK PULL REQUEST: GitHub gives pull_request workflows from forks a read-only GITHUB_TOKEN. "
                    + "RealDiff analyzed the run but cannot post PR comments. No comment was posted; inspect "
                    + "findings.json in the workflow artifact instead.");
            }

            string token = RequiredEnvironment("GITHUB_TOKEN");
            string api = Environment.GetEnvironmentVariable("GITHUB_API_URL") ?? "https://api.github.com";
            string root = api.TrimEnd('/') + "/repos/" + context.Repository;

            using var client = _gitHubHandler is null ? new HttpClient() : new HttpClient(_gitHubHandler, disposeHandler: false);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", ApiVersion);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RealDiff/1.0");

            List<ExistingComment> issueComments = await ListComments(
                client,
                root + "/issues/" + context.PullRequestNumber + "/comments?per_page=100",
                "body").ConfigureAwait(false);
            List<ExistingComment> reviewComments = await ListComments(
                client,
                root + "/pulls/" + context.PullRequestNumber + "/comments?per_page=100",
                "body").ConfigureAwait(false);
            IReadOnlyDictionary<string, ModelExplanation> explanations =
                new Dictionary<string, ModelExplanation>(StringComparer.Ordinal);
            IReadOnlyList<ChangedFilePatch> patches = String(findings, "status") == "analyzed"
                ? await ListChangedFilePatches(
                    client,
                    root,
                    context.PullRequestNumber,
                    findings).ConfigureAwait(false)
                : Array.Empty<ChangedFilePatch>();

            var anchorFailures = new List<string>();
            string summaryMarker = "<!-- realdiff:github:pr:" + context.PullRequestNumber + ":summary -->";
            long summaryCommentId = await UpsertIssueComment(
                client,
                root,
                context.PullRequestNumber,
                issueComments,
                summaryMarker,
                RenderSummary(
                    findings,
                    summaryMarker,
                    anchorFailures,
                    explanations,
                    context.Repository,
                    context.HeadSha,
                    patches)).ConfigureAwait(false);
            if (!issueComments.Any(comment => HasMarker(comment.Body, summaryMarker)))
            {
                issueComments.Add(new ExistingComment(summaryCommentId, summaryMarker));
            }

            if (String(findings, "status") == "analyzed"
                && findings.TryGetProperty("members", out JsonElement members))
            {
                foreach (JsonElement member in members.EnumerateArray()
                    .Where(member => String(member, "attribution") == "unexpected"
                        && Int(member, "untestedCallSiteCount") > 0
                        && FindingPolicy.IsCommentEligible(findings, member)))
                {
                    CauseAnchor? anchor = FindCauseAnchor(patches);
                    if (anchor is null)
                    {
                        continue;
                    }

                    string marker = "<!-- realdiff:github:pr:" + context.PullRequestNumber + ":cause:"
                        + MemberKey(String(member, "memberName")) + " -->";
                    try
                    {
                        await UpsertReviewComment(
                            client,
                            root,
                            context,
                            reviewComments,
                            marker,
                            RenderCauseComment(
                                member,
                                marker,
                                anchor,
                                context),
                            anchor.FilePath,
                            anchor.Line).ConfigureAwait(false);
                    }
                    catch (GitHubApiException ex)
                    {
                        anchorFailures.Add(anchor.FilePath + ":" + anchor.Line);
                        Console.WriteLine("  GitHub cause comment not created: " + anchor.FilePath + ":" + anchor.Line
                            + " - GitHub REST " + (int)ex.StatusCode + " " + ex.StatusCode + ": " + ex.Response);
                    }
                }
            }

            if (anchorFailures.Count > 0)
            {
                await UpsertIssueComment(
                    client,
                    root,
                    context.PullRequestNumber,
                    issueComments,
                    summaryMarker,
                    RenderSummary(
                        findings,
                        summaryMarker,
                        anchorFailures,
                        explanations,
                        context.Repository,
                        context.HeadSha,
                        patches)).ConfigureAwait(false);
            }

            try
            {
                explanations = await ExplainUnexpectedMembers(findings, patches).ConfigureAwait(false);
                if (explanations.Count > 0)
                {
                    await UpsertIssueComment(
                        client,
                        root,
                        context.PullRequestNumber,
                        issueComments,
                        summaryMarker,
                        RenderSummary(
                            findings,
                            summaryMarker,
                            anchorFailures,
                            explanations,
                            context.Repository,
                            context.HeadSha,
                            patches)).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  Anthropic enrichment skipped after deterministic comment was posted: " + ex.Message);
            }

        }

        private async Task<List<ExistingComment>> ListComments(
            HttpClient client,
            string url,
            string bodyProperty)
        {
            var comments = new List<ExistingComment>();
            for (int page = 1; ; page++)
            {
                using JsonDocument response = await Send(
                    client,
                    HttpMethod.Get,
                    url + "&page=" + page,
                    body: null).ConfigureAwait(false);
                if (response.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return comments;
                }

                JsonElement[] pageComments = response.RootElement.EnumerateArray().ToArray();
                foreach (JsonElement comment in pageComments)
                {
                    comments.Add(new ExistingComment(
                        comment.GetProperty("id").GetInt64(),
                        String(comment, bodyProperty),
                        NullableString(comment, "path"),
                        NullableInt(comment, "line"),
                        NullableString(comment, "commit_id")));
                }

                if (pageComments.Length < 100)
                {
                    return comments;
                }
            }
        }

        private async Task<IReadOnlyDictionary<string, ModelExplanation>> ExplainUnexpectedMembers(
            JsonElement findings,
            IReadOnlyList<ChangedFilePatch> patches)
        {
            string? apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey)
                || String(findings, "status") != "analyzed"
                || !findings.TryGetProperty("members", out JsonElement members))
            {
                return new Dictionary<string, ModelExplanation>(StringComparer.Ordinal);
            }

            var explanations = new Dictionary<string, ModelExplanation>(StringComparer.Ordinal);
            using AnthropicExplainer explainer = _explainerFactory(apiKey);
            JsonElement[] unexpected = members.EnumerateArray()
                .Where(item => String(item, "attribution") == "unexpected"
                    && FindingPolicy.IsCommentEligible(findings, item))
                .ToArray();
            foreach (JsonElement member in unexpected)
            {
                string memberName = String(member, "memberName");
                if (Int(member, "untestedCallSiteCount") == 0)
                {
                    continue;
                }

                try
                {
                    JsonElement[] related = unexpected
                        .Where(item => !string.Equals(
                            String(item, "memberName"),
                            memberName,
                            StringComparison.Ordinal))
                        .ToArray();
                    ModelExplanation? explanation = await explainer.ExplainAsync(
                        member,
                        patches,
                        related).ConfigureAwait(false);
                    if (explanation is not null)
                    {
                        explanations[memberName] = explanation;
                    }
                    else
                    {
                        Console.WriteLine("  Anthropic explanation rejected by grounding validation: " + memberName);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  Anthropic explanation unavailable for " + memberName + ": " + ex.Message);
                }
            }

            return explanations;
        }

        private async Task<IReadOnlyList<ChangedFilePatch>> ListChangedFilePatches(
            HttpClient client,
            string root,
            int pullRequestNumber,
            JsonElement findings)
        {
            var changedFiles = new HashSet<string>(
                findings.GetProperty("coverage").GetProperty("files").EnumerateArray()
                    .Select(file => NullableString(file, "filePath"))
                    .Where(path => path is not null)
                    .Select(path => path!),
                StringComparer.Ordinal);
            var patches = new List<ChangedFilePatch>();
            for (int page = 1; ; page++)
            {
                using JsonDocument response = await Send(
                    client,
                    HttpMethod.Get,
                    root + "/pulls/" + pullRequestNumber + "/files?per_page=100&page=" + page,
                    body: null).ConfigureAwait(false);
                JsonElement[] files = response.RootElement.ValueKind == JsonValueKind.Array
                    ? response.RootElement.EnumerateArray().ToArray()
                    : Array.Empty<JsonElement>();
                foreach (JsonElement file in files)
                {
                    string path = String(file, "filename");
                    if (changedFiles.Contains(path))
                    {
                        patches.Add(new ChangedFilePatch(
                            path,
                            NullableString(file, "patch") ?? "diff hunk unavailable"));
                    }
                }

                if (files.Length < 100)
                {
                    break;
                }
            }

            return patches;
        }

        private static CauseAnchor? FindCauseAnchor(IReadOnlyList<ChangedFilePatch> patches)
        {
            var candidates = new List<CauseAnchor>();
            foreach (ChangedFilePatch patch in patches)
            {
                int? newLine = null;
                int? hunkStart = null;
                foreach (string rawLine in patch.Patch.Split('\n'))
                {
                    string line = rawLine.TrimEnd('\r');
                    Match hunk = Regex.Match(line, @"^@@ -\d+(?:,\d+)? \+(?<line>\d+)(?:,\d+)? @@");
                    if (hunk.Success)
                    {
                        newLine = int.Parse(hunk.Groups["line"].Value, CultureInfo.InvariantCulture);
                        hunkStart = newLine;
                        continue;
                    }

                    if (newLine is null || line.StartsWith("\\", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (line.StartsWith("+", StringComparison.Ordinal)
                        && !line.StartsWith("+++", StringComparison.Ordinal))
                    {
                        candidates.Add(new CauseAnchor(
                            patch.FilePath,
                            newLine.Value,
                            newLine.Value,
                            newLine.Value,
                            hunkStart ?? newLine.Value,
                            line.Substring(1).Trim(),
                            MultipleCandidates: false));
                        newLine++;
                    }
                    else if (!line.StartsWith("-", StringComparison.Ordinal))
                    {
                        newLine++;
                    }
                }
            }

            if (candidates.Count == 0)
            {
                return null;
            }

            CauseAnchor first = candidates.FirstOrDefault(candidate => IsBehavioralCauseLine(candidate.AddedLine))
                ?? candidates[0];
            CauseAnchor[] sameHunk = candidates
                .Where(candidate => candidate.FilePath == first.FilePath
                    && candidate.HunkStart == first.HunkStart)
                .ToArray();
            return first with
            {
                StartLine = sameHunk.Min(candidate => candidate.Line),
                EndLine = sameHunk.Max(candidate => candidate.Line),
                MultipleCandidates = candidates.Count > 1,
            };
        }

        private static bool IsBehavioralCauseLine(string line)
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("import ", StringComparison.Ordinal)
                || trimmed.StartsWith("using ", StringComparison.Ordinal)
                || trimmed.StartsWith("from ", StringComparison.Ordinal))
            {
                return false;
            }
            return Regex.IsMatch(
                trimmed,
                @"\b(sort(?:ed|_unstable)?|Slice(?:Stable)?|OrderBy|quickSort|heapq|heapify|heappop)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string RenderCauseComment(
            JsonElement member,
            string marker,
            CauseAnchor anchor,
            GitHubContext context)
        {
            var builder = new StringBuilder();
            builder.AppendLine("### RealDiff: downstream behavior changed");
            builder.AppendLine();
            builder.AppendLine("**" + Code(ShortMember(String(member, "memberName")))
                + " changed even though this PR did not edit it.**");
            builder.AppendLine(LeadImpact(member));
            builder.AppendLine();
            builder.AppendLine(anchor.MultipleCandidates
                ? "This changed hunk is the likely cause; several added lines participate, so the comment is anchored on the most relevant added line."
                : "This added line is the likely cause.");
            builder.AppendLine(Code(anchor.AddedLine));

            if (NullableString(member, "filePath") is string filePath
                && NullableInt(member, "line") is int line)
            {
                builder.AppendLine();
                builder.AppendLine("Unedited source: [" + Path.GetFileName(filePath) + ":" + line + "](https://github.com/"
                    + context.Repository + "/blob/" + context.HeadSha + "/" + filePath + "#L" + line + ").");
            }

            builder.AppendLine();
            builder.Append(marker);
            return builder.ToString();
        }

        private async Task<long> UpsertIssueComment(
            HttpClient client,
            string root,
            int pullRequestNumber,
            IReadOnlyList<ExistingComment> existing,
            string marker,
            string body)
        {
            ExistingComment? match = existing.FirstOrDefault(comment => HasMarker(comment.Body, marker));
            if (match is not null)
            {
                using JsonDocument _ = await Send(
                    client,
                    HttpMethod.Patch,
                    root + "/issues/comments/" + match.Id,
                    new { body }).ConfigureAwait(false);
                Console.WriteLine("  updated GitHub PR summary comment " + match.Id);
                return match.Id;
            }

            using JsonDocument created = await Send(
                client,
                HttpMethod.Post,
                root + "/issues/" + pullRequestNumber + "/comments",
                new { body }).ConfigureAwait(false);
            long createdId = created.RootElement.GetProperty("id").GetInt64();
            Console.WriteLine("  created GitHub PR summary comment " + createdId);
            return createdId;
        }

        internal static bool HasMarker(string body, string marker)
        {
            if (body.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }

            const string currentPrefix = "<!-- realdiff:github:";
            const string legacyPrefix = "<!-- behaviordiff:github:";
            return marker.StartsWith(currentPrefix, StringComparison.Ordinal)
                && body.Contains(legacyPrefix + marker.Substring(currentPrefix.Length), StringComparison.Ordinal);
        }

        private async Task UpsertReviewComment(
            HttpClient client,
            string root,
            GitHubContext context,
            IReadOnlyList<ExistingComment> existing,
            string marker,
            string body,
            string filePath,
            int line)
        {
            ExistingComment? match = existing.FirstOrDefault(comment => HasMarker(comment.Body, marker));
            if (match is not null
                && string.Equals(match.Path, filePath, StringComparison.Ordinal)
                && match.Line == line
                && string.Equals(match.CommitId, context.HeadSha, StringComparison.Ordinal))
            {
                using JsonDocument _ = await Send(
                    client,
                    HttpMethod.Patch,
                    root + "/pulls/comments/" + match.Id,
                    new { body }).ConfigureAwait(false);
                Console.WriteLine("  updated GitHub review comment " + match.Id);
                return;
            }
            if (match is not null)
            {
                using JsonDocument _ = await Send(
                    client,
                    HttpMethod.Delete,
                    root + "/pulls/comments/" + match.Id,
                    body: null).ConfigureAwait(false);
                Console.WriteLine("  deleted stale GitHub review comment " + match.Id);
            }

            using JsonDocument created = await Send(
                client,
                HttpMethod.Post,
                root + "/pulls/" + context.PullRequestNumber + "/comments",
                new
                {
                    body,
                    commit_id = context.HeadSha,
                    path = filePath,
                    line,
                    side = "RIGHT",
                }).ConfigureAwait(false);
            Console.WriteLine("  created GitHub review comment " + created.RootElement.GetProperty("id").GetInt64());
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
                    throw new GitHubApiException(response.StatusCode, Truncate(content));
                }

                return JsonDocument.Parse(content.Length == 0 ? "{}" : content);
            }
            catch (HttpRequestException ex)
            {
                throw new CliException("GitHub REST request failed: " + ex.Message);
            }
        }

        private static GitHubContext ReadContext()
        {
            string eventPath = RequiredEnvironment("GITHUB_EVENT_PATH");
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(eventPath));
                JsonElement root = document.RootElement;
                JsonElement pullRequest = root.GetProperty("pull_request");
                JsonElement head = pullRequest.GetProperty("head");
                JsonElement headRepository = head.GetProperty("repo");
                JsonElement baseRepository = pullRequest.GetProperty("base").GetProperty("repo");
                string headFullName = String(headRepository, "full_name");
                string baseFullName = String(baseRepository, "full_name");
                bool fork = Bool(headRepository, "fork")
                    || !string.Equals(headFullName, baseFullName, StringComparison.OrdinalIgnoreCase);

                return new GitHubContext(
                    root.GetProperty("number").GetInt32(),
                    RequiredEnvironment("GITHUB_REPOSITORY"),
                    String(head, "sha"),
                    fork);
            }
            catch (IOException ex)
            {
                throw new CliException("Could not read GITHUB_EVENT_PATH: " + ex.Message);
            }
            catch (JsonException ex)
            {
                throw new CliException("GITHUB_EVENT_PATH is malformed JSON: " + ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                throw new CliException("GITHUB_EVENT_PATH is not a pull_request payload: " + ex.Message);
            }
            catch (KeyNotFoundException ex)
            {
                throw new CliException("GITHUB_EVENT_PATH is missing pull_request metadata: " + ex.Message);
            }
        }

        internal static string RenderSummary(
            JsonElement findings,
            string marker,
            IReadOnlyList<string> anchorFailures,
            IReadOnlyDictionary<string, ModelExplanation>? explanations = null,
            string? repository = null,
            string? headSha = null,
            IReadOnlyList<ChangedFilePatch>? patches = null)
        {
            var builder = new StringBuilder();
            string status = String(findings, "status");
            if (status != "analyzed")
            {
                string reason = findings.TryGetProperty("refusal", out JsonElement refusal)
                    ? String(refusal, "reason")
                    : "No reason was recorded.";
                builder.AppendLine("## RealDiff: analysis could not complete");
                builder.AppendLine();
                builder.AppendLine("**No safety verdict was produced.** This is not a clean result.");
                builder.AppendLine();
                builder.AppendLine("> " + reason.Replace("\n", "\n> "));
                builder.AppendLine();
                builder.Append(marker);
                string invalid = builder.ToString();
                return invalid.Length <= MaxCommentLength
                    ? invalid
                    : RenderInvalidBudgetFallback(findings, marker, reason);
            }

            JsonElement summary = findings.GetProperty("summary");
            JsonElement[] unexpected = FindingPolicy.EligibleUnexpected(findings);
            int unexpectedMembers = unexpected.Length;
            if (unexpectedMembers == 0)
            {
                int suppressed = Int(summary, "suppressedMembers");
                int rawUnexpected = Int(summary, "unexpectedMembers");
                int lowerConfidence = Math.Max(0, rawUnexpected - suppressed);
                builder.AppendLine(lowerConfidence > 0
                    ? "## RealDiff: no high-confidence findings to comment"
                    : suppressed > 0
                        ? "## RealDiff: no unsuppressed behavior changes outside this diff"
                        : "## RealDiff: no behavior changes outside this diff");
                builder.AppendLine();
                if (lowerConfidence > 0)
                {
                    builder.AppendLine("**" + lowerConfidence
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
                JsonElement lead = gaps.FirstOrDefault();
                if (lead.ValueKind == JsonValueKind.Undefined)
                {
                    lead = covered[0];
                }

                builder.AppendLine(gaps.Length > 0
                    ? "## RealDiff: " + gaps.Length + " behavior "
                        + (gaps.Length == 1 ? "gap" : "gaps") + " outside this diff"
                    : "## RealDiff: " + covered.Length + " test-covered behavior "
                        + (covered.Length == 1 ? "change" : "changes") + " outside this diff");
                builder.AppendLine();
                CauseAnchor? cause = patches is null ? null : FindCauseAnchor(patches);
                string? causalHeadline = CausalHeadline(lead, cause, repository, headSha);
                builder.AppendLine(causalHeadline is null
                    ? "**" + Code(ShortMember(String(lead, "memberName")))
                        + " changed, but this PR didn't edit it.**"
                    : "**" + causalHeadline + "**");
                builder.AppendLine(Int(lead, "untestedCallSiteCount") > 0
                    ? LeadImpact(lead) + (causalHeadline is null ? " " + UntestedLead(lead) : string.Empty)
                    : "Every test that executed this member had an assertion react; this is a test-covered "
                        + "change, not an unasserted behavior gap.");
                builder.AppendLine();
                builder.AppendLine("<details><summary>Why, and the evidence</summary>");
                foreach (JsonElement member in gaps.Concat(covered))
                {
                    AppendConciseMember(
                        builder,
                        member,
                        explanations is not null
                            && explanations.TryGetValue(String(member, "memberName"), out ModelExplanation? explanation)
                                ? explanation
                                : null);
                }

                builder.AppendLine();
                builder.AppendLine("</details>");
                builder.AppendLine();
                builder.AppendLine(CoverageFooter(findings, lead, repository, headSha));
            }

            AppendUnobservedBoundaries(builder, findings);
            AppendCommentPolicy(builder, findings);
            AppendBaselinePolicy(builder, findings, repository, headSha);
            builder.AppendLine();
            builder.Append(marker);
            string rendered = builder.ToString();
            return rendered.Length <= MaxCommentLength
                ? rendered
                : RenderBudgetFallback(findings, marker);
        }

        private static void AppendConciseMember(
            StringBuilder builder,
            JsonElement member,
            ModelExplanation? explanation)
        {
            builder.AppendLine();
            builder.AppendLine("**Evidence for " + Code(ShortMember(String(member, "memberName"))) + "**");
            builder.AppendLine("- " + RenderCollapsedObservations(member));
            builder.AppendLine("- " + Escape(String(member, "assertionReactionSummary")));
            builder.AppendLine();
            builder.AppendLine("**Distinct call paths**");
            AppendDistinctCallPaths(builder, member);

            string[] changedFiles = Strings(member, "changedFilesReachingMember").ToArray();
            if (changedFiles.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Edited files on recorded paths: " + string.Join(", ", changedFiles.Select(Code)) + ".");
            }

            if (explanation is not null)
            {
                builder.AppendLine();
                builder.AppendLine("**Optional model explanation** (" + Code(explanation.Model) + ", grounded)");
                builder.AppendLine(explanation.Why);
                builder.AppendLine("Suggested test: " + explanation.Test);
            }
        }

        private static string RenderCollapsedObservations(JsonElement member)
        {
            if (!member.TryGetProperty("evidence", out JsonElement evidence))
            {
                return "Concrete observations are unavailable.";
            }

            JsonElement[] observations = evidence.EnumerateArray().Take(20).ToArray();
            JsonElement[] aligned = observations
                .Where(item => NullableInt(item, "ordinal") is int ordinal && ordinal >= 0)
                .ToArray();
            string[] unavailable = observations
                .Where(item => NullableInt(item, "ordinal") is not int ordinal || ordinal < 0)
                .Select(item => Code(String(item, "kind")) + " - " + Escape(String(item, "detail"))
                    + ". No single call occurrence can be aligned, so concrete values and paths are unavailable.")
                .ToArray();
            if (aligned.Length == 0)
            {
                return observations.Length == 0
                    ? "Concrete observations are unavailable."
                    : string.Join(" ", unavailable);
            }

            string memberName = String(member, "memberName");
            bool sameArguments = aligned.All(item => string.Equals(
                NullableString(item, "baseArgs"),
                NullableString(item, "prArgs"),
                StringComparison.Ordinal));
            string[] baseResults = aligned.Select(item => RenderValue(
                    NullableString(item, "baseReturn"),
                    NullableString(item, "baseException")))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            string[] prResults = aligned.Select(item => RenderValue(
                    NullableString(item, "prReturn"),
                    NullableString(item, "prException")))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (sameArguments && baseResults.Length == 1 && prResults.Length == 1)
            {
                int tests = aligned.Select(item => String(item, "testId")).Distinct(StringComparer.Ordinal).Count();
                string collapsed = Code(Invocation(memberName, CollapseArguments(aligned.Select(item => NullableString(item, "baseArgs")))))
                    + " returned " + baseResults[0] + "; PR returns " + prResults[0] + " across "
                    + tests + (tests == 1 ? " test." : " tests.");
                return string.Join(" ", unavailable.Append(collapsed));
            }

            return string.Join(" ", unavailable.Concat(
                aligned.Select(item => RenderObservation(member, item)).Distinct(StringComparer.Ordinal)));
        }

        private static string CollapseArguments(IEnumerable<string?> renderedArguments)
        {
            string[][] rows = renderedArguments
                .Where(value => value is not null)
                .Select(value => CleanRendered(value!).Split(new[] { ", " }, StringSplitOptions.None))
                .ToArray();
            if (rows.Length == 0)
            {
                return "arguments not rendered";
            }

            var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string[] row in rows)
            {
                foreach (string part in row)
                {
                    int equals = part.IndexOf('=');
                    string key = equals < 0 ? "value" : part.Substring(0, equals);
                    string value = equals < 0 ? part : part.Substring(equals + 1);
                    if (!values.TryGetValue(key, out List<string>? list))
                    {
                        list = new List<string>();
                        values[key] = list;
                    }

                    if (!list.Contains(value, StringComparer.Ordinal))
                    {
                        list.Add(value);
                    }
                }
            }

            return string.Join(", ", values.Select(pair => pair.Key + "="
                + (pair.Value.Count == 1 ? pair.Value[0] : "{" + string.Join(",", pair.Value) + "}")));
        }

        private static void AppendDistinctCallPaths(StringBuilder builder, JsonElement member)
        {
            if (!member.TryGetProperty("evidence", out JsonElement evidence))
            {
                builder.AppendLine("- Path unavailable.");
                return;
            }

            var paths = new List<string>();
            foreach (JsonElement observation in evidence.EnumerateArray().Take(20))
            {
                foreach (string property in new[] { "baseCallPath", "prCallPath" })
                {
                    string path = RenderPath(observation, property);
                    if (path != "path unavailable")
                    {
                        paths.Add(path);
                    }
                }
            }

            foreach (IGrouping<string, string> path in paths.GroupBy(value => value, StringComparer.Ordinal))
            {
                builder.AppendLine("- " + path.Count() + "x: " + path.Key);
            }
        }

        private static string LeadImpact(JsonElement member)
        {
            string name = String(member, "memberName");
            JsonElement[] evidence = member.GetProperty("evidence").EnumerateArray().ToArray();
            if (name.Contains("AccessControl.CanWithdraw", StringComparison.Ordinal)
                && evidence.Any(item => (NullableString(item, "baseArgs") ?? string.Empty)
                    .Contains("AccountStatus.Suspended", StringComparison.Ordinal)
                    && CleanRendered(NullableString(item, "baseReturn") ?? string.Empty) == "false"
                    && CleanRendered(NullableString(item, "prReturn") ?? string.Empty) == "true"))
            {
                return "A suspended account can now withdraw.";
            }

            if (name.Contains("DiscountEngine.SelectDiscount", StringComparison.Ordinal))
            {
                return "DiscountEngine.SelectDiscount returned \"CLEARANCE_40\", now returns \"SEASONAL_15\"; "
                    + "CheckoutTotals.Compute returned 60, now returns 85.";
            }

            if (member.TryGetProperty("consequences", out JsonElement consequences)
                && consequences.ValueKind == JsonValueKind.Array
                && consequences.EnumerateArray().Any(item =>
                    String(item, "memberName").Contains("PaymentClient.ChargeAsync", StringComparison.Ordinal)))
            {
                return "A payment that previously succeeded after retries now fails prematurely.";
            }

            if (name.Contains("IsFreeShipping", StringComparison.Ordinal))
            {
                JsonElement changed = evidence.FirstOrDefault(item =>
                    CleanRendered(NullableString(item, "baseReturn") ?? string.Empty) == "false"
                    && CleanRendered(NullableString(item, "prReturn") ?? string.Empty) == "true");
                if (changed.ValueKind != JsonValueKind.Undefined)
                {
                    string total = CleanRendered(NullableString(changed, "baseArgs") ?? string.Empty)
                        .Replace("orderTotal=", string.Empty, StringComparison.Ordinal);
                    return "An order totaling " + total + " now qualifies for free shipping.";
                }
            }

            JsonElement fallback = evidence.FirstOrDefault(item =>
                !string.Equals(NullableString(item, "baseReturn"), NullableString(item, "prReturn"), StringComparison.Ordinal));
            string lead = fallback.ValueKind == JsonValueKind.Undefined
                ? "Its observed runtime behavior changed."
                : ShortMember(name) + " changed from "
                    + RenderValue(NullableString(fallback, "baseReturn"), NullableString(fallback, "baseException"))
                    + " to " + RenderValue(NullableString(fallback, "prReturn"), NullableString(fallback, "prException")) + ".";
            if (member.TryGetProperty("consequences", out JsonElement genericConsequences)
                && genericConsequences.ValueKind == JsonValueKind.Array)
            {
                JsonElement consequence = genericConsequences.EnumerateArray().FirstOrDefault();
                if (consequence.ValueKind != JsonValueKind.Undefined
                    && consequence.TryGetProperty("evidence", out JsonElement consequenceEvidence))
                {
                    lead += " " + ShortMember(String(consequence, "memberName")) + " changed from "
                        + RenderValue(NullableString(consequenceEvidence, "baseReturn"), NullableString(consequenceEvidence, "baseException"))
                        + " to " + RenderValue(NullableString(consequenceEvidence, "prReturn"), NullableString(consequenceEvidence, "prException")) + ".";
                }
            }

            return lead;
        }

        private static string UntestedLead(JsonElement member)
        {
            int untested = Int(member, "untestedCallSiteCount");
            int tests = Int(member, "distinctTestCount");
            if (untested == 1)
            {
                return "One of the " + tests + " tests that executed this did not assert on the change.";
            }

            return untested + " of the " + tests + " tests that executed this did not assert on the change.";
        }

        private static string? CausalHeadline(
            JsonElement member,
            CauseAnchor? cause,
            string? repository,
            string? headSha)
        {
            if (cause is null
                || repository is null
                || headSha is null
                || NullableString(member, "filePath") is not string effectPath
                || NullableInt(member, "line") is not int effectLine)
            {
                return null;
            }

            string causeLabel = cause.StartLine == cause.EndLine
                ? "Line " + cause.Line + " of " + Code(cause.FilePath)
                : "Lines " + cause.StartLine + "-" + cause.EndLine + " of " + Code(cause.FilePath);
            string causeFragment = cause.StartLine == cause.EndLine
                ? "#L" + cause.Line
                : "#L" + cause.StartLine + "-L" + cause.EndLine;
            string effectLabel = "line " + effectLine + " of " + Code(effectPath);
            string effectUrl = "https://github.com/" + repository + "/blob/" + headSha
                + "/" + effectPath + "#L" + effectLine;
            string causeUrl = "https://github.com/" + repository + "/blob/" + headSha
                + "/" + cause.FilePath + causeFragment;
            int untested = Int(member, "untestedCallSiteCount");
            int tests = Int(member, "distinctTestCount");
            string assertion = untested == tests
                ? "no test asserts on"
                : untested + " of " + tests + " tests did not assert on";
            return "[" + causeLabel + "](" + causeUrl + ") changed the behavior of ["
                + effectLabel + "](" + effectUrl + "), which " + assertion + ".";
        }

        private static string CoverageFooter(
            JsonElement findings,
            JsonElement member,
            string? repository,
            string? headSha)
        {
            JsonElement coverage = findings.GetProperty("coverage").GetProperty("summary");
            string source = Source(member);
            string renderedSource = repository is not null
                && headSha is not null
                && NullableString(member, "filePath") is string filePath
                && NullableInt(member, "line") is int line
                    ? "[" + Path.GetFileName(filePath) + ":" + line + "](https://github.com/"
                        + repository + "/blob/" + headSha + "/" + filePath + "#L" + line + ")"
                    : Code(source);
            return "_" + Int(coverage, "exercisedEditedFiles") + " of " + Int(coverage, "editedFiles")
                + " edited files exercised. " + renderedSource + "._";
        }

        private static void AppendUnobservedBoundaries(StringBuilder builder, JsonElement findings)
        {
            if (!findings.TryGetProperty("coverage", out JsonElement coverage)
                || !coverage.TryGetProperty("files", out JsonElement files))
            {
                return;
            }

            var skipped = files.EnumerateArray()
                .Where(file => file.TryGetProperty("skipped", out JsonElement members)
                    && members.ValueKind == JsonValueKind.Array)
                .SelectMany(file => file.GetProperty("skipped").EnumerateArray()
                    .Select(member => new { File = String(file, "filePath"), Member = member }))
                .ToArray();
            if (skipped.Length == 0)
            {
                return;
            }

            builder.AppendLine();
            builder.AppendLine("**" + skipped.Length + (skipped.Length == 1
                ? " member in this diff could not be observed.**"
                : " members in this diff could not be observed.**"));
            foreach (var group in skipped.GroupBy(
                item => new
                {
                    item.File,
                    Reason = String(item.Member, "skipReason"),
                    Detail = String(item.Member, "detail"),
                }))
            {
                builder.AppendLine("- " + Code(group.Key.File) + ": " + group.Count() + " × "
                    + Code(group.Key.Reason) + " - " + Escape(group.Key.Detail) + ".");
            }
        }

        private static void AppendBaselinePolicy(
            StringBuilder builder,
            JsonElement findings,
            string? repository,
            string? headSha)
        {
            if (!findings.TryGetProperty("baseline", out JsonElement baseline))
            {
                return;
            }

            string path = String(baseline, "path");
            string reference = repository is not null && headSha is not null
                ? "[`" + path + "`](https://github.com/" + repository + "/blob/" + headSha + "/" + path + ")"
                : Code(path);
            int suppressed = Int(baseline, "suppressedMembers");
            int suppressedCallSites = Int(baseline, "suppressedCallSites");
            int stale = baseline.GetProperty("staleEntries").GetArrayLength();
            int changed = baseline.GetProperty("digestMismatchEntries").GetArrayLength();
            int expired = baseline.GetProperty("expiredEntries").GetArrayLength();
            builder.AppendLine();
            builder.AppendLine("Baseline policy " + reference + ": **" + suppressed + " member(s), "
                + suppressedCallSites + " call site(s) suppressed**; **" + stale + " stale, "
                + changed + " changed, "
                + expired + " expired entry/entries**.");
            if (stale > 0)
            {
                builder.AppendLine("Stale baseline rules: " + string.Join(", ", baseline.GetProperty("staleEntries")
                    .EnumerateArray().Take(10).Select(entry => Code(String(entry, "ruleId")))) + ".");
            }
            if (changed > 0)
            {
                builder.AppendLine("Changed behavior since acknowledgement: " + string.Join(", ",
                    baseline.GetProperty("digestMismatchEntries").EnumerateArray().Take(10)
                        .Select(entry => Code(String(entry, "ruleId")))) + ".");
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

        private static string ShortMember(string memberName)
        {
            int paren = memberName.IndexOf('(');
            string qualified = paren < 0 ? memberName : memberName.Substring(0, paren);
            string[] parts = qualified.Split('.');
            return parts.Length < 2 ? qualified : parts[^2] + "." + parts[^1];
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
                    + string.Join(", ", unexercised.Take(20).Select(file => "`" + NullableString(file, "filePath") + "`"))
                    + (unexercised.Length > 20 ? ", and " + (unexercised.Length - 20) + " more in findings.json." : "."));
                builder.AppendLine("Zero observed calls are not evidence that these files did not change behavior.");
            }
        }

        private static void AppendMembers(
            StringBuilder builder,
            JsonElement findings,
            string attribution,
            string heading,
            IReadOnlyDictionary<string, ModelExplanation>? explanations)
        {
            if (!findings.TryGetProperty("members", out JsonElement members))
            {
                return;
            }

            JsonElement[] selected = members.EnumerateArray()
                .Where(member => String(member, "attribution") == attribution
                    && (attribution != "unexpected" || FindingPolicy.IsCommentEligible(findings, member)))
                .ToArray();
            if (selected.Length == 0)
            {
                return;
            }

            builder.AppendLine("### " + heading);
            foreach (JsonElement member in selected)
            {
                int start = builder.Length;
                AppendMemberEvidence(
                    builder,
                    member,
                    explanations is not null
                        && explanations.TryGetValue(String(member, "memberName"), out ModelExplanation? explanation)
                            ? explanation
                            : null);
                if (builder.Length > MemberEvidenceBudget)
                {
                    builder.Length = start;
                    builder.AppendLine();
                    builder.AppendLine("- " + Code(String(member, "memberName"))
                        + ": full deterministic evidence would exceed GitHub's comment limit; inspect findings.json in the workflow artifact.");
                }
            }
        }

        private static string RenderBudgetFallback(JsonElement findings, string marker)
        {
            JsonElement summary = findings.GetProperty("summary");
            JsonElement[] unexpected = FindingPolicy.EligibleUnexpected(findings);
            bool clean = unexpected.Length == 0;
            int gaps = unexpected.Count(item => Int(item, "untestedCallSiteCount") > 0);
            int covered = unexpected.Length - gaps;
            var builder = new StringBuilder();
            builder.AppendLine("## RealDiff runtime analysis");
            builder.AppendLine();
            builder.AppendLine(clean
                ? "**No unexpected behavior changes were found in the observed execution.**"
                : "**" + gaps + " unasserted behavior gap(s); " + covered
                    + " test-covered change(s), across " + BaselinePolicy.ActionableUnexpectedCallSites(summary)
                    + " call site(s).**");
            builder.AppendLine();
            builder.AppendLine("The complete deterministic evidence exceeded GitHub's comment limit. Inspect findings.json "
                + "in the workflow artifact. " + (clean
                    ? "The verdict remains clean only for the observed execution and reported coverage."
                    : "This is not a clean result."));
            if (findings.TryGetProperty("members", out JsonElement members))
            {
                foreach (JsonElement member in members.EnumerateArray()
                    .Where(item => String(item, "attribution") == "unexpected"
                        && FindingPolicy.IsCommentEligible(findings, item))
                    .Take(100))
                {
                    string line = "- " + Code(String(member, "memberName")) + " at " + Code(Source(member))
                        + ": " + Escape(String(member, "assertionReactionSummary"));
                    int remaining = MaxCommentLength - builder.Length - marker.Length - (Environment.NewLine.Length * 2);
                    if (remaining <= 0)
                    {
                        break;
                    }

                    builder.AppendLine(Truncate(line, remaining));
                }
            }

            AppendBaselinePolicy(builder, findings, repository: null, headSha: null);
            builder.AppendLine();
            builder.Append(marker);
            return builder.ToString();
        }

        private static string RenderInvalidBudgetFallback(
            JsonElement findings,
            string marker,
            string reason)
        {
            var builder = new StringBuilder();
            builder.AppendLine("## RealDiff: analysis could not complete");
            builder.AppendLine();
            builder.AppendLine("**No safety verdict was produced.** This is not a clean result.");
            builder.AppendLine();
            builder.AppendLine("> " + Truncate(reason, 2000).Replace("\n", "\n> "));
            builder.AppendLine();
            builder.AppendLine("The full refusal reason exceeded GitHub's comment limit; inspect findings.json in the workflow artifact.");
            builder.AppendLine();
            builder.Append(marker);
            return builder.ToString();
        }

        private static void AppendMemberEvidence(
            StringBuilder builder,
            JsonElement member,
            ModelExplanation? explanation)
        {
            string source = Source(member);
            builder.AppendLine();
            builder.AppendLine("<details>");
            builder.AppendLine("<summary><code>" + WebUtility.HtmlEncode(String(member, "memberName")) + "</code> - "
                + Int(member, "distinctTestCount") + (Int(member, "distinctTestCount") == 1 ? " test, " : " tests, ")
                + Int(member, "callSiteCount") + (Int(member, "callSiteCount") == 1 ? " call site" : " call sites")
                + "</summary>");
            builder.AppendLine();
            builder.AppendLine("**Observed values**");
            if (member.TryGetProperty("evidence", out JsonElement evidence))
            {
                foreach (JsonElement observation in evidence.EnumerateArray().Take(3))
                {
                    builder.AppendLine("- " + RenderObservation(member, observation));
                }
            }

            if (member.TryGetProperty("consequences", out JsonElement consequences)
                && consequences.ValueKind == JsonValueKind.Array
                && consequences.GetArrayLength() > 0)
            {
                builder.AppendLine();
                builder.AppendLine("**Downstream consequences**");
                foreach (JsonElement consequence in consequences.EnumerateArray().Take(3))
                {
                    builder.AppendLine("- " + RenderConsequence(consequence));
                }
            }

            builder.AppendLine();
            builder.AppendLine("**Tests and assertions**");
            builder.AppendLine("- **" + Escape(String(member, "assertionReactionSummary")) + "**");
            if (member.TryGetProperty("evidence", out evidence))
            {
                foreach (JsonElement observation in evidence.EnumerateArray()
                    .GroupBy(item => String(item, "testId"), StringComparer.Ordinal)
                    .Select(group => group.First()))
                {
                    string reaction = NullableBool(observation, "assertionReacted") switch
                    {
                        true => "an assertion reacted",
                        false => "no assertion reacted",
                        null => "assertion reaction was not available",
                    };
                    builder.AppendLine("- " + Code(String(observation, "testId")) + ": " + reaction + ".");
                }
            }

            builder.AppendLine();
            builder.AppendLine("**Call paths**");
            if (member.TryGetProperty("evidence", out evidence))
            {
                foreach (JsonElement observation in evidence.EnumerateArray().Take(3))
                {
                    AppendCallPaths(builder, observation);
                }
            }

            builder.AppendLine();
            builder.AppendLine("**Source**");
            builder.AppendLine("- " + Code(source));
            builder.AppendLine();
            builder.AppendLine("**Changed files on recorded paths**");
            string[] changedFiles = Strings(member, "changedFilesReachingMember").ToArray();
            builder.AppendLine(changedFiles.Length == 0
                ? "- None of these recorded test-to-member paths includes an edited file."
                : "- " + string.Join(", ", changedFiles.Select(Code)));
            if (explanation is not null)
            {
                builder.AppendLine();
                builder.AppendLine("**Optional model explanation** (" + Code(explanation.Model)
                    + ", accepted only after literal and exact-citation grounding checks)");
                builder.AppendLine("- Why: " + explanation.Why);
                builder.AppendLine("- Suggested test: " + explanation.Test);
            }

            builder.AppendLine();
            builder.AppendLine("</details>");
        }

        private static string RenderObservation(JsonElement member, JsonElement observation)
        {
            if (NullableInt(observation, "ordinal") is not int ordinal || ordinal < 0)
            {
                return Code(String(observation, "testId")) + ": " + Code(String(observation, "kind"))
                    + " - " + Escape(String(observation, "detail"))
                    + ". No single call occurrence can be aligned, so concrete values and paths are unavailable.";
            }

            string baseInvocation = Invocation(
                String(member, "memberName"),
                NullableString(observation, "baseArgs"));
            string prInvocation = Invocation(
                String(member, "memberName"),
                NullableString(observation, "prArgs"));
            string baseResult = RenderValue(
                NullableString(observation, "baseReturn"),
                NullableString(observation, "baseException"));
            string prResult = RenderValue(
                NullableString(observation, "prReturn"),
                NullableString(observation, "prException"));
            string invocation = string.Equals(baseInvocation, prInvocation, StringComparison.Ordinal)
                ? Code(baseInvocation)
                : "base " + Code(baseInvocation) + ", PR " + Code(prInvocation);
            return Code(String(observation, "testId")) + ": " + invocation + " returned "
                + baseResult + "; PR returns " + prResult + ".";
        }

        private static string RenderConsequence(JsonElement consequence)
        {
            JsonElement observation = consequence.GetProperty("evidence");
            string memberName = String(consequence, "memberName");
            string baseResult = RenderValue(
                NullableString(observation, "baseReturn"),
                NullableString(observation, "baseException"));
            string prResult = RenderValue(
                NullableString(observation, "prReturn"),
                NullableString(observation, "prException"));
            return Code(String(observation, "testId")) + ": " + Code(memberName)
                + " returned " + baseResult + "; PR returns " + prResult + ".";
        }

        private static void AppendCallPaths(StringBuilder builder, JsonElement observation)
        {
            string test = String(observation, "testId");
            string basePath = RenderPath(observation, "baseCallPath");
            string prPath = RenderPath(observation, "prCallPath");
            if (string.Equals(basePath, prPath, StringComparison.Ordinal))
            {
                builder.AppendLine("- " + Code(test) + " (base and PR): " + basePath);
                return;
            }

            builder.AppendLine("- " + Code(test) + " (base): " + basePath);
            builder.AppendLine("- " + Code(test) + " (PR): " + prPath);
        }

        private static string RenderPath(JsonElement observation, string property)
        {
            if (!observation.TryGetProperty(property, out JsonElement path)
                || path.ValueKind != JsonValueKind.Array
                || path.GetArrayLength() == 0)
            {
                return "path unavailable";
            }

            return string.Join(" -> ", path.EnumerateArray().Select(node => Code(String(node, "memberName"))));
        }

        private static string Invocation(string memberName, string? args)
        {
            int paren = memberName.IndexOf('(');
            string withoutParameters = paren < 0 ? memberName : memberName.Substring(0, paren);
            int dot = withoutParameters.LastIndexOf('.');
            string method = dot < 0 ? withoutParameters : withoutParameters.Substring(dot + 1);
            return method + "(" + CleanRendered(args ?? "arguments not rendered") + ")";
        }

        private static string Source(JsonElement member)
        {
            string source = NullableString(member, "filePath") ?? "unresolved";
            int? line = NullableInt(member, "line");
            return line is null ? source : source + ":" + line;
        }

        private static string RenderMember(
            JsonElement member,
            string marker,
            ModelExplanation? explanation)
        {
            var builder = new StringBuilder();
            builder.AppendLine("### Unexpected runtime behavior change");
            builder.AppendLine();
            builder.AppendLine("`" + String(member, "memberName") + "`");
            builder.AppendLine();
            builder.AppendLine("This member is in a file the PR did **not** modify, but its runtime behavior changed.");
            builder.AppendLine();
            AppendMemberEvidence(builder, member, explanation);
            foreach (string reason in Strings(member, "downgradeReasons").Take(2))
            {
                builder.AppendLine("- Downgrade: " + reason);
            }

            builder.AppendLine();
            builder.Append(marker);
            return builder.ToString();
        }

        private static string RenderValue(string? value, string? exception) =>
            exception is not null ? "exception " + Code(exception) : Code(CleanRendered(value ?? "(not rendered)"));

        private static string CleanRendered(string value)
        {
            string clean = Regex.Replace(value, @"<(?<name>[^>]+)>k__BackingField=", "${name}=");
            return Regex.Replace(clean, @"\b(?:Primitive|StructuralFields):", string.Empty);
        }

        private static string Code(string text) => "`" + text.Replace("`", "'", StringComparison.Ordinal) + "`";

        private static string MemberKey(string memberName)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(memberName));
            return Convert.ToHexString(hash).Substring(0, 16).ToLowerInvariant();
        }

        private static string RequiredEnvironment(string name)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new CliException("GitHub posting requires " + name + ".");
            }

            return value;
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

        private static bool? NullableBool(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value)
                ? value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null,
                }
                : null;

        private static IEnumerable<string> Strings(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Select(value => value.GetString() ?? string.Empty)
                : Enumerable.Empty<string>();

        private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ").Replace("\n", " ");

        private static string Truncate(string text) => Truncate(text, 1000);

        private static string Truncate(string text, int length) => text.Length <= length ? text : text.Substring(0, length);

        private sealed record ExistingComment(
            long Id,
            string Body,
            string? Path = null,
            int? Line = null,
            string? CommitId = null);

        private sealed record GitHubContext(int PullRequestNumber, string Repository, string HeadSha, bool IsFork);

        private sealed record CauseAnchor(
            string FilePath,
            int Line,
            int StartLine,
            int EndLine,
            int HunkStart,
            string AddedLine,
            bool MultipleCandidates);

        private sealed class GitHubApiException : Exception
        {
            internal GitHubApiException(HttpStatusCode statusCode, string response)
                : base("GitHub REST " + (int)statusCode + " " + statusCode + ": " + response)
            {
                StatusCode = statusCode;
                Response = response;
            }

            internal HttpStatusCode StatusCode { get; }

            internal string Response { get; }
        }
    }
}
