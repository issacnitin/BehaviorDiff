using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace BehaviorDiff.Cli
{
    internal sealed class CrossLanguageRunSet
    {
        internal string Base1 { get; init; } = string.Empty;

        internal string Base2 { get; init; } = string.Empty;

        internal string Base3 { get; init; } = string.Empty;

        internal string Pr { get; init; } = string.Empty;

        internal string BaseRoot { get; init; } = string.Empty;
    }

    internal sealed class CrossLanguageExecution
    {
        private static readonly Regex JavaPackage = new(
            @"^\s*package\s+([A-Za-z_$][A-Za-z0-9_$]*(?:\.[A-Za-z_$][A-Za-z0-9_$]*)*)\s*;",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private readonly string _work;
        private readonly TraceCacheSession _cache;
        private readonly PipelineTimings _timings;

        internal CrossLanguageExecution(string work, TraceCacheSession cache, PipelineTimings timings)
        {
            _work = work;
            _cache = cache;
            _timings = timings;
        }

        internal static int StripBuildOutput(string tree)
        {
            int removed = 0;
            foreach (string directory in Directory.EnumerateDirectories(tree, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path) is "target" or "node_modules" or "dist")
                .OrderByDescending(path => path.Length)
                .ToList())
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                    removed++;
                }
                catch (IOException)
                {
                }
            }

            return removed;
        }

        internal CrossLanguageRunSet Run(
            LanguageDetection baseDetection,
            LanguageDetection prDetection,
            string baseTree,
            string prTree,
            string targetSha)
        {
            return baseDetection.Language switch
            {
                RepositoryLanguage.Java => RunJava(baseDetection, prDetection, baseTree, prTree, targetSha),
                RepositoryLanguage.Node => RunNode(baseDetection, prDetection, baseTree, prTree, targetSha),
                RepositoryLanguage.Rust => RunRust(baseDetection, prDetection, baseTree, prTree, targetSha),
                _ => throw new CliException("Cross-language execution was requested for " + baseDetection.Language + "."),
            };
        }

        internal void Warm(LanguageDetection detection, string baseTree, string targetSha)
        {
            switch (detection.Language)
            {
                case RepositoryLanguage.Java:
                    WarmJava(detection, baseTree, targetSha);
                    break;
                case RepositoryLanguage.Node:
                    WarmNode(detection, baseTree, targetSha);
                    break;
                case RepositoryLanguage.Rust:
                    WarmRust(detection, baseTree, targetSha);
                    break;
                default:
                    throw new CliException("Cache warming was requested for " + detection.Language + ".");
            }
        }

        private void WarmJava(LanguageDetection detection, string baseTree, string targetSha)
        {
            Console.WriteLine();
            Console.WriteLine("=== 2. Java clean build ===");
            MavenCommand maven = ResolveMaven(detection.EntryPoint, baseTree);
            BuildJava("base", detection.EntryPoint, maven);

            string scope = string.Join(",", DeriveJavaScopes(detection.EntryPoint, detection.EntryPoint));
            string agent = ResolveJavaAgent();
            var key = new TraceCacheKey(
                targetSha,
                "java",
                TracerFingerprint.ForFile(agent),
                Pipeline.ScopeConfig(scope));
            if (_cache.TryRestore(key, out _))
            {
                return;
            }

            Console.WriteLine();
            Console.WriteLine("=== 3. Java base trace runs ===");
            var stopwatch = Stopwatch.StartNew();
            string base1 = RunJavaTests("base_run1", detection.EntryPoint, baseTree, maven, scope, agent);
            RunJavaTests("base_run2", detection.EntryPoint, baseTree, maven, scope, agent);
            RunJavaTests("base_run3", detection.EntryPoint, baseTree, maven, scope, agent);
            Pipeline.AssertTestIdsPresent(base1);
            stopwatch.Stop();
            _cache.Store(key, baseTree, stopwatch.ElapsedMilliseconds);
        }

        private void WarmNode(LanguageDetection detection, string baseTree, string targetSha)
        {
            Console.WriteLine();
            Console.WriteLine("=== 2. Node clean build ===");
            string baseDirectory = Path.GetDirectoryName(detection.EntryPoint)!;
            string manager = DetectNodePackageManager(baseDirectory);
            if (manager != "npm")
            {
                throw new CliException(
                    "Detected " + manager + " from " + LockfileName(manager)
                    + ", but this BehaviorDiff version supports npm/package-lock.json only.",
                    ExitCodes.RunInvalid);
            }

            BuildNode("base", baseDirectory);
            string scope = string.Join(";", DeriveNodeScopes(baseDirectory, baseDirectory));
            string tracer = ResolveNodeTracer();
            var key = new TraceCacheKey(
                targetSha,
                "node",
                TracerFingerprint.ForDirectory(tracer),
                Pipeline.ScopeConfig(scope));
            if (_cache.TryRestore(key, out _))
            {
                return;
            }

            Console.WriteLine();
            Console.WriteLine("=== 3. Node base trace runs ===");
            var stopwatch = Stopwatch.StartNew();
            string base1 = RunNodeTests("base_run1", baseDirectory, baseTree, scope, tracer);
            RunNodeTests("base_run2", baseDirectory, baseTree, scope, tracer);
            RunNodeTests("base_run3", baseDirectory, baseTree, scope, tracer);
            Pipeline.AssertTestIdsPresent(base1);
            stopwatch.Stop();
            _cache.Store(key, baseTree, stopwatch.ElapsedMilliseconds);
        }

        private CrossLanguageRunSet RunJava(
            LanguageDetection baseDetection,
            LanguageDetection prDetection,
            string baseTree,
            string prTree,
            string targetSha)
        {
            Console.WriteLine();
            Console.WriteLine("=== 2. Java clean builds ===");
            MavenCommand baseMaven = ResolveMaven(baseDetection.EntryPoint, baseTree);
            MavenCommand prMaven = ResolveMaven(prDetection.EntryPoint, prTree);
            BuildJava("base", baseDetection.EntryPoint, baseMaven);
            BuildJava("pr", prDetection.EntryPoint, prMaven);

            Console.WriteLine();
            Console.WriteLine("=== 3. Java trace scope and agent ===");
            string scope = string.Join(",", DeriveJavaScopes(baseDetection.EntryPoint, prDetection.EntryPoint));
            string agent = ResolveJavaAgent();
            Console.WriteLine("  package scope: " + scope);
            Console.WriteLine("  java agent  : " + agent);

            var key = new TraceCacheKey(
                targetSha,
                "java",
                TracerFingerprint.ForFile(agent),
                Pipeline.ScopeConfig(scope));
            bool cacheHit = _cache.TryRestore(key, out TraceCacheEntry? cacheEntry);

            Console.WriteLine();
            Console.WriteLine("=== 4. Java test runs ===");
            string base1;
            string base2;
            string base3;
            string baseRoot;
            if (cacheHit)
            {
                base1 = TraceCacheSession.RunPath(_work, 1);
                base2 = TraceCacheSession.RunPath(_work, 2);
                base3 = TraceCacheSession.RunPath(_work, 3);
                baseRoot = cacheEntry!.BaseRoot;
            }
            else
            {
                var stopwatch = Stopwatch.StartNew();
                base1 = RunJavaTests("base_run1", baseDetection.EntryPoint, baseTree, baseMaven, scope, agent);
                base2 = RunJavaTests("base_run2", baseDetection.EntryPoint, baseTree, baseMaven, scope, agent);
                base3 = RunJavaTests("base_run3", baseDetection.EntryPoint, baseTree, baseMaven, scope, agent);
                stopwatch.Stop();
                baseRoot = baseTree;
                _cache.Store(key, baseRoot, stopwatch.ElapsedMilliseconds);
            }

            Pipeline.AssertTestIdsPresent(base1);
            string pr = RunJavaTests("pr_run", prDetection.EntryPoint, prTree, prMaven, scope, agent);
            return new CrossLanguageRunSet { Base1 = base1, Base2 = base2, Base3 = base3, Pr = pr, BaseRoot = baseRoot };
        }

        private CrossLanguageRunSet RunNode(
            LanguageDetection baseDetection,
            LanguageDetection prDetection,
            string baseTree,
            string prTree,
            string targetSha)
        {
            Console.WriteLine();
            Console.WriteLine("=== 2. Node clean builds ===");
            var buildStopwatch = Stopwatch.StartNew();
            string baseDirectory = Path.GetDirectoryName(baseDetection.EntryPoint)!;
            string prDirectory = Path.GetDirectoryName(prDetection.EntryPoint)!;
            string baseManager = DetectNodePackageManager(baseDirectory);
            string prManager = DetectNodePackageManager(prDirectory);
            if (!string.Equals(baseManager, prManager, StringComparison.Ordinal))
            {
                throw new CliException(
                    "Base uses " + baseManager + " but PR uses " + prManager
                    + ". Different Node package managers make the build inputs incomparable.",
                    ExitCodes.RunInvalid);
            }

            if (baseManager != "npm")
            {
                throw new CliException(
                    "Detected " + baseManager + " from " + LockfileName(baseManager)
                    + ", but this BehaviorDiff version supports npm/package-lock.json only.",
                    ExitCodes.RunInvalid);
            }

            BuildNode("base", baseDirectory);
            BuildNode("pr", prDirectory);
            buildStopwatch.Stop();
            _timings.BuildMilliseconds += buildStopwatch.ElapsedMilliseconds;

            Console.WriteLine();
            Console.WriteLine("=== 3. Node trace scope and tracer ===");
            string scope = string.Join(";", DeriveNodeScopes(baseDirectory, prDirectory));
            string tracer = ResolveNodeTracer();
            Console.WriteLine("  path scope : " + scope);
            Console.WriteLine("  node tracer: " + tracer);

            var key = new TraceCacheKey(
                targetSha,
                "node",
                TracerFingerprint.ForDirectory(tracer),
                Pipeline.ScopeConfig(scope));
            bool cacheHit = _cache.TryRestore(key, out TraceCacheEntry? cacheEntry);

            Console.WriteLine();
            Console.WriteLine("=== 4. Node test runs ===");
            string base1;
            string base2;
            string base3;
            string baseRoot;
            if (cacheHit)
            {
                base1 = TraceCacheSession.RunPath(_work, 1);
                base2 = TraceCacheSession.RunPath(_work, 2);
                base3 = TraceCacheSession.RunPath(_work, 3);
                baseRoot = cacheEntry!.BaseRoot;
            }
            else
            {
                var stopwatch = Stopwatch.StartNew();
                base1 = RunNodeTests("base_run1", baseDirectory, baseTree, scope, tracer);
                base2 = RunNodeTests("base_run2", baseDirectory, baseTree, scope, tracer);
                base3 = RunNodeTests("base_run3", baseDirectory, baseTree, scope, tracer);
                stopwatch.Stop();
                _timings.InstrumentedRunMilliseconds += stopwatch.ElapsedMilliseconds;
                baseRoot = baseTree;
                _cache.Store(key, baseRoot, stopwatch.ElapsedMilliseconds);
            }

            Pipeline.AssertTestIdsPresent(base1);
            var prStopwatch = Stopwatch.StartNew();
            string pr = RunNodeTests("pr_run", prDirectory, prTree, scope, tracer);
            prStopwatch.Stop();
            _timings.InstrumentedRunMilliseconds += prStopwatch.ElapsedMilliseconds;
            return new CrossLanguageRunSet { Base1 = base1, Base2 = base2, Base3 = base3, Pr = pr, BaseRoot = baseRoot };
        }

        private void WarmRust(LanguageDetection detection, string baseTree, string targetSha)
        {
            string source = Path.GetDirectoryName(detection.EntryPoint)!;
            string tracer = ResolveRustTracer();
            var key = new TraceCacheKey(targetSha, "rust", TracerFingerprint.ForFile(tracer), Pipeline.ScopeConfig(string.Empty));
            if (_cache.TryRestore(key, out _))
            {
                return;
            }
            var stopwatch = Stopwatch.StartNew();
            RunRustTests("base_run1", source, Path.Combine(_work, "rust-base-cache"), tracer);
            RunRustTests("base_run2", source, Path.Combine(_work, "rust-base-cache"), tracer);
            RunRustTests("base_run3", source, Path.Combine(_work, "rust-base-cache"), tracer);
            stopwatch.Stop();
            _cache.Store(key, baseTree, stopwatch.ElapsedMilliseconds);
        }

        private CrossLanguageRunSet RunRust(
            LanguageDetection baseDetection,
            LanguageDetection prDetection,
            string baseTree,
            string prTree,
            string targetSha)
        {
            Console.WriteLine();
            Console.WriteLine("=== 2. Rust stable cached source rewriting ===");
            string baseSource = Path.GetDirectoryName(baseDetection.EntryPoint)!;
            string prSource = Path.GetDirectoryName(prDetection.EntryPoint)!;
            string tracer = ResolveRustTracer();
            Console.WriteLine("  rust tracer: " + tracer);
            var key = new TraceCacheKey(targetSha, "rust", TracerFingerprint.ForFile(tracer), Pipeline.ScopeConfig(string.Empty));
            bool cacheHit = _cache.TryRestore(key, out TraceCacheEntry? cacheEntry);
            string base1;
            string base2;
            string base3;
            string baseRoot;
            if (cacheHit)
            {
                base1 = TraceCacheSession.RunPath(_work, 1);
                base2 = TraceCacheSession.RunPath(_work, 2);
                base3 = TraceCacheSession.RunPath(_work, 3);
                baseRoot = cacheEntry!.BaseRoot;
            }
            else
            {
                var stopwatch = Stopwatch.StartNew();
                string cache = Path.Combine(_work, "rust-base-cache");
                base1 = RunRustTests("base_run1", baseSource, cache, tracer);
                base2 = RunRustTests("base_run2", baseSource, cache, tracer);
                base3 = RunRustTests("base_run3", baseSource, cache, tracer);
                stopwatch.Stop();
                _timings.InstrumentedRunMilliseconds += stopwatch.ElapsedMilliseconds;
                baseRoot = baseTree;
                _cache.Store(key, baseRoot, stopwatch.ElapsedMilliseconds);
            }
            Pipeline.AssertTestIdsPresent(base1);
            string pr = RunRustTests("pr_run", prSource, Path.Combine(_work, "rust-pr-cache"), tracer);
            return new CrossLanguageRunSet { Base1 = base1, Base2 = base2, Base3 = base3, Pr = pr, BaseRoot = baseRoot };
        }

        private string RunRustTests(string label, string source, string cache, string tracer)
        {
            string directory = PrepareRunDirectory(label);
            ProcessResult rewrite = Shell.Run(
                tracer,
                new[] { "--source", source, "--cache-root", cache },
                source);
            if (!rewrite.Ok)
            {
                throw new CliException("Rust source rewriting failed." + Environment.NewLine + Shell.Tail(rewrite.Output, 25), ExitCodes.RepoDoesNotBuild);
            }
            using JsonDocument report = JsonDocument.Parse(rewrite.Output.Trim());
            string rewritten = report.RootElement.GetProperty("output").GetString()!;
            string trace = Path.Combine(directory, "run.rust.ndjson");
            var environment = new Dictionary<string, string> { ["BEHAVIORDIFF_RUST_EXIT_TRACE"] = trace };
            Console.WriteLine("  " + label.PadRight(10) + " command: cargo test -- --test-threads=1");
            ProcessResult test = RunScriptCommand(
                "cargo",
                new[] { "test", "--quiet", "--manifest-path", Path.Combine(rewritten, "Cargo.toml"), "--", "--test-threads=1" },
                rewritten,
                environment);
            if (!test.Ok)
            {
                throw new CliException("Rewritten Rust tests failed." + Environment.NewLine + Shell.Tail(test.Output, 25), ExitCodes.BuildOrTestFailure);
            }
            string origin = Path.Combine(rewritten, ".behaviordiff-rust-origin.json");
            string manifest = Path.Combine(directory, "run.rust.manifest.ndjson");
            ProcessResult finalize = Shell.Run(
                tracer,
                new[] { "finalize", "--origin", origin, "--trace", trace, "--out", manifest },
                rewritten);
            if (!finalize.Ok)
            {
                throw new CliException("Rust trace manifest finalization failed." + Environment.NewLine + Shell.Tail(finalize.Output, 25), ExitCodes.RunInvalid);
            }
            TraceSummary summary = ValidateTrace(directory, label, test.Output);
            ReportTrace(label, summary, test.ExitCode);
            return directory;
        }

        private static void BuildJava(string label, string entryPoint, MavenCommand maven)
        {
            var arguments = new[]
            {
                "--batch-mode",
                "--no-transfer-progress",
                "-f",
                entryPoint,
                "package",
                "-DskipTests",
            };
            Console.WriteLine("  " + label + " command: " + maven.DisplayName + " package -DskipTests --batch-mode --no-transfer-progress");
            ProcessResult result = maven.Run(arguments, Path.GetDirectoryName(entryPoint)!);
            if (!result.Ok)
            {
                throw new CliException(
                    "This Java repository does not build in this environment, before instrumentation."
                    + Environment.NewLine + "    Worktree: " + label
                    + Environment.NewLine + Shell.Tail(result.Output, 25),
                    ExitCodes.RepoDoesNotBuild);
            }
        }

        private static void BuildNode(string label, string directory)
        {
            Console.WriteLine("  " + label + " command: npm ci");
            ProcessResult install = RunScriptCommand("npm", new[] { "ci" }, directory);
            if (!install.Ok)
            {
                throw new CliException(
                    "This Node repository does not install from package-lock.json in this environment, before instrumentation."
                    + Environment.NewLine + "    Worktree: " + label
                    + Environment.NewLine + Shell.Tail(install.Output, 25),
                    ExitCodes.RepoDoesNotBuild);
            }

            Console.WriteLine("  " + label + " command: npm run build --if-present");
            ProcessResult build = RunScriptCommand("npm", new[] { "run", "build", "--if-present" }, directory);
            if (!build.Ok)
            {
                throw new CliException(
                    "This Node repository does not build in this environment, before instrumentation."
                    + Environment.NewLine + "    Worktree: " + label
                    + Environment.NewLine + Shell.Tail(build.Output, 25),
                    ExitCodes.RepoDoesNotBuild);
            }
        }

        private string RunJavaTests(
            string label,
            string entryPoint,
            string repositoryRoot,
            MavenCommand maven,
            string scope,
            string agent)
        {
            string directory = PrepareRunDirectory(label);
            string trace = Path.Combine(directory, "run.ndjson");
            string argLine = "--add-opens java.base/java.util=ALL-UNNAMED -javaagent:\"" + agent + "\"";
            var environment = new Dictionary<string, string>
            {
                ["BEHAVIORDIFF_TRACE"] = trace,
                ["BEHAVIORDIFF_NAMESPACES"] = scope,
                ["BEHAVIORDIFF_REPOSITORY_ROOT"] = repositoryRoot,
            };
            var arguments = new[]
            {
                "--batch-mode",
                "--no-transfer-progress",
                "-f",
                entryPoint,
                "test",
                "-DargLine=" + argLine,
            };
            Console.WriteLine("  " + label.PadRight(10) + " command: " + maven.DisplayName + " test -DargLine=<add-opens + javaagent>");
            ProcessResult result = maven.Run(arguments, Path.GetDirectoryName(entryPoint)!, environment);
            TraceSummary summary = ValidateTrace(directory, label, result.Output);
            ReportTrace(label, summary, result.ExitCode);
            return directory;
        }

        private string RunNodeTests(
            string label,
            string packageDirectory,
            string repositoryRoot,
            string scope,
            string tracer)
        {
            string directory = PrepareRunDirectory(label);
            string register = Path.Combine(tracer, "register.cjs").Replace('\\', '/');
            string loader = new Uri(Path.Combine(tracer, "loader.mjs")).AbsoluteUri;
            string additions = "--require " + QuoteNodeOption(register) + " --loader " + QuoteNodeOption(loader);
            string existingOptions = Environment.GetEnvironmentVariable("NODE_OPTIONS") ?? string.Empty;
            var environment = new Dictionary<string, string>
            {
                ["BEHAVIORDIFF_TRACE"] = Path.Combine(directory, "run.ndjson"),
                ["BEHAVIORDIFF_NAMESPACES"] = scope,
                ["BEHAVIORDIFF_NODE_ROOT"] = tracer,
                ["BEHAVIORDIFF_REPOSITORY_ROOT"] = repositoryRoot,
                ["CI"] = "true",
                ["NODE_OPTIONS"] = string.IsNullOrWhiteSpace(existingOptions)
                    ? additions
                    : existingOptions.Trim() + " " + additions,
            };
            Console.WriteLine("  " + label.PadRight(10) + " command: npm test (NODE_OPTIONS += --require register.cjs --loader loader.mjs)");
            ProcessResult result = RunScriptCommand("npm", new[] { "test" }, packageDirectory, environment);
            TraceSummary summary = ValidateTrace(directory, label, result.Output);
            ReportTrace(label, summary, result.ExitCode);
            return directory;
        }

        private string PrepareRunDirectory(string label)
        {
            string directory = Path.Combine(_work, label);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            Directory.CreateDirectory(directory);
            return directory;
        }

        private static IReadOnlyList<string> DeriveJavaScopes(string baseEntryPoint, string prEntryPoint)
        {
            var packages = new SortedSet<string>(StringComparer.Ordinal);
            bool foundSource = false;
            foreach (string entryPoint in new[] { baseEntryPoint, prEntryPoint })
            {
                string root = Path.GetDirectoryName(entryPoint)!;
                foreach (string sourceRoot in new[] { "src/main/java", "src/test/java" })
                {
                    string directory = Path.Combine(root, sourceRoot.Replace('/', Path.DirectorySeparatorChar));
                    if (!Directory.Exists(directory))
                    {
                        continue;
                    }

                    foreach (string source in Directory.EnumerateFiles(directory, "*.java", SearchOption.AllDirectories))
                    {
                        if (Path.GetFileName(source) == "module-info.java")
                        {
                            continue;
                        }

                        foundSource = true;
                        Match match = JavaPackage.Match(File.ReadAllText(source));
                        if (!match.Success)
                        {
                            throw new CliException(
                                "Java source is in the default package, which cannot be scoped safely: " + source,
                                ExitCodes.RunInvalid);
                        }

                        packages.Add(match.Groups[1].Value);
                    }
                }
            }

            if (!foundSource || packages.Count == 0)
            {
                throw new CliException(
                    "Could not derive a Java package scope from src/main/java or src/test/java.",
                    ExitCodes.RunInvalid);
            }

            var collapsed = new List<string>();
            foreach (string package in packages.OrderBy(value => value.Count(character => character == '.')).ThenBy(value => value, StringComparer.Ordinal))
            {
                if (!collapsed.Any(parent => package == parent || package.StartsWith(parent + ".", StringComparison.Ordinal)))
                {
                    collapsed.Add(package);
                }
            }

            return collapsed;
        }

        private static IReadOnlyList<string> DeriveNodeScopes(string baseDirectory, string prDirectory)
        {
            string[] candidates = { "src", "lib", "app", "dist" };
            string[] scopes = candidates
                .Where(candidate => Directory.Exists(Path.Combine(baseDirectory, candidate))
                    || Directory.Exists(Path.Combine(prDirectory, candidate)))
                .ToArray();
            if (scopes.Length == 0)
            {
                throw new CliException(
                    "Could not derive a Node scope. Expected at least one of src, lib, app, or dist beside package.json.",
                    ExitCodes.RunInvalid);
            }

            return scopes;
        }

        private static string DetectNodePackageManager(string directory)
        {
            var managers = new List<string>();
            if (File.Exists(Path.Combine(directory, "package-lock.json"))) managers.Add("npm");
            if (File.Exists(Path.Combine(directory, "pnpm-lock.yaml"))) managers.Add("pnpm");
            if (File.Exists(Path.Combine(directory, "yarn.lock"))) managers.Add("yarn");
            if (managers.Count > 1)
            {
                throw new CliException(
                    "Multiple Node lockfiles were detected beside package.json: " + string.Join(", ", managers.Select(LockfileName)) + ".",
                    ExitCodes.RunInvalid);
            }

            if (managers.Count == 0)
            {
                throw new CliException(
                    "Node execution requires package-lock.json beside package.json so npm ci is reproducible.",
                    ExitCodes.RunInvalid);
            }

            return managers[0];
        }

        private static string LockfileName(string manager) => manager switch
        {
            "npm" => "package-lock.json",
            "pnpm" => "pnpm-lock.yaml",
            "yarn" => "yarn.lock",
            _ => manager,
        };

        private static string ResolveJavaAgent()
        {
            string? configured = Environment.GetEnvironmentVariable("BEHAVIORDIFF_JAVA_AGENT");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                string fullPath = Path.GetFullPath(configured);
                if (!File.Exists(fullPath))
                {
                    throw new CliException("BEHAVIORDIFF_JAVA_AGENT does not name an existing jar: " + fullPath);
                }

                return fullPath;
            }

            string packaged = Path.Combine(AppContext.BaseDirectory, "tracers", "java", "behaviordiff-java-agent.jar");
            if (File.Exists(packaged))
            {
                return packaged;
            }

            foreach (string root in CandidateSourceRoots())
            {
                string target = Path.Combine(root, "src", "BehaviorDiff.Java.Agent", "target");
                if (!Directory.Exists(target))
                {
                    continue;
                }

                string? jar = Directory.EnumerateFiles(target, "behaviordiff-java-agent-*.jar", SearchOption.TopDirectoryOnly)
                    .Where(path => !Path.GetFileName(path).StartsWith("original-", StringComparison.Ordinal))
                    .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                    .FirstOrDefault();
                if (jar != null)
                {
                    return jar;
                }
            }

            throw new CliException(
                "BehaviorDiff Java agent was not found. Set BEHAVIORDIFF_JAVA_AGENT, install "
                + packaged + ", or build src/BehaviorDiff.Java.Agent first.");
        }

        private static string ResolveNodeTracer()
        {
            string? configured = Environment.GetEnvironmentVariable("BEHAVIORDIFF_NODE_TRACER");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return ValidateNodeTracer(Path.GetFullPath(configured));
            }

            string packaged = Path.Combine(AppContext.BaseDirectory, "tracers", "node");
            if (Directory.Exists(packaged))
            {
                return ValidateNodeTracer(packaged);
            }

            foreach (string root in CandidateSourceRoots())
            {
                string source = Path.Combine(root, "src", "BehaviorDiff.Node");
                if (Directory.Exists(source))
                {
                    return ValidateNodeTracer(source);
                }
            }

            throw new CliException(
                "BehaviorDiff Node tracer was not found. Set BEHAVIORDIFF_NODE_TRACER or install it at " + packaged + ".");
        }

        private static string ResolveRustTracer()
        {
            string? configured = Environment.GetEnvironmentVariable("BEHAVIORDIFF_RUST_TRACER");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            {
                return Path.GetFullPath(configured);
            }
            string fileName = OperatingSystem.IsWindows() ? "behaviordiff-rust-rewrite.exe" : "behaviordiff-rust-rewrite";
            string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : "osx";
            string architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            string packaged = Path.Combine(AppContext.BaseDirectory, "tracers", "rust", os + "-" + architecture, fileName);
            if (File.Exists(packaged))
            {
                return packaged;
            }
            foreach (string root in CandidateSourceRoots())
            {
                string candidate = Path.Combine(root, "src", "BehaviorDiff.Rust.Tracer", "target", "release", fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            throw new CliException("BehaviorDiff Rust tracer was not found. Set BEHAVIORDIFF_RUST_TRACER or build src/BehaviorDiff.Rust.Tracer.");
        }

        private static string ValidateNodeTracer(string directory)
        {
            var missing = new List<string>();
            foreach (string path in new[] { "register.cjs", "loader.mjs", "package.json", "node_modules" })
            {
                string candidate = Path.Combine(directory, path);
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    missing.Add(path);
                }
            }

            if (missing.Count > 0)
            {
                throw new CliException(
                    "BehaviorDiff Node tracer is not installed at " + directory + ". Missing: "
                    + string.Join(", ", missing) + ". Run npm ci in the tracer directory.");
            }

            return directory;
        }

        private static IEnumerable<string> CandidateSourceRoots()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            {
                DirectoryInfo? current = new(Path.GetFullPath(start));
                while (current != null)
                {
                    string root = current.FullName;
                    if (seen.Add(root)
                        && Directory.Exists(Path.Combine(root, "src", "BehaviorDiff.Node")))
                    {
                        yield return root;
                    }

                    current = current.Parent;
                }
            }
        }

        private static MavenCommand ResolveMaven(string entryPoint, string repositoryRoot)
        {
            string entryDirectory = Path.GetDirectoryName(entryPoint)!;
            foreach (string directory in new[] { entryDirectory, repositoryRoot }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string windowsWrapper = Path.Combine(directory, "mvnw.cmd");
                if (OperatingSystem.IsWindows() && File.Exists(windowsWrapper))
                {
                    return MavenCommand.Script(windowsWrapper);
                }

                string wrapper = Path.Combine(directory, "mvnw");
                if (!OperatingSystem.IsWindows() && File.Exists(wrapper))
                {
                    return MavenCommand.Direct(wrapper);
                }
            }

            return OperatingSystem.IsWindows() ? MavenCommand.Script("mvn") : MavenCommand.Direct("mvn");
        }

        private static ProcessResult RunScriptCommand(
            string command,
            IEnumerable<string> arguments,
            string workingDirectory,
            IDictionary<string, string>? environment = null)
        {
            if (!OperatingSystem.IsWindows())
            {
                return Shell.Run(command, arguments, workingDirectory, environment);
            }

            var commandArguments = new List<string> { "/d", "/s", "/c", command };
            commandArguments.AddRange(arguments);
            return Shell.Run("cmd.exe", commandArguments, workingDirectory, environment);
        }

        private static TraceSummary ValidateTrace(string directory, string label, string output)
        {
            string[] traces = Directory.GetFiles(directory, "run.*.ndjson")
                .Where(path => !path.Contains(".manifest.", StringComparison.Ordinal))
                .ToArray();
            long bytes = traces.Sum(path => new FileInfo(path).Length);
            int records = 0;
            try
            {
                foreach (string trace in traces)
                {
                    foreach (string line in File.ReadLines(trace).Where(value => !string.IsNullOrWhiteSpace(value)))
                    {
                        using JsonDocument document = JsonDocument.Parse(line);
                        JsonElement root = document.RootElement;
                        if (!root.TryGetProperty("testId", out _)
                            || !root.TryGetProperty("methodFullName", out _))
                        {
                            throw new JsonException("event is missing testId or methodFullName");
                        }

                        records++;
                    }
                }
            }
            catch (JsonException ex)
            {
                throw new CliException(
                    "INVALID TRACE: " + label + " produced malformed NDJSON: " + ex.Message
                    + Environment.NewLine + Shell.Tail(output, 30),
                    ExitCodes.RunInvalid);
            }

            string[] manifests = Directory.GetFiles(directory, "run.*.manifest.ndjson");
            long manifestBytes = manifests.Sum(path => new FileInfo(path).Length);
            if (traces.Length == 0 || bytes == 0 || records == 0 || manifests.Length == 0 || manifestBytes == 0)
            {
                throw new CliException(
                    "NO EVENTS: " + label + " produced " + traces.Length + " trace file(s), " + records
                    + " event(s), and " + manifests.Length + " manifest(s). A nonzero test exit is allowed only "
                    + "when a valid nonempty trace was produced."
                    + Environment.NewLine + Shell.Tail(output, 30),
                    ExitCodes.RunInvalid);
            }

            return new TraceSummary(traces.Length, bytes, records);
        }

        private static void ReportTrace(string label, TraceSummary summary, int exitCode)
        {
            Console.WriteLine("  " + label.PadRight(10) + " traces=" + summary.Files + " bytes=" + summary.Bytes
                + " events=" + summary.Records + " test-exit=" + exitCode);
        }

        private static string QuoteNodeOption(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

        private sealed class MavenCommand
        {
            private readonly string _fileName;
            private readonly IReadOnlyList<string> _prefix;

            private MavenCommand(string fileName, IReadOnlyList<string> prefix, string displayName)
            {
                _fileName = fileName;
                _prefix = prefix;
                DisplayName = displayName;
            }

            internal string DisplayName { get; }

            internal static MavenCommand Direct(string fileName) => new(fileName, Array.Empty<string>(), Path.GetFileName(fileName));

            internal static MavenCommand Script(string path) => new(
                "cmd.exe",
                new[] { "/d", "/s", "/c", path },
                Path.GetFileName(path));

            internal ProcessResult Run(
                IEnumerable<string> arguments,
                string workingDirectory,
                IDictionary<string, string>? environment = null) =>
                Shell.Run(_fileName, _prefix.Concat(arguments), workingDirectory, environment);
        }

        private sealed class TraceSummary
        {
            internal TraceSummary(int files, long bytes, int records)
            {
                Files = files;
                Bytes = bytes;
                Records = records;
            }

            internal int Files { get; }

            internal long Bytes { get; }

            internal int Records { get; }
        }
    }
}