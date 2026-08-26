using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BehaviorDiff.Cli
{
    internal sealed class RepositoryConfig
    {
        [YamlMember(Alias = "language")]
        public string? Language { get; set; }

        [YamlMember(Alias = "build")]
        public string? Build { get; set; }

        [YamlMember(Alias = "test")]
        public string? Test { get; set; }

        [YamlMember(Alias = "workdir")]
        public string? Workdir { get; set; }

        [YamlMember(Alias = "test_projects")]
        public List<string> TestProjects { get; set; } = new();

        [YamlMember(Alias = "include_namespaces")]
        public List<string> IncludeNamespaces { get; set; } = new();

        [YamlMember(Alias = "exclude_namespaces")]
        public List<string> ExcludeNamespaces { get; set; } = new();

        [YamlMember(Alias = "redaction")]
        public RedactionConfig Redaction { get; set; } = new();

        [YamlMember(Alias = "baseline")]
        public BaselineDocument? Baseline { get; set; }
    }

    internal sealed class RedactionConfig
    {
        [YamlMember(Alias = "names")]
        public List<string> Names { get; set; } = new();

        [YamlMember(Alias = "types")]
        public List<string> Types { get; set; } = new();

        [YamlMember(Alias = "paths")]
        public List<string> Paths { get; set; } = new();
    }

    internal sealed class LoadedRepositoryConfig
    {
        internal string RepositoryRoot { get; init; } = string.Empty;

        internal string Path { get; init; } = string.Empty;

        internal RepositoryConfig Value { get; init; } = new();

        internal bool Exists => Path.Length > 0;
    }

    internal static class RepositoryConfigLoader
    {
        private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

        internal static LoadedRepositoryConfig Load(string repository)
        {
            string root = Path.GetFullPath(repository);
            string path = Path.Combine(root, ".behaviordiff", "config.yml");
            if (!File.Exists(path))
            {
                return new LoadedRepositoryConfig { RepositoryRoot = root };
            }

            try
            {
                RepositoryConfig value = Yaml.Deserialize<RepositoryConfig>(File.ReadAllText(path))
                    ?? throw new CliException("BehaviorDiff config is empty: " + path, ExitCodes.RunInvalid);
                Validate(value, root, path);
                return new LoadedRepositoryConfig
                {
                    RepositoryRoot = root,
                    Path = path,
                    Value = value,
                };
            }
            catch (YamlException ex)
            {
                throw new CliException("BehaviorDiff config YAML is malformed: " + ex.Message, ExitCodes.RunInvalid);
            }
        }

        internal static string ResolveWorkDirectory(LoadedRepositoryConfig loaded)
        {
            string relative = string.IsNullOrWhiteSpace(loaded.Value.Workdir) ? "." : loaded.Value.Workdir!;
            string full = Path.GetFullPath(Path.Combine(loaded.RepositoryRoot, relative));
            string rootPrefix = loaded.RepositoryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!string.Equals(full, loaded.RepositoryRoot, StringComparison.OrdinalIgnoreCase)
                && !full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new CliException("BehaviorDiff config workdir escapes the repository root: " + relative, ExitCodes.RunInvalid);
            }
            if (!Directory.Exists(full))
            {
                throw new CliException("BehaviorDiff config workdir does not exist: " + relative, ExitCodes.RunInvalid);
            }
            return full;
        }

        internal static void ApplyEnvironment(LoadedRepositoryConfig loaded)
        {
            SetList("BEHAVIORDIFF_REDACT_NAMES", loaded.Value.Redaction.Names);
            SetList("BEHAVIORDIFF_REDACT_TYPES", loaded.Value.Redaction.Types);
            SetList("BEHAVIORDIFF_REDACT_PATHS", loaded.Value.Redaction.Paths);
            SetList("BEHAVIORDIFF_NAMESPACES", loaded.Value.IncludeNamespaces);
            SetList("BEHAVIORDIFF_EXCLUDE_NAMESPACES", loaded.Value.ExcludeNamespaces);
        }

        internal static string? MaterializeBaseline(LoadedRepositoryConfig loaded, string output)
        {
            if (loaded.Value.Baseline is null)
            {
                return null;
            }

            var serializer = new SerializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, serializer.Serialize(loaded.Value.Baseline));
            return output;
        }

        private static void SetList(string name, IReadOnlyCollection<string> configured)
        {
            if (configured.Count == 0)
            {
                return;
            }

            string existing = Environment.GetEnvironmentVariable(name) ?? string.Empty;
            string combined = string.Join(";", existing.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Concat(configured)
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase));
            Environment.SetEnvironmentVariable(name, combined);
        }

        private static void Validate(RepositoryConfig config, string root, string path)
        {
            if (config.Language is not null && !LanguageDetector.TryParseLanguage(config.Language, out _))
            {
                throw new CliException(
                    "Unsupported language '" + config.Language + "' in " + path
                    + ". Expected dotnet, java, node, go, or rust.",
                    ExitCodes.RunInvalid);
            }

            foreach ((IEnumerable<string> values, string field) in new[]
            {
                (config.TestProjects, "test_projects"),
                (config.IncludeNamespaces, "include_namespaces"),
                (config.ExcludeNamespaces, "exclude_namespaces"),
                (config.Redaction.Names, "redaction.names"),
                (config.Redaction.Types, "redaction.types"),
                (config.Redaction.Paths, "redaction.paths"),
            })
            {
                foreach (string value in values)
                {
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        throw new CliException("BehaviorDiff config " + field + " contains an empty value.", ExitCodes.RunInvalid);
                    }
                }
            }

            _ = root;
        }
    }
}
