using System.Text.Json;
using RealDiff.Cli;

if (args is ["--verify-github-markers"])
{
    const string marker = "<!-- realdiff:github:pr:1:summary -->";
    if (!GitHubPoster.HasMarker(marker, marker)
        || !GitHubPoster.HasMarker("<!-- behaviordiff:github:pr:1:summary -->", marker)
        || GitHubPoster.HasMarker("<!-- behaviordiff:github:pr:2:summary -->", marker))
    {
        Console.Error.WriteLine("GitHub marker compatibility proof failed.");
        return 1;
    }

    Console.WriteLine("GitHub marker compatibility proof: PASS");
    return 0;
}

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("usage: RealDiff.CommentPreview [--provider=github|azuredevops] <findings.json> | --verify-github-markers");
    return 2;
}

if (args.Length == 2 && !args[0].StartsWith("--provider=", StringComparison.Ordinal))
{
    Console.Error.WriteLine("usage: RealDiff.CommentPreview [--provider=github|azuredevops] <findings.json> | --verify-github-markers");
    return 2;
}

string provider = args.Length == 2 ? args[0].Substring("--provider=".Length) : "github";
string path = args[^1];
using JsonDocument findings = JsonDocument.Parse(File.ReadAllText(path));
if (provider == "azuredevops")
{
    Console.Write(AzureDevOpsPoster.RenderSummary(findings.RootElement, "<!-- realdiff:comment-preview -->"));
}
else if (provider == "github")
{
    Console.Write(GitHubPoster.RenderSummary(
        findings.RootElement,
        "<!-- realdiff:comment-preview -->",
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
