using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BehaviorDiff.Cli
{
    internal enum RepositoryLanguage
    {
        DotNet,
        Java,
        Node,
    }

    internal sealed class LanguageDetection
    {
        internal RepositoryLanguage Language { get; init; }

        internal string EntryPoint { get; init; } = string.Empty;

        internal string Evidence { get; init; } = string.Empty;
    }

    internal static class LanguageDetector
    {
        internal static LanguageDetection Detect(string repository)
        {
            string root = Path.GetFullPath(repository);
            var candidates = RootCandidates(root);
            if (candidates.Count == 0)
            {
                candidates = RecursiveCandidates(root);
            }

            if (candidates.Count == 0)
            {
                throw new CliException(
                    "Could not detect a supported repository language. Expected a solution/project, pom.xml, or package.json.",
                    ExitCodes.RunInvalid);
            }

            RepositoryLanguage[] languages = candidates.Select(candidate => candidate.Language).Distinct().ToArray();
            if (languages.Length != 1)
            {
                throw new CliException(
                    "Repository language is ambiguous: "
                    + string.Join(", ", candidates.Select(candidate => candidate.Evidence).OrderBy(value => value, StringComparer.Ordinal))
                    + ". Keep one root build entry point or select a language explicitly.",
                    ExitCodes.RunInvalid);
            }

            return new LanguageDetection
            {
                Language = languages[0],
                EntryPoint = candidates[0].EntryPoint,
                Evidence = string.Join(", ", candidates
                    .Where(candidate => candidate.Language == languages[0])
                    .Select(candidate => candidate.Evidence)
                    .OrderBy(value => value, StringComparer.Ordinal)),
            };
        }

        private static List<LanguageDetection> RootCandidates(string root)
        {
            var candidates = new List<LanguageDetection>();
            AddDotNet(candidates, root, Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(root, "*.csproj", SearchOption.TopDirectoryOnly)));
            Add(candidates, root, RepositoryLanguage.Java, Existing(root, "pom.xml"));
            Add(candidates, root, RepositoryLanguage.Node, Existing(root, "package.json"));
            return candidates;
        }

        private static List<LanguageDetection> RecursiveCandidates(string root)
        {
            var candidates = new List<LanguageDetection>();
            AddDotNet(candidates, root, Find(root, "*.sln").Concat(Find(root, "*.csproj")));
            Add(candidates, root, RepositoryLanguage.Java, Find(root, "pom.xml"));
            Add(candidates, root, RepositoryLanguage.Node, Find(root, "package.json")
                .Where(path => !IsBuildOutput(path)));
            return candidates;
        }

        private static void AddDotNet(List<LanguageDetection> candidates, string root, IEnumerable<string> paths) =>
            Add(candidates, root, RepositoryLanguage.DotNet, paths);

        private static void Add(
            List<LanguageDetection> candidates,
            string root,
            RepositoryLanguage language,
            IEnumerable<string> paths)
        {
            string? evidence = paths.FirstOrDefault();
            if (evidence != null)
            {
                candidates.Add(new LanguageDetection
                {
                    Language = language,
                    EntryPoint = Path.GetFullPath(evidence),
                    Evidence = Path.GetRelativePath(root, evidence).Replace('\\', '/'),
                });
            }
        }

        private static IEnumerable<string> Existing(string root, params string[] names) =>
            names.Select(name => Path.Combine(root, name)).Where(File.Exists);

        private static IEnumerable<string> Find(string root, string pattern) =>
            Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).Where(path => !IsBuildOutput(path));

        private static bool IsBuildOutput(string path)
        {
            string normalized = path.Replace('\\', '/');
            return normalized.Contains("/bin/", StringComparison.Ordinal)
                || normalized.Contains("/obj/", StringComparison.Ordinal)
                || normalized.Contains("/target/", StringComparison.Ordinal)
                || normalized.Contains("/node_modules/", StringComparison.Ordinal)
                || normalized.Contains("/dist/", StringComparison.Ordinal);
        }
    }
}