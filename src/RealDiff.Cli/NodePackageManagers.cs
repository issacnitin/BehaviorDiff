using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RealDiff.Cli
{
    internal static class NodePackageManagers
    {
        internal static string Detect(string directory)
        {
            IReadOnlyList<(string Manager, string Lockfile)> detected = Find(directory);
            if (detected.Count > 1)
            {
                throw new CliException(
                    "Multiple Node lockfiles were detected beside package.json: "
                    + string.Join(", ", detected.Select(item => item.Lockfile)) + ".",
                    ExitCodes.RunInvalid);
            }
            if (detected.Count == 0)
            {
                throw new CliException(
                    "Node execution requires one lockfile beside package.json: package-lock.json, pnpm-lock.yaml, yarn.lock, bun.lock, or bun.lockb.",
                    ExitCodes.RunInvalid);
            }
            return detected[0].Manager;
        }

        internal static string? TryDetect(string directory)
        {
            IReadOnlyList<(string Manager, string Lockfile)> detected = Find(directory);
            return detected.Count == 0 ? null : Detect(directory);
        }

        internal static string LockfileName(string manager) => manager switch
        {
            "npm" => "package-lock.json",
            "pnpm" => "pnpm-lock.yaml",
            "yarn" => "yarn.lock",
            "bun" => "bun.lock or bun.lockb",
            _ => manager,
        };

        internal static string[] InstallArguments(string manager, string directory) => manager switch
        {
            "npm" => new[] { "ci" },
            "pnpm" => new[] { "install", "--frozen-lockfile" },
            "yarn" when File.Exists(Path.Combine(directory, ".yarnrc.yml")) => new[] { "install", "--immutable" },
            "yarn" => new[] { "install", "--frozen-lockfile" },
            "bun" => new[] { "install", "--frozen-lockfile" },
            _ => throw new CliException("Unsupported Node package manager: " + manager, ExitCodes.RunInvalid),
        };

        internal static string InstallCommand(string manager, string directory) =>
            manager + " " + string.Join(" ", InstallArguments(manager, directory));

        internal static bool HasScript(string directory, string name)
        {
            try
            {
                using JsonDocument package = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "package.json")));
                return package.RootElement.ValueKind == JsonValueKind.Object
                    && package.RootElement.TryGetProperty("scripts", out JsonElement scripts)
                    && scripts.ValueKind == JsonValueKind.Object
                    && scripts.TryGetProperty(name, out JsonElement script)
                    && script.ValueKind == JsonValueKind.String;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static IReadOnlyList<(string Manager, string Lockfile)> Find(string directory)
        {
            var detected = new List<(string Manager, string Lockfile)>();
            AddIfPresent(detected, directory, "npm", "package-lock.json");
            AddIfPresent(detected, directory, "pnpm", "pnpm-lock.yaml");
            AddIfPresent(detected, directory, "yarn", "yarn.lock");
            AddIfPresent(detected, directory, "bun", "bun.lock");
            AddIfPresent(detected, directory, "bun", "bun.lockb");
            return detected;
        }

        private static void AddIfPresent(
            ICollection<(string Manager, string Lockfile)> detected,
            string directory,
            string manager,
            string lockfile)
        {
            if (File.Exists(Path.Combine(directory, lockfile)))
            {
                detected.Add((manager, lockfile));
            }
        }
    }
}