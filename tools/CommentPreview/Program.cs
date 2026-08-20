using System.Text.Json;
using BehaviorDiff.Cli;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: BehaviorDiff.CommentPreview <findings.json>");
    return 2;
}

using JsonDocument findings = JsonDocument.Parse(File.ReadAllText(args[0]));
Console.Write(GitHubPoster.RenderSummary(
    findings.RootElement,
    "<!-- behaviordiff:comment-preview -->",
    Array.Empty<string>()));
return 0;
