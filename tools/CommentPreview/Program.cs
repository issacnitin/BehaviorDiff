using System.Text.Json;
using BehaviorDiff.Cli;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("usage: BehaviorDiff.CommentPreview [--provider=github|azuredevops] <findings.json>");
    return 2;
}

if (args.Length == 2 && !args[0].StartsWith("--provider=", StringComparison.Ordinal))
{
    Console.Error.WriteLine("usage: BehaviorDiff.CommentPreview [--provider=github|azuredevops] <findings.json>");
    return 2;
}

string provider = args.Length == 2 ? args[0].Substring("--provider=".Length) : "github";
string path = args[^1];
using JsonDocument findings = JsonDocument.Parse(File.ReadAllText(path));
if (provider == "azuredevops")
{
    Console.Write(AzureDevOpsPoster.RenderSummary(findings.RootElement, "<!-- behaviordiff:comment-preview -->"));
}
else if (provider == "github")
{
    Console.Write(GitHubPoster.RenderSummary(
        findings.RootElement,
        "<!-- behaviordiff:comment-preview -->",
        Array.Empty<string>(),
        repository: "acme/repo",
        headSha: "proof-pr"));
}
else
{
    Console.Error.WriteLine("provider must be github or azuredevops");
    return 2;
}

return 0;
