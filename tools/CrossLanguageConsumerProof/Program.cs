using System.Text;
using System.Text.Json;
using BehaviorDiff.Cli;
using BehaviorDiff.Mcp;

namespace BehaviorDiff.CrossLanguageConsumerProof;

internal static class Program
{
    private static readonly DateTimeOffset FixedTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static int Main(string[] args)
    {
        try
        {
            ArtifactPair[] artifacts = ParseArguments(args);
            string originalRoot = RunStore.Root;
            string proofRoot = Path.Combine(Path.GetTempPath(), "behaviordiff-cross-language-consumer-proof-runs");
            try
            {
                Directory.Delete(proofRoot, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }

            RunStore.Root = proofRoot;
            try
            {
                foreach (ArtifactPair artifact in artifacts)
                {
                    VerifyLanguage(artifact);
                }
            }
            finally
            {
                RunStore.Root = originalRoot;
                Directory.Delete(proofRoot, recursive: true);
            }

            Console.WriteLine("Deterministic explainer: GitHubPoster is the production deterministic comment renderer; renderer stability accepted.");
            Console.WriteLine("Network coverage: Azure renderer/payload only; no live post and no live Anthropic call were performed.");
            Console.WriteLine("verify-cross-language-consumers: PASS");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("verify-cross-language-consumers: FAIL: " + exception.Message);
            return 1;
        }
    }

    private static ArtifactPair[] ParseArguments(string[] args)
    {
        if (args.Length == 2)
        {
            return new[]
            {
                FromDirectory(LanguageSpec.Java, args[0]),
                FromDirectory(LanguageSpec.Node, args[1]),
            };
        }

        if (args.Length == 4)
        {
            return new[]
            {
                FromFiles(LanguageSpec.Java, args[0], args[1]),
                FromFiles(LanguageSpec.Node, args[2], args[3]),
            };
        }

        throw new ArgumentException(
            "usage: BehaviorDiff.CrossLanguageConsumerProof <java-artifact-dir> <node-artifact-dir>"
            + Environment.NewLine
            + "   or: BehaviorDiff.CrossLanguageConsumerProof <java-findings> <java-divergence> <node-findings> <node-divergence>");
    }

    private static ArtifactPair FromDirectory(LanguageSpec language, string directory) =>
        FromFiles(
            language,
            Path.Combine(Path.GetFullPath(directory), "findings.json"),
            Path.Combine(Path.GetFullPath(directory), "divergence-set.json"));

    private static ArtifactPair FromFiles(LanguageSpec language, string findings, string divergences)
    {
        string findingsPath = RequiredFile(findings);
        string divergencePath = RequiredFile(divergences);
        return new ArtifactPair(language, findingsPath, divergencePath);
    }

    private static string RequiredFile(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return File.Exists(fullPath)
            ? fullPath
            : throw new FileNotFoundException("Required proof artifact was not found.", fullPath);
    }

    private static void VerifyLanguage(ArtifactPair artifact)
    {
        using JsonDocument findings = JsonDocument.Parse(File.ReadAllBytes(artifact.FindingsPath));
        Assert(
            findings.RootElement.TryGetProperty("status", out JsonElement status)
            && status.GetString() == "analyzed",
            artifact.Language.Name + " findings are not analyzed");

        JsonElement selected = SelectMember(findings.RootElement, artifact.Language);
        string memberName = selected.GetProperty("memberName").GetString() ?? string.Empty;
        string filePath = selected.GetProperty("filePath").GetString() ?? string.Empty;
        string marker = "<!-- behaviordiff:cross-language-consumer-proof:" + artifact.Language.Name + " -->";

        string githubFirst = GitHubPoster.RenderSummary(
            findings.RootElement,
            marker,
            Array.Empty<string>());
        string githubSecond = GitHubPoster.RenderSummary(
            findings.RootElement,
            marker,
            Array.Empty<string>());
        AssertByteIdentical(githubFirst, githubSecond, artifact.Language.Name + " GitHub summary");

        string azureFirst = AzureDevOpsPoster.RenderSummary(findings.RootElement, marker);
        string azureSecond = AzureDevOpsPoster.RenderSummary(findings.RootElement, marker);
        AssertByteIdentical(azureFirst, azureSecond, artifact.Language.Name + " Azure summary");

        AssertComment(githubFirst, artifact.Language, marker, filePath, "GitHub");
        AssertComment(azureFirst, artifact.Language, marker, filePath, "Azure renderer/payload");
        Assert(filePath.EndsWith(artifact.Language.Extension, StringComparison.Ordinal),
            artifact.Language.Name + " selected member lost its source extension: " + filePath);

        McpResult mcp = VerifyMcp(artifact, memberName);
        Console.WriteLine("=== " + artifact.Language.DisplayName + " consumer proof ===");
        Console.WriteLine("  GitHub deterministic length       : " + Encoding.UTF8.GetByteCount(githubFirst));
        Console.WriteLine("  Azure renderer/payload length     : " + Encoding.UTF8.GetByteCount(azureFirst));
        Console.WriteLine("  MCP unexpected members            : " + mcp.UnexpectedMembers);
        Console.WriteLine("  MCP untested members              : " + mcp.UntestedMembers);
        Console.WriteLine("  MCP selected member               : " + memberName);
        Console.WriteLine("  MCP call path nodes               : " + mcp.CallPathNodes);
        Console.WriteLine("  MCP test                          : " + mcp.TestName);
        Console.WriteLine("  MCP call path                     : " + mcp.CallPath);
    }

    private static JsonElement SelectMember(JsonElement findings, LanguageSpec language)
    {
        JsonElement[] matches = findings.GetProperty("members").EnumerateArray()
            .Where(member => member.GetProperty("attribution").GetString() == "unexpected")
            .Where(member => language.MemberMatch(member.GetProperty("memberName").GetString() ?? string.Empty))
            .ToArray();
        Assert(matches.Length > 0, language.Name + " findings contain no expected language-shaped unexpected member");
        return matches[0];
    }

    private static void AssertComment(
        string comment,
        LanguageSpec language,
        string marker,
        string filePath,
        string renderer)
    {
        Assert(Encoding.UTF8.GetByteCount(comment) >= 100, language.Name + " " + renderer + " markdown is unreasonably short");
        Assert(comment.Contains("BehaviorDiff", StringComparison.Ordinal), language.Name + " " + renderer + " markdown has no heading");
        Assert(comment.Contains(marker, StringComparison.Ordinal), language.Name + " " + renderer + " markdown lost its marker");
        Assert(comment.Contains(language.CommentMemberToken, StringComparison.Ordinal),
            language.Name + " " + renderer + " markdown lost member pattern " + language.CommentMemberToken);
        Assert(comment.Contains(language.Extension, StringComparison.Ordinal),
            language.Name + " " + renderer + " markdown lost source extension " + language.Extension);
        Assert(comment.Contains(filePath, StringComparison.Ordinal),
            language.Name + " " + renderer + " markdown lost source path " + filePath);
        Assert(comment.Contains(language.TestEvidenceToken, StringComparison.Ordinal),
            language.Name + " " + renderer + " markdown lost test evidence " + language.TestEvidenceToken);
        Assert(!comment.Contains("SampleApp", StringComparison.OrdinalIgnoreCase),
            language.Name + " " + renderer + " markdown contains SampleApp contamination");
        if (language.Name == "java")
        {
            Assert(comment.Contains("(I)I", StringComparison.Ordinal),
                language.Name + " " + renderer + " markdown lost JVM signature evidence");
        }
    }

    private static McpResult VerifyMcp(ArtifactPair artifact, string memberName)
    {
        string runId = artifact.Language.Name + "-cross-language-consumer-proof";
        var record = new RunRecord
        {
            RunId = runId,
            Status = "complete",
            Phase = "complete",
            Progress = 100,
            Error = null,
            RepoPath = Path.GetDirectoryName(artifact.FindingsPath) ?? string.Empty,
            BaseRef = artifact.Language.Name + "-proof-base",
            PrRef = artifact.Language.Name + "-proof-pr",
            ExitCode = 0,
            StartedUtc = FixedTime,
            CompletedUtc = FixedTime,
        };
        RunStore.Save(record);
        File.Copy(artifact.FindingsPath, Path.Combine(RunStore.Directory_(runId), "findings.json"), overwrite: true);
        File.Copy(artifact.DivergencePath, Path.Combine(RunStore.Directory_(runId), "divergence-set.json"), overwrite: true);

        using JsonDocument listed = ParseMcp(BehaviorDiffTools.ListDivergences(runId, "unexpected"), "list_divergences");
        int unexpectedMembers = listed.RootElement.GetProperty("total_members").GetInt32();
        Assert(unexpectedMembers > 0, artifact.Language.Name + " MCP list returned no unexpected members");
        Assert(listed.RootElement.GetProperty("members").EnumerateArray().Any(member =>
                member.GetProperty("member").GetString() == memberName
                && (member.GetProperty("file").GetString() ?? string.Empty).EndsWith(artifact.Language.Extension, StringComparison.Ordinal)),
            artifact.Language.Name + " MCP list did not preserve the selected member and source path");

        using JsonDocument detail = ParseMcp(BehaviorDiffTools.GetDivergence(runId, memberName), "get_divergence");
        Assert(detail.RootElement.GetProperty("member").GetString() == memberName,
            artifact.Language.Name + " MCP detail changed the selected member name");
        Assert((detail.RootElement.GetProperty("file").GetString() ?? string.Empty).EndsWith(artifact.Language.Extension, StringComparison.Ordinal),
            artifact.Language.Name + " MCP detail changed the source path");

        using JsonDocument callPath = ParseMcp(BehaviorDiffTools.GetCallPath(runId, memberName), "get_call_path");
        string testName = callPath.RootElement.GetProperty("test").GetString() ?? string.Empty;
        JsonElement[] path = callPath.RootElement.GetProperty("path").EnumerateArray().ToArray();
        Assert(path.Length > 0, artifact.Language.Name + " MCP call path is empty");
        Assert(path.Any(node => node.GetProperty("member").GetString() == memberName),
            artifact.Language.Name + " MCP call path lost the selected member");
        Assert(path.Any(node => (node.GetProperty("file").GetString() ?? string.Empty).EndsWith(artifact.Language.Extension, StringComparison.Ordinal)),
            artifact.Language.Name + " MCP call path lost language source paths");
        Assert(artifact.Language.TestMatch(testName),
            artifact.Language.Name + " MCP test name was not preserved: " + testName);

        using JsonDocument untested = ParseMcp(BehaviorDiffTools.GetUntestedDivergences(runId), "get_untested_divergences");
        int untestedMembers = untested.RootElement.GetProperty("total_members").GetInt32();
        Assert(untestedMembers >= 0 && untested.RootElement.GetProperty("members").ValueKind == JsonValueKind.Array,
            artifact.Language.Name + " MCP untested query returned an invalid count");

        string combined = listed.RootElement.GetRawText()
            + detail.RootElement.GetRawText()
            + callPath.RootElement.GetRawText()
            + untested.RootElement.GetRawText();
        Assert(!combined.Contains("SampleApp", StringComparison.OrdinalIgnoreCase),
            artifact.Language.Name + " MCP responses contain SampleApp contamination");

        return new McpResult(
            unexpectedMembers,
            untestedMembers,
            path.Length,
            testName,
            string.Join(" -> ", path.Select(node => node.GetProperty("member").GetString() ?? string.Empty)));
    }

    private static JsonDocument ParseMcp(string json, string query)
    {
        JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("error", out JsonElement error))
        {
            string message = error.GetString() ?? "unknown error";
            document.Dispose();
            throw new InvalidOperationException(query + " returned an error: " + message);
        }

        return document;
    }

    private static void AssertByteIdentical(string first, string second, string label) =>
        Assert(
            Encoding.UTF8.GetBytes(first).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(second)),
            label + " was not byte-identical across repeated renders");

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record LanguageSpec(
        string Name,
        string DisplayName,
        string Extension,
        string CommentMemberToken,
        string TestEvidenceToken,
        Func<string, bool> MemberMatch,
        Func<string, bool> TestMatch)
    {
        internal static LanguageSpec Java { get; } = new(
            "java",
            "Java",
            ".java",
            "Subject.observe",
            "ReferenceTests.volume(I)V",
            member => member.Contains("Subject.observe", StringComparison.Ordinal),
            test => test.Contains("ReferenceTests", StringComparison.Ordinal) && test.Contains("(I)V", StringComparison.Ordinal));

        internal static LanguageSpec Node { get; } = new(
            "node",
            "Node",
            ".js",
            "subject.js#",
            "node-reference/promise-chain",
            member => member.Contains("subject.js#AsyncSettlement.settle", StringComparison.Ordinal)
                || member.Contains("subject.js#promiseWorkflow", StringComparison.Ordinal),
            test => test.Contains("node-reference/", StringComparison.Ordinal));
    }

    private sealed record ArtifactPair(LanguageSpec Language, string FindingsPath, string DivergencePath);

    private sealed record McpResult(
        int UnexpectedMembers,
        int UntestedMembers,
        int CallPathNodes,
        string TestName,
        string CallPath);
}