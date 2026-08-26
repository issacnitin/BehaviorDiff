using System.Text.Json;
using RealDiff.Cli;

if (args.Length != 3)
{
    Console.Error.WriteLine("usage: RealDiff.AnthropicLive <findings.json> <changed-file> <patch-file>");
    return 2;
}

string? apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("ANTHROPIC_API_KEY is not set. Enter it directly in the terminal environment, then rerun.");
    return 3;
}

using JsonDocument findings = JsonDocument.Parse(File.ReadAllText(args[0]));
JsonElement member = findings.RootElement.GetProperty("members").EnumerateArray()
    .First(item => item.GetProperty("attribution").GetString() == "unexpected");
JsonElement[] related = findings.RootElement.GetProperty("members").EnumerateArray()
    .Where(item => item.GetProperty("attribution").GetString() == "unexpected"
        && item.GetProperty("memberName").GetString() != member.GetProperty("memberName").GetString())
    .ToArray();
var patches = new[] { new ChangedFilePatch(args[1], File.ReadAllText(args[2])) };

ExplanationAttempt attempt;
using (var explainer = new AnthropicExplainer(apiKey))
{
    attempt = await explainer.ExplainWithDiagnosticsAsync(member, patches, related);
}

Console.WriteLine("=== RAW ANTHROPIC RESPONSE ===");
Console.WriteLine(attempt.RawResponse);
Console.WriteLine();
Console.WriteLine("=== VALIDATION ===");
Console.WriteLine(attempt.Validation);
Console.WriteLine();
Console.WriteLine("=== FINAL RENDERED COMMENT ===");
var explanations = new Dictionary<string, ModelExplanation>(StringComparer.Ordinal);
if (attempt.Explanation is not null)
{
    explanations[member.GetProperty("memberName").GetString() ?? string.Empty] = attempt.Explanation;
}

Console.WriteLine(GitHubPoster.RenderSummary(
    findings.RootElement,
    "<!-- realdiff:real-anthropic-preview -->",
    new[] { (member.GetProperty("filePath").GetString() ?? "unresolved") + ":" + member.GetProperty("line").GetInt32() },
    explanations));
return attempt.Explanation is null ? 1 : 0;
