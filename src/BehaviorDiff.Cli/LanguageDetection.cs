using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BehaviorDiff.Cli
{
    internal enum RepositoryLanguage
    {
        DotNet,
        Java,
        Node,
        Go,
        Rust,
    }

    internal sealed class LanguageDetection
    {
        internal RepositoryLanguage Language { get; init; }

        internal string EntryPoint { get; init; } = string.Empty;

        internal string Evidence { get; init; } = string.Empty;

        internal string WorkDirectory { get; init; } = string.Empty;

        internal string Workdir { get; init; } = ".";

        internal string BuildCommand { get; init; } = string.Empty;

        internal string TestCommand { get; init; } = string.Empty;

        internal string[] TestProjects { get; init; } = Array.Empty<string>();

        internal string[] IncludeNamespaces { get; init; } = Array.Empty<string>();

        internal string[] ExcludeNamespaces { get; init; } = Array.Empty<string>();

        internal LoadedRepositoryConfig Config { get; init; } = new();

        internal bool HasCustomBuild => !string.IsNullOrWhiteSpace(Config.Value.Build);

        internal bool HasCustomTest => !string.IsNullOrWhiteSpace(Config.Value.Test);
    }

    internal static class LanguageDetector
    {
        private static readonly Regex JavaPackage = new(
            @"^\s*package\s+([A-Za-z_$][A-Za-z0-9_$]*(?:\.[A-Za-z_$][A-Za-z0-9_$]*)*)\s*;",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

        internal static LanguageDetection Detect(string repository)
        {
            LoadedRepositoryConfig config = RepositoryConfigLoader.Load(repository);
            string root = config.RepositoryRoot;
            string workDirectory = RepositoryConfigLoader.ResolveWorkDirectory(config);
            List<LanguageDetection> candidates = RootCandidates(workDirectory);
            if (candidates.Count == 0)
            {
                candidates = RecursiveCandidates(workDirectory);
            }

            RepositoryLanguage? configuredLanguage = null;
            if (!string.IsNullOrWhiteSpace(config.Value.Language))
            {
                TryParseLanguage(config.Value.Language!, out RepositoryLanguage parsed);
                configuredLanguage = parsed;
                candidates = candidates.Where(candidate => candidate.Language == parsed).ToList();
            }

            if (candidates.Count == 0 && configuredLanguage is null)
            {
                throw DetectionFailure(
                    "Could not detect a supported repository language. Expected a solution/project, pom.xml, package.json, go.mod, or Cargo.toml.");
            }

            RepositoryLanguage language;
            LanguageDetection? marker = null;
            if (configuredLanguage is RepositoryLanguage explicitLanguage)
            {
                language = explicitLanguage;
                marker = candidates.Count == 1 ? candidates[0] : null;
                if (marker is null && (!HasBothCommands(config.Value) || candidates.Count > 1))
                {
                    throw DetectionFailure(
                        candidates.Count == 0
                            ? "The configured language has no conventional build entry point in workdir '" + Relative(root, workDirectory) + "'."
                            : "The configured workdir contains multiple " + Name(language) + " build entry points: "
                                + string.Join(", ", candidates.Select(candidate => candidate.Evidence).OrderBy(value => value, StringComparer.Ordinal)) + ".");
                }
            }
            else
            {
                RepositoryLanguage[] languages = candidates.Select(candidate => candidate.Language).Distinct().ToArray();
                if (languages.Length != 1)
                {
                    throw DetectionFailure(
                        "Repository language is ambiguous: "
                        + string.Join(", ", candidates.Select(candidate => candidate.Evidence).OrderBy(value => value, StringComparer.Ordinal)) + ".");
                }
                if (candidates.Count != 1)
                {
                    throw DetectionFailure(
                        "Multiple " + Name(languages[0]) + " build entry points were found: "
                        + string.Join(", ", candidates.Select(candidate => candidate.Evidence).OrderBy(value => value, StringComparer.Ordinal)) + ".");
                }
                language = languages[0];
                marker = candidates[0];
            }

            string evidence = marker?.Evidence ?? Relative(root, workDirectory);
            string entryPoint = marker?.EntryPoint ?? workDirectory;
            string[] inferredProjects = language == RepositoryLanguage.DotNet
                ? InferDotNetTestProjects(root, workDirectory)
                : Array.Empty<string>();
            string[] testProjects = config.Value.TestProjects.Count > 0
                ? config.Value.TestProjects.ToArray()
                : inferredProjects;
            string[] inferredScope = InferScope(language, root, workDirectory);
            string[] include = config.Value.IncludeNamespaces.Count > 0
                ? config.Value.IncludeNamespaces.ToArray()
                : inferredScope;

            return new LanguageDetection
            {
                Language = language,
                EntryPoint = entryPoint,
                Evidence = evidence,
                WorkDirectory = workDirectory,
                Workdir = Relative(root, workDirectory),
                BuildCommand = string.IsNullOrWhiteSpace(config.Value.Build)
                    ? DefaultBuild(language, evidence)
                    : config.Value.Build!,
                TestCommand = string.IsNullOrWhiteSpace(config.Value.Test)
                    ? DefaultTest(language, testProjects)
                    : config.Value.Test!,
                TestProjects = testProjects,
                IncludeNamespaces = include,
                ExcludeNamespaces = config.Value.ExcludeNamespaces.ToArray(),
                Config = config,
            };
        }

        internal static bool TryParseLanguage(string value, out RepositoryLanguage language)
        {
            switch (value.Trim().ToLowerInvariant())
            {
                case "dotnet": language = RepositoryLanguage.DotNet; return true;
                case "java": language = RepositoryLanguage.Java; return true;
                case "node": language = RepositoryLanguage.Node; return true;
                case "go": language = RepositoryLanguage.Go; return true;
                case "rust": language = RepositoryLanguage.Rust; return true;
                default: language = default; return false;
            }
        }

        internal static string Render(LanguageDetection detection)
        {
            var serializer = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
                .Build();
            return serializer.Serialize(new DetectionReport
            {
                Language = Name(detection.Language),
                Workdir = detection.Workdir,
                EntryPoint = detection.Evidence,
                Build = detection.BuildCommand,
                Test = detection.TestCommand,
                TestProjects = detection.TestProjects,
                IncludeNamespaces = detection.IncludeNamespaces,
                ExcludeNamespaces = detection.ExcludeNamespaces,
                Source = detection.Config.Exists ? ".behaviordiff/config.yml + detection" : "auto-detection",
            }).TrimEnd();
        }

        private static List<LanguageDetection> RootCandidates(string root)
        {
            var candidates = new List<LanguageDetection>();
            string[] solutions = Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly).ToArray();
            Add(candidates, root, RepositoryLanguage.DotNet, solutions.Length > 0
                ? solutions
                : Directory.EnumerateFiles(root, "*.csproj", SearchOption.TopDirectoryOnly));
            Add(candidates, root, RepositoryLanguage.Java, Existing(root, "pom.xml"));
            Add(candidates, root, RepositoryLanguage.Node, Existing(root, "package.json"));
            Add(candidates, root, RepositoryLanguage.Go, Existing(root, "go.mod"));
            Add(candidates, root, RepositoryLanguage.Rust, Existing(root, "Cargo.toml"));
            return candidates;
        }

        private static List<LanguageDetection> RecursiveCandidates(string root)
        {
            var candidates = new List<LanguageDetection>();
            string[] solutions = Find(root, "*.sln").ToArray();
            Add(candidates, root, RepositoryLanguage.DotNet, solutions.Length > 0 ? solutions : Find(root, "*.csproj"));
            Add(candidates, root, RepositoryLanguage.Java, Find(root, "pom.xml"));
            Add(candidates, root, RepositoryLanguage.Node, Find(root, "package.json"));
            Add(candidates, root, RepositoryLanguage.Go, Find(root, "go.mod"));
            Add(candidates, root, RepositoryLanguage.Rust, Find(root, "Cargo.toml"));
            return candidates;
        }

        private static void Add(
            List<LanguageDetection> candidates,
            string root,
            RepositoryLanguage language,
            IEnumerable<string> paths)
        {
            foreach (string path in paths.OrderBy(value => value, StringComparer.Ordinal))
            {
                candidates.Add(new LanguageDetection
                {
                    Language = language,
                    EntryPoint = Path.GetFullPath(path),
                    Evidence = Path.GetRelativePath(root, path).Replace('\\', '/'),
                });
            }
        }

        private static string[] InferDotNetTestProjects(string repositoryRoot, string workDirectory) =>
            RepoScan.Scan(workDirectory).XunitProjects
                .Select(project => Relative(repositoryRoot, project.Path))
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();

        private static string[] InferScope(RepositoryLanguage language, string repositoryRoot, string workDirectory)
        {
            if (language == RepositoryLanguage.DotNet)
            {
                return RepoScan.Scan(workDirectory).NamespacePrefixes.ToArray();
            }
            if (language == RepositoryLanguage.Node)
            {
                return new[] { "src", "lib", "app", "dist" }
                    .Where(candidate => Directory.Exists(Path.Combine(workDirectory, candidate)))
                    .ToArray();
            }
            if (language == RepositoryLanguage.Java)
            {
                var packages = new SortedSet<string>(StringComparer.Ordinal);
                foreach (string sourceRoot in new[] { "src/main/java", "src/test/java" })
                {
                    string directory = Path.Combine(workDirectory, sourceRoot.Replace('/', Path.DirectorySeparatorChar));
                    if (!Directory.Exists(directory)) continue;
                    foreach (string source in Directory.EnumerateFiles(directory, "*.java", SearchOption.AllDirectories))
                    {
                        Match match = JavaPackage.Match(File.ReadAllText(source));
                        if (match.Success) packages.Add(match.Groups[1].Value);
                    }
                }
                return packages.ToArray();
            }
            return new[] { Relative(repositoryRoot, workDirectory) };
        }

        private static string DefaultBuild(RepositoryLanguage language, string entryPoint) => language switch
        {
            RepositoryLanguage.DotNet => "dotnet build " + Quote(entryPoint) + " -c Release --nologo",
            RepositoryLanguage.Java => "mvn --batch-mode --no-transfer-progress package -DskipTests",
            RepositoryLanguage.Node => "npm ci && npm run build --if-present",
            RepositoryLanguage.Go => "go build ./...",
            RepositoryLanguage.Rust => "cargo build",
            _ => string.Empty,
        };

        private static string DefaultTest(RepositoryLanguage language, string[] projects) => language switch
        {
            RepositoryLanguage.DotNet when projects.Length > 0 => "dotnet test " + string.Join(" ", projects.Select(Quote)) + " -c Release --no-build --nologo",
            RepositoryLanguage.DotNet => "dotnet test -c Release --no-build --nologo",
            RepositoryLanguage.Java => "mvn --batch-mode --no-transfer-progress test",
            RepositoryLanguage.Node => "npm test",
            RepositoryLanguage.Go => "go test ./...",
            RepositoryLanguage.Rust => "cargo test -- --test-threads=1",
            _ => string.Empty,
        };

        private static bool HasBothCommands(RepositoryConfig config) =>
            !string.IsNullOrWhiteSpace(config.Build) && !string.IsNullOrWhiteSpace(config.Test);

        private static CliException DetectionFailure(string detail) => new(
            detail + " Run 'behaviordiff detect <repo>' and write .behaviordiff/config.yml to resolve it.",
            ExitCodes.RunInvalid);

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

        private static string Relative(string root, string path)
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            return relative.Length == 0 ? "." : relative;
        }

        private static string Quote(string value) => value.Contains(' ') ? "\"" + value + "\"" : value;

        private static string Name(RepositoryLanguage language) => language.ToString().ToLowerInvariant();

        private sealed class DetectionReport
        {
            public string Language { get; init; } = string.Empty;
            public string Workdir { get; init; } = string.Empty;
            public string EntryPoint { get; init; } = string.Empty;
            public string Build { get; init; } = string.Empty;
            public string Test { get; init; } = string.Empty;
            public string[] TestProjects { get; init; } = Array.Empty<string>();
            public string[] IncludeNamespaces { get; init; } = Array.Empty<string>();
            public string[] ExcludeNamespaces { get; init; } = Array.Empty<string>();
            public string Source { get; init; } = string.Empty;
        }
    }
}
