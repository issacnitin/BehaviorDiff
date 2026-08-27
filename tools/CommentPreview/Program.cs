using System.Text.Json;
using RealDiff.Cli;
using RealDiff.Contracts;

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

if (args is ["--verify-unobserved-boundaries"])
{
        using JsonDocument proof = JsonDocument.Parse("""
                {
                    "status": "analyzed",
                    "summary": {
                        "suppressedMembers": 0,
                        "unexpectedMembers": 0,
                        "editedFiles": 1,
                        "tracedMembers": 0,
                        "observedCallSites": 0
                    },
                    "coverage": {
                        "summary": { "editedFiles": 1, "exercisedEditedFiles": 0 },
                        "files": [{
                            "filePath": "src/Example.cs",
                            "skipped": [{
                                "methodFullName": "Example..cctor()",
                                "skipReason": "Unobservable",
                                "detail": "DotNet: TypeInitializer",
                                "line": 4
                            }]
                        }]
                    },
                    "members": []
                }
                """);
        string rendered = GitHubPoster.RenderSummary(
                proof.RootElement,
                "<!-- realdiff:comment-preview -->",
                Array.Empty<string>());
        if (!rendered.Contains("1 member in this diff could not be observed", StringComparison.Ordinal)
                || !rendered.Contains("`Unobservable` - DotNet: TypeInitializer", StringComparison.Ordinal))
        {
                Console.Error.WriteLine("Unobserved boundary comment proof failed.");
                return 1;
        }

        Console.WriteLine("Unobserved boundary comment proof: PASS");
        return 0;
}

    if (args is ["--verify-dotnet-initializer-manifest"])
    {
        var expected = new ManifestEntry
        {
            Assembly = "Example",
            MethodFullName = "Example..cctor()",
            Status = PatchStatus.Skipped,
            FilePath = "src/Example.cs",
            Line = 4,
            SkipReason = NeutralSkipReason.Unobservable,
            Detail = "DotNet: TypeInitializer",
            SourceResolution = SourceResolution.SequencePoints,
        };
        string line = ManifestNdjson.ToLine(expected);
        bool parsed = ManifestNdjson.TryParseLine(
            line,
            out ManifestEntry? actual,
            out _,
            out _,
            out _,
            out _,
            out string? error);
        if (!parsed
            || actual?.FilePath != expected.FilePath
            || actual.Line != expected.Line
            || actual.SkipReason != expected.SkipReason
            || actual.Detail != expected.Detail)
        {
            Console.Error.WriteLine(".NET initializer manifest proof failed: " + error);
            return 1;
        }

        Console.WriteLine(".NET initializer manifest proof: PASS");
        return 0;
    }

if (args.Length is < 1 or > 2)
{
        Console.Error.WriteLine("usage: RealDiff.CommentPreview [--provider=github|azuredevops] <findings.json> | --verify-github-markers | --verify-unobserved-boundaries | --verify-dotnet-initializer-manifest");
    return 2;
}

if (args.Length == 2 && !args[0].StartsWith("--provider=", StringComparison.Ordinal))
{
    Console.Error.WriteLine("usage: RealDiff.CommentPreview [--provider=github|azuredevops] <findings.json> | --verify-github-markers | --verify-unobserved-boundaries | --verify-dotnet-initializer-manifest");
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
