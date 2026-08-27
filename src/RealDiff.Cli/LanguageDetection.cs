using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RealDiff.Cli
{
    internal enum RepositoryLanguage
    {
        DotNet,
        Java,
        Node,
        Go,
        Rust,
        Python,
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

        internal string[] SourceRoots { get; init; } = Array.Empty<string>();

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
                    "Could not detect a supported repository language. Expected a solution/project, pom.xml, package.json, go.mod, Cargo.toml, pyproject.toml, setup.py, or requirements.txt.");
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
            string[] sourceRoots = language == RepositoryLanguage.Java
                ? InferJavaSourceRoots(root, entryPoint, workDirectory, config.Value.SourceRoots)
                : Array.Empty<string>();
            string[] inferredScope = InferScope(language, root, workDirectory, sourceRoots);
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
                    ? DefaultBuild(language, evidence, workDirectory)
                    : config.Value.Build!,
                TestCommand = string.IsNullOrWhiteSpace(config.Value.Test)
                    ? DefaultTest(language, evidence, workDirectory, testProjects)
                    : config.Value.Test!,
                TestProjects = testProjects,
                SourceRoots = sourceRoots,
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
                case "python": language = RepositoryLanguage.Python; return true;
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
                SourceRoots = detection.SourceRoots,
                IncludeNamespaces = detection.IncludeNamespaces,
                ExcludeNamespaces = detection.ExcludeNamespaces,
                Source = detection.Config.Exists ? ".realdiff/config.yml + detection" : "auto-detection",
            }).TrimEnd();
        }

        private static List<LanguageDetection> RootCandidates(string root)
        {
            var candidates = new List<LanguageDetection>();
            string[] solutions = Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly).ToArray();
            Add(candidates, root, RepositoryLanguage.DotNet, solutions.Length > 0
                ? solutions
                : Directory.EnumerateFiles(root, "*.csproj", SearchOption.TopDirectoryOnly));
            Add(candidates, root, RepositoryLanguage.Java, Existing(root, "pom.xml", "build.gradle", "build.gradle.kts"));
            Add(candidates, root, RepositoryLanguage.Node, Existing(root, "package.json"));
            Add(candidates, root, RepositoryLanguage.Go, Existing(root, "go.mod"));
            Add(candidates, root, RepositoryLanguage.Rust, Existing(root, "Cargo.toml"));
            Add(candidates, root, RepositoryLanguage.Python, PythonMarkers(root, SearchOption.TopDirectoryOnly));
            return candidates;
        }

        private static List<LanguageDetection> RecursiveCandidates(string root)
        {
            var candidates = new List<LanguageDetection>();
            string[] solutions = Find(root, "*.sln").ToArray();
            Add(candidates, root, RepositoryLanguage.DotNet, solutions.Length > 0 ? solutions : Find(root, "*.csproj"));
            Add(candidates, root, RepositoryLanguage.Java, Find(root, "pom.xml")
                .Concat(Find(root, "build.gradle"))
                .Concat(Find(root, "build.gradle.kts")));
            Add(candidates, root, RepositoryLanguage.Node, Find(root, "package.json"));
            Add(candidates, root, RepositoryLanguage.Go, Find(root, "go.mod"));
            Add(candidates, root, RepositoryLanguage.Rust, Find(root, "Cargo.toml"));
            Add(candidates, root, RepositoryLanguage.Python, PythonMarkers(root, SearchOption.AllDirectories));
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

        private static string[] InferScope(
            RepositoryLanguage language,
            string repositoryRoot,
            string workDirectory,
            IReadOnlyList<string> sourceRoots)
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
                foreach (string sourceRoot in sourceRoots)
                {
                    string directory = Path.Combine(repositoryRoot, sourceRoot.Replace('/', Path.DirectorySeparatorChar));
                    if (!Directory.Exists(directory)) continue;
                    foreach (string source in Directory.EnumerateFiles(directory, "*.java", SearchOption.AllDirectories))
                    {
                        Match match = JavaPackage.Match(File.ReadAllText(source));
                        if (match.Success) packages.Add(match.Groups[1].Value);
                    }
                }
                return packages.ToArray();
            }
            if (language == RepositoryLanguage.Python)
            {
                string[] scopes = Directory.EnumerateDirectories(workDirectory, "*", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .OfType<string>()
                    .Where(candidate => candidate is "src" or "lib" or "app"
                        || candidate.StartsWith("test", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(candidate => candidate, StringComparer.Ordinal)
                    .ToArray();
                return scopes.Length > 0 ? scopes : new[] { Relative(repositoryRoot, workDirectory) };
            }
            return new[] { Relative(repositoryRoot, workDirectory) };
        }

        private static string DefaultBuild(
            RepositoryLanguage language,
            string entryPoint,
            string workDirectory) => language switch
        {
            RepositoryLanguage.DotNet => "dotnet build " + Quote(entryPoint) + " -c Release --nologo",
            RepositoryLanguage.Java when IsGradleEntryPoint(entryPoint) => "gradlew build -x test",
            RepositoryLanguage.Java => "mvn --batch-mode --no-transfer-progress package -DskipTests",
            RepositoryLanguage.Node => DefaultNodeBuild(workDirectory),
            RepositoryLanguage.Go => "go build ./...",
            RepositoryLanguage.Rust => "cargo build",
            RepositoryLanguage.Python => string.Empty,
            _ => string.Empty,
        };

        private static string DefaultTest(
            RepositoryLanguage language,
            string entryPoint,
            string workDirectory,
            string[] projects) => language switch
        {
            RepositoryLanguage.DotNet when projects.Length > 0 => "dotnet test " + string.Join(" ", projects.Select(Quote)) + " -c Release --no-build --nologo",
            RepositoryLanguage.DotNet => "dotnet test -c Release --no-build --nologo",
            RepositoryLanguage.Java when IsGradleEntryPoint(entryPoint) => "gradlew test",
            RepositoryLanguage.Java => "mvn --batch-mode --no-transfer-progress test",
            RepositoryLanguage.Node => (NodePackageManagers.TryDetect(workDirectory) ?? "npm") + " run test",
            RepositoryLanguage.Go => "go test ./...",
            RepositoryLanguage.Rust => "cargo test -- --test-threads=1",
            RepositoryLanguage.Python => "python -m pytest",
            _ => string.Empty,
        };

        private static string DefaultNodeBuild(string workDirectory)
        {
            string manager = NodePackageManagers.TryDetect(workDirectory) ?? "npm";
            string install = NodePackageManagers.InstallCommand(manager, workDirectory);
            return NodePackageManagers.HasScript(workDirectory, "build")
                ? install + " && " + manager + " run build"
                : install;
        }

        private static string[] InferJavaSourceRoots(
            string repositoryRoot,
            string entryPoint,
            string workDirectory,
            IReadOnlyList<string> configured)
        {
            if (configured.Count > 0)
            {
                return configured
                    .Select(root => RepositoryRelativeSourceRoot(repositoryRoot, repositoryRoot, root))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            }

            var roots = new SortedSet<string>(StringComparer.Ordinal);
            if (IsGradleEntryPoint(entryPoint) && File.Exists(entryPoint))
            {
                string text = File.ReadAllText(entryPoint);
                foreach (Match match in Regex.Matches(
                    text,
                    @"(?:(?:srcDirs?|setSrcDirs)\s*(?:=|\()\s*\[?)(?<values>[^\]\)\r\n]+)",
                    RegexOptions.CultureInvariant))
                {
                    foreach (Match value in Regex.Matches(match.Groups["values"].Value, "['\\\"](?<path>[^'\\\"]+)['\\\"]"))
                    {
                        roots.Add(RepositoryRelativeSourceRoot(
                            repositoryRoot,
                            Path.GetDirectoryName(entryPoint)!,
                            value.Groups["path"].Value));
                    }
                }
            }

            foreach (string candidate in new[] { "src/main/java", "src/test/java" })
            {
                if (Directory.Exists(Path.Combine(workDirectory, candidate.Replace('/', Path.DirectorySeparatorChar))))
                {
                    roots.Add(RepositoryRelativeSourceRoot(repositoryRoot, workDirectory, candidate));
                }
            }
            return roots.ToArray();
        }

        private static string NormalizeRelativePath(string value) =>
            value.Trim().TrimEnd('/', '\\').Replace('\\', '/');

        private static string RepositoryRelativeSourceRoot(
            string repositoryRoot,
            string baseDirectory,
            string value)
        {
            string fullRoot = Path.GetFullPath(repositoryRoot);
            string fullPath = Path.GetFullPath(Path.Combine(baseDirectory, value));
            string rootPrefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase)
                && !fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new CliException(
                    "Java source root escapes the repository root: " + value,
                    ExitCodes.RunInvalid);
            }

            return NormalizeRelativePath(Path.GetRelativePath(fullRoot, fullPath));
        }

        private static bool IsGradleEntryPoint(string entryPoint) =>
            entryPoint.EndsWith("build.gradle", StringComparison.OrdinalIgnoreCase)
            || entryPoint.EndsWith("build.gradle.kts", StringComparison.OrdinalIgnoreCase);

        private static bool HasBothCommands(RepositoryConfig config) =>
            !string.IsNullOrWhiteSpace(config.Build) && !string.IsNullOrWhiteSpace(config.Test);

        private static CliException DetectionFailure(string detail) => new(
            detail + " Run 'realdiff detect <repo>' and write .realdiff/config.yml to resolve it.",
            ExitCodes.RunInvalid);

        private static IEnumerable<string> Existing(string root, params string[] names) =>
            names.Select(name => Path.Combine(root, name)).Where(File.Exists);

        private static IEnumerable<string> Find(string root, string pattern) =>
            Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).Where(path => !IsBuildOutput(path));

        private static IEnumerable<string> PythonMarkers(string root, SearchOption option) =>
            new[] { "pyproject.toml", "setup.py", "requirements.txt" }
                .SelectMany(name => Directory.EnumerateFiles(root, name, option))
                .Where(path => !IsBuildOutput(path))
                .GroupBy(Path.GetDirectoryName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderBy(PythonMarkerRank).First());

        private static int PythonMarkerRank(string path) => Path.GetFileName(path) switch
        {
            "pyproject.toml" => 0,
            "setup.py" => 1,
            _ => 2,
        };

        private static bool IsBuildOutput(string path)
        {
            string normalized = path.Replace('\\', '/');
            return normalized.Contains("/bin/", StringComparison.Ordinal)
                || normalized.Contains("/obj/", StringComparison.Ordinal)
                || normalized.Contains("/target/", StringComparison.Ordinal)
                || normalized.Contains("/node_modules/", StringComparison.Ordinal)
                || normalized.Contains("/__pycache__/", StringComparison.Ordinal)
                || normalized.Contains("/.pytest_cache/", StringComparison.Ordinal)
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
            public string[] SourceRoots { get; init; } = Array.Empty<string>();
            public string[] IncludeNamespaces { get; init; } = Array.Empty<string>();
            public string[] ExcludeNamespaces { get; init; } = Array.Empty<string>();
            public string Source { get; init; } = string.Empty;
        }
    }
}
