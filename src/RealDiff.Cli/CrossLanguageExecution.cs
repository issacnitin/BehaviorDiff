using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace RealDiff.Cli
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
        private sealed record PythonRuntimeInfo(string Executable, string Version);

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
                .Where(path => Path.GetFileName(path) is "target" or "node_modules" or "dist" or "__pycache__" or ".pytest_cache")
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
                RepositoryLanguage.Go => RunGo(baseDetection, prDetection, baseTree, prTree, targetSha),
                RepositoryLanguage.Rust => RunRust(baseDetection, prDetection, baseTree, prTree, targetSha),
                RepositoryLanguage.Python => RunPython(baseDetection, prDetection, baseTree, prTree, targetSha),
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
                case RepositoryLanguage.Go:
                    WarmGo(detection, baseTree, targetSha);
                    break;
                case RepositoryLanguage.Rust:
                    WarmRust(detection, baseTree, targetSha);
                    break;
                case RepositoryLanguage.Python:
                    WarmPython(detection, baseTree, targetSha);
                    break;
                default:
                    throw new CliException("Cache warming was requested for " + detection.Language + ".");
            }
        }

        private void WarmJava(LanguageDetection detection, string baseTree, string targetSha)
        {
            Console.WriteLine();
            Console.WriteLine("=== 2. Java clean build ===");
            bool gradle = IsGradleEntryPoint(detection.EntryPoint);
            MavenCommand maven = gradle ? ResolveGradle(detection.EntryPoint, baseTree) : ResolveMaven(detection.EntryPoint, baseTree);
            if (detection.HasCustomBuild) RunConfiguredBuild("base", detection);
            else if (gradle) BuildGradle("base", detection, maven);
            else BuildJava("base", detection.EntryPoint, maven);

            string scope = string.Join(",", detection.IncludeNamespaces);
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
            string base1 = RunJavaTests("base_run1", detection, baseTree, maven, gradle, scope, agent);
            RunJavaTests("base_run2", detection, baseTree, maven, gradle, scope, agent);
            RunJavaTests("base_run3", detection, baseTree, maven, gradle, scope, agent);
            Pipeline.AssertTestIdsPresent(base1);
            stopwatch.Stop();
            _cache.Store(key, baseTree, stopwatch.ElapsedMilliseconds);
        }

        private void WarmNode(LanguageDetection detection, string baseTree, string targetSha)
        {
            Console.WriteLine();
            Console.WriteLine("=== 2. Node clean build ===");
            string baseDirectory = Path.GetDirectoryName(detection.EntryPoint)!;
            string manager = NodePackageManagers.Detect(baseDirectory);

            if (detection.HasCustomBuild) RunConfiguredBuild("base", detection);
            else BuildNode("base", baseDirectory, manager);
            string scope = string.Join(";", detection.IncludeNamespaces);
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
            string base1 = RunNodeTests("base_run1", detection, baseTree, manager, scope, tracer);
            RunNodeTests("base_run2", detection, baseTree, manager, scope, tracer);
            RunNodeTests("base_run3", detection, baseTree, manager, scope, tracer);
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
            bool baseGradle = IsGradleEntryPoint(baseDetection.EntryPoint);
            bool prGradle = IsGradleEntryPoint(prDetection.EntryPoint);
            if (baseGradle != prGradle)
            {
                throw new CliException("Base and PR use different Java build systems.", ExitCodes.RunInvalid);
            }
            MavenCommand baseMaven = baseGradle ? ResolveGradle(baseDetection.EntryPoint, baseTree) : ResolveMaven(baseDetection.EntryPoint, baseTree);
            MavenCommand prMaven = prGradle ? ResolveGradle(prDetection.EntryPoint, prTree) : ResolveMaven(prDetection.EntryPoint, prTree);
            if (baseDetection.HasCustomBuild) RunConfiguredBuild("base", baseDetection);
            else if (baseGradle) BuildGradle("base", baseDetection, baseMaven);
            else BuildJava("base", baseDetection.EntryPoint, baseMaven);
            if (prDetection.HasCustomBuild) RunConfiguredBuild("pr", prDetection);
            else if (prGradle) BuildGradle("pr", prDetection, prMaven);
            else BuildJava("pr", prDetection.EntryPoint, prMaven);

            Console.WriteLine();
            Console.WriteLine("=== 3. Java trace scope and agent ===");
            string scope = string.Join(",", baseDetection.IncludeNamespaces);
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
                base1 = RunJavaTests("base_run1", baseDetection, baseTree, baseMaven, baseGradle, scope, agent);
                base2 = RunJavaTests("base_run2", baseDetection, baseTree, baseMaven, baseGradle, scope, agent);
                base3 = RunJavaTests("base_run3", baseDetection, baseTree, baseMaven, baseGradle, scope, agent);
                stopwatch.Stop();
                baseRoot = baseTree;
                _cache.Store(key, baseRoot, stopwatch.ElapsedMilliseconds);
            }

            Pipeline.AssertTestIdsPresent(base1);
            string pr = RunJavaTests("pr_run", prDetection, prTree, prMaven, prGradle, scope, agent);
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
            string baseManager = NodePackageManagers.Detect(baseDirectory);
            string prManager = NodePackageManagers.Detect(prDirectory);
            if (!string.Equals(baseManager, prManager, StringComparison.Ordinal))
            {
                throw new CliException(
                    "Base uses " + baseManager + " but PR uses " + prManager
                    + ". Different Node package managers make the build inputs incomparable.",
                    ExitCodes.RunInvalid);
            }

            if (baseDetection.HasCustomBuild) RunConfiguredBuild("base", baseDetection);
            else BuildNode("base", baseDirectory, baseManager);
            if (prDetection.HasCustomBuild) RunConfiguredBuild("pr", prDetection);
            else BuildNode("pr", prDirectory, prManager);
            buildStopwatch.Stop();
            _timings.BuildMilliseconds += buildStopwatch.ElapsedMilliseconds;

            Console.WriteLine();
            Console.WriteLine("=== 3. Node trace scope and tracer ===");
            string scope = string.Join(";", baseDetection.IncludeNamespaces);
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
                base1 = RunNodeTests("base_run1", baseDetection, baseTree, baseManager, scope, tracer);
                base2 = RunNodeTests("base_run2", baseDetection, baseTree, baseManager, scope, tracer);
                base3 = RunNodeTests("base_run3", baseDetection, baseTree, baseManager, scope, tracer);
                stopwatch.Stop();
                _timings.InstrumentedRunMilliseconds += stopwatch.ElapsedMilliseconds;
                baseRoot = baseTree;
                _cache.Store(key, baseRoot, stopwatch.ElapsedMilliseconds);
            }

            Pipeline.AssertTestIdsPresent(base1);
            var prStopwatch = Stopwatch.StartNew();
            string pr = RunNodeTests("pr_run", prDetection, prTree, prManager, scope, tracer);
            prStopwatch.Stop();
            _timings.InstrumentedRunMilliseconds += prStopwatch.ElapsedMilliseconds;
            return new CrossLanguageRunSet { Base1 = base1, Base2 = base2, Base3 = base3, Pr = pr, BaseRoot = baseRoot };
        }

        private void WarmGo(LanguageDetection detection, string baseTree, string targetSha)
        {
            RunConfiguredBuild("base", detection);
            string rewriter = ResolveGoRewriter();
            var key = new TraceCacheKey(targetSha, "go", TracerFingerprint.ForFile(rewriter), Pipeline.ScopeConfig(
                string.Join(";", detection.ExcludeNamespaces)));
            if (_cache.TryRestore(key, out _))
            {
                return;
            }
            var stopwatch = Stopwatch.StartNew();
            RunGoTests("base_run1", detection, Path.Combine(_work, "go-base-cache"), rewriter);
            RunGoTests("base_run2", detection, Path.Combine(_work, "go-base-cache"), rewriter);
            string base3 = RunGoTests("base_run3", detection, Path.Combine(_work, "go-base-cache"), rewriter);
            stopwatch.Stop();
            Pipeline.AssertTestIdsPresent(base3);
            _cache.Store(key, baseTree, stopwatch.ElapsedMilliseconds);
        }

        private CrossLanguageRunSet RunGo(
            LanguageDetection baseDetection,
            LanguageDetection prDetection,
            string baseTree,
            string prTree,
            string targetSha)
        {
            Console.WriteLine();
            Console.WriteLine("=== 2. Go stable source rewriting ===");
            RunConfiguredBuild("base", baseDetection);
            RunConfiguredBuild("pr", prDetection);
            string rewriter = ResolveGoRewriter();
            Console.WriteLine("  go rewriter: " + rewriter);
            var key = new TraceCacheKey(targetSha, "go", TracerFingerprint.ForFile(rewriter), Pipeline.ScopeConfig(
                string.Join(";", baseDetection.ExcludeNamespaces)));
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
                string cache = Path.Combine(_work, "go-base-cache");
                base1 = RunGoTests("base_run1", baseDetection, cache, rewriter);
                base2 = RunGoTests("base_run2", baseDetection, cache, rewriter);
                base3 = RunGoTests("base_run3", baseDetection, cache, rewriter);
                stopwatch.Stop();
                _timings.InstrumentedRunMilliseconds += stopwatch.ElapsedMilliseconds;
                baseRoot = baseTree;
                _cache.Store(key, baseRoot, stopwatch.ElapsedMilliseconds);
            }
            Pipeline.AssertTestIdsPresent(base1);
            string pr = RunGoTests("pr_run", prDetection, Path.Combine(_work, "go-pr-cache"), rewriter);
            return new CrossLanguageRunSet { Base1 = base1, Base2 = base2, Base3 = base3, Pr = pr, BaseRoot = baseRoot };
        }

        private string RunGoTests(string label, LanguageDetection detection, string cache, string rewriter)
        {
            string directory = PrepareRunDirectory(label);
            string rewritten = Path.Combine(cache, label);
            if (Directory.Exists(rewritten)) Directory.Delete(rewritten, recursive: true);
            Directory.CreateDirectory(cache);
            var rewriteArguments = new List<string> { "--source", detection.WorkDirectory, "--out", rewritten };
            if (detection.ExcludeNamespaces.Length > 0)
            {
                rewriteArguments.Add("--exclude");
                rewriteArguments.Add(string.Join(",", detection.ExcludeNamespaces));
            }
            ProcessResult rewrite = Shell.Run(rewriter, rewriteArguments, detection.WorkDirectory);
            if (!rewrite.Ok)
            {
                throw new CliException("Go source rewriting failed." + Environment.NewLine + Shell.Tail(rewrite.Output, 25), ExitCodes.RepoDoesNotBuild);
            }

            var environment = new Dictionary<string, string>
            {
                ["REALDIFF_TRACE"] = Path.Combine(directory, "run.ndjson"),
                ["REALDIFF_REPOSITORY_ROOT"] = detection.Config.RepositoryRoot,
            };
            string command = detection.HasCustomTest ? detection.TestCommand : "go test ./...";
            Console.WriteLine("  " + label.PadRight(10) + " command: " + command);
            ProcessResult test = Shell.RunCommand(command, rewritten, environment);
            TraceSummary summary = ValidateTrace(directory, label, test.Output);
            ReportTrace(label, summary, test.ExitCode);
            return directory;
        }

        private void WarmPython(LanguageDetection detection, string baseTree, string targetSha)
        {
            PythonRuntimeInfo python = ResolvePythonRuntime(detection.WorkDirectory);
            string tracer = ResolvePythonTracer();
            TraceCacheKey key = PythonCacheKey(targetSha, detection, tracer, python);
            if (_cache.TryRestore(key, out _))
            {
                return;
            }
            var stopwatch = Stopwatch.StartNew();
            string base1 = RunPythonTests("base_run1", detection, python, tracer);
            RunPythonTests("base_run2", detection, python, tracer);
            RunPythonTests("base_run3", detection, python, tracer);
            stopwatch.Stop();
            Pipeline.AssertTestIdsPresent(base1);
            _cache.Store(key, baseTree, stopwatch.ElapsedMilliseconds);
        }

        private CrossLanguageRunSet RunPython(
            LanguageDetection baseDetection,
            LanguageDetection prDetection,
            string baseTree,
            string prTree,
            string targetSha)
        {
            Console.WriteLine();
            Console.WriteLine("=== 2. Python process-start monitoring ===");
            if (baseDetection.HasCustomBuild) RunConfiguredBuild("base", baseDetection);
            if (prDetection.HasCustomBuild) RunConfiguredBuild("pr", prDetection);
            PythonRuntimeInfo python = ResolvePythonRuntime(baseDetection.WorkDirectory);
            string tracer = ResolvePythonTracer();
            Console.WriteLine("  python runtime: " + python.Executable + " (" + python.Version + ")");
            Console.WriteLine("  python tracer : " + tracer);
            TraceCacheKey key = PythonCacheKey(targetSha, baseDetection, tracer, python);
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
                base1 = RunPythonTests("base_run1", baseDetection, python, tracer);
                base2 = RunPythonTests("base_run2", baseDetection, python, tracer);
                base3 = RunPythonTests("base_run3", baseDetection, python, tracer);
                stopwatch.Stop();
                _timings.InstrumentedRunMilliseconds += stopwatch.ElapsedMilliseconds;
                baseRoot = baseTree;
                _cache.Store(key, baseRoot, stopwatch.ElapsedMilliseconds);
            }
            Pipeline.AssertTestIdsPresent(base1);
            var prStopwatch = Stopwatch.StartNew();
            string pr = RunPythonTests("pr_run", prDetection, python, tracer);
            prStopwatch.Stop();
            _timings.InstrumentedRunMilliseconds += prStopwatch.ElapsedMilliseconds;
            return new CrossLanguageRunSet { Base1 = base1, Base2 = base2, Base3 = base3, Pr = pr, BaseRoot = baseRoot };
        }

        private string RunPythonTests(
            string label,
            LanguageDetection detection,
            PythonRuntimeInfo python,
            string tracer)
        {
            string directory = PrepareRunDirectory(label);
            string existingPythonPath = Environment.GetEnvironmentVariable("PYTHONPATH") ?? string.Empty;
            var pythonPaths = new List<string> { tracer };
            string sourceDirectory = Path.Combine(detection.WorkDirectory, "src");
            if (Directory.Exists(sourceDirectory)) pythonPaths.Add(sourceDirectory);
            if (!string.IsNullOrEmpty(existingPythonPath)) pythonPaths.Add(existingPythonPath);
            string pythonPath = string.Join(Path.PathSeparator, pythonPaths);
            var environment = new Dictionary<string, string>
            {
                ["PYTHONPATH"] = pythonPath,
                ["PYTHONDONTWRITEBYTECODE"] = "1",
                ["REALDIFF_TRACE"] = Path.Combine(directory, "run.python.ndjson"),
                ["REALDIFF_REPOSITORY_ROOT"] = detection.Config.RepositoryRoot,
                ["REALDIFF_INCLUDE_NAMESPACES"] = string.Join(",", detection.IncludeNamespaces),
                ["REALDIFF_EXCLUDE_NAMESPACES"] = string.Join(",", detection.ExcludeNamespaces),
            };
            ProcessResult result;
            if (detection.HasCustomTest)
            {
                Console.WriteLine("  " + label.PadRight(10) + " configured command: " + detection.TestCommand);
                result = Shell.RunCommand(detection.TestCommand, detection.WorkDirectory, environment);
            }
            else
            {
                Console.WriteLine("  " + label.PadRight(10) + " command: python -m pytest");
                result = Shell.Run(
                    python.Executable,
                    new[] { "-m", "pytest", "-p", "realdiff_python.pytest_plugin" },
                    detection.WorkDirectory,
                    environment);
            }
            TraceSummary summary = ValidateTrace(directory, label, result.Output);
            ReportTrace(label, summary, result.ExitCode);
            return directory;
        }

        private static TraceCacheKey PythonCacheKey(
            string targetSha,
            LanguageDetection detection,
            string tracer,
            PythonRuntimeInfo python)
        {
            string fingerprint = TracerFingerprint.ForDirectory(
                tracer,
                path => !path.Replace('\\', '/').Contains("/__pycache__/", StringComparison.Ordinal)
                    && Path.GetExtension(path) == ".py");
            string scope = string.Join(";", detection.IncludeNamespaces) + "\npython=" + python.Version;
            return new TraceCacheKey(targetSha, "python", fingerprint, Pipeline.ScopeConfig(scope));
        }

        private void WarmRust(LanguageDetection detection, string baseTree, string targetSha)
        {
            if (detection.HasCustomBuild) RunConfiguredBuild("base", detection);
            string tracer = ResolveRustTracer();
            var key = new TraceCacheKey(targetSha, "rust", TracerFingerprint.ForFile(tracer), Pipeline.ScopeConfig(string.Empty));
            if (_cache.TryRestore(key, out _))
            {
                return;
            }
            var stopwatch = Stopwatch.StartNew();
            RunRustTests("base_run1", detection, Path.Combine(_work, "rust-base-cache"), tracer);
            RunRustTests("base_run2", detection, Path.Combine(_work, "rust-base-cache"), tracer);
            RunRustTests("base_run3", detection, Path.Combine(_work, "rust-base-cache"), tracer);
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
            if (baseDetection.HasCustomBuild) RunConfiguredBuild("base", baseDetection);
            if (prDetection.HasCustomBuild) RunConfiguredBuild("pr", prDetection);
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
                base1 = RunRustTests("base_run1", baseDetection, cache, tracer);
                base2 = RunRustTests("base_run2", baseDetection, cache, tracer);
                base3 = RunRustTests("base_run3", baseDetection, cache, tracer);
                stopwatch.Stop();
                _timings.InstrumentedRunMilliseconds += stopwatch.ElapsedMilliseconds;
                baseRoot = baseTree;
                _cache.Store(key, baseRoot, stopwatch.ElapsedMilliseconds);
            }
            Pipeline.AssertTestIdsPresent(base1);
            string pr = RunRustTests("pr_run", prDetection, Path.Combine(_work, "rust-pr-cache"), tracer);
            return new CrossLanguageRunSet { Base1 = base1, Base2 = base2, Base3 = base3, Pr = pr, BaseRoot = baseRoot };
        }

        private string RunRustTests(string label, LanguageDetection detection, string cache, string tracer)
        {
            string source = detection.WorkDirectory;
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
            var environment = new Dictionary<string, string> { ["REALDIFF_RUST_EXIT_TRACE"] = trace };
            ProcessResult test;
            if (detection.HasCustomTest)
            {
                Console.WriteLine("  " + label.PadRight(10) + " configured command: " + detection.TestCommand);
                test = Shell.RunCommand(detection.TestCommand, rewritten, environment);
            }
            else
            {
                Console.WriteLine("  " + label.PadRight(10) + " command: cargo test -- --test-threads=1");
                test = RunScriptCommand(
                    "cargo",
                    new[] { "test", "--quiet", "--manifest-path", Path.Combine(rewritten, "Cargo.toml"), "--", "--test-threads=1" },
                    rewritten,
                    environment);
            }
            string origin = Path.Combine(rewritten, ".realdiff-rust-origin.json");
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

        private static void RunConfiguredBuild(string label, LanguageDetection detection)
        {
            Console.WriteLine("  " + label + " configured command: " + detection.BuildCommand);
            ProcessResult result = Shell.RunCommand(detection.BuildCommand, detection.WorkDirectory);
            if (!result.Ok)
            {
                throw new CliException(
                    "The configured build command failed before instrumentation."
                    + Environment.NewLine + "    Worktree: " + label
                    + Environment.NewLine + "    Workdir: " + detection.Workdir
                    + Environment.NewLine + Shell.Tail(result.Output, 25),
                    ExitCodes.RepoDoesNotBuild);
            }
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

        private static void BuildGradle(string label, LanguageDetection detection, MavenCommand gradle)
        {
            var arguments = new[] { "--no-daemon", "build", "-x", "test" };
            Console.WriteLine("  " + label + " command: " + gradle.DisplayName + " --no-daemon build -x test");
            ProcessResult result = gradle.Run(arguments, detection.WorkDirectory);
            if (!result.Ok)
            {
                throw new CliException(
                    "This Gradle Java repository does not build in this environment, before instrumentation."
                    + Environment.NewLine + "    Worktree: " + label
                    + Environment.NewLine + Shell.Tail(result.Output, 25),
                    ExitCodes.RepoDoesNotBuild);
            }
        }

        private static void BuildNode(string label, string directory, string manager)
        {
            string[] installArguments = NodePackageManagers.InstallArguments(manager, directory);
            Console.WriteLine("  " + label + " command: " + manager + " " + string.Join(" ", installArguments));
            ProcessResult install = RunScriptCommand(manager, installArguments, directory);
            if (!install.Ok)
            {
                throw new CliException(
                    "This Node repository does not install from " + NodePackageManagers.LockfileName(manager)
                    + " in this environment, before instrumentation."
                    + Environment.NewLine + "    Worktree: " + label
                    + Environment.NewLine + Shell.Tail(install.Output, 25),
                    ExitCodes.RepoDoesNotBuild);
            }

            if (!NodePackageManagers.HasScript(directory, "build"))
            {
                Console.WriteLine("  " + label + " command: no build script");
                return;
            }

            Console.WriteLine("  " + label + " command: " + manager + " run build");
            ProcessResult build = RunScriptCommand(manager, new[] { "run", "build" }, directory);
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
            LanguageDetection detection,
            string repositoryRoot,
            MavenCommand maven,
            bool gradle,
            string scope,
            string agent)
        {
            string directory = PrepareRunDirectory(label);
            string trace = Path.Combine(directory, "run.ndjson");
            string argLine = "--add-opens java.base/java.util=ALL-UNNAMED -javaagent:\"" + agent + "\"";
            var environment = new Dictionary<string, string>
            {
                ["REALDIFF_TRACE"] = trace,
                ["REALDIFF_NAMESPACES"] = scope,
                ["REALDIFF_REPOSITORY_ROOT"] = repositoryRoot,
                ["REALDIFF_JAVA_SOURCE_ROOTS"] = string.Join(";", detection.SourceRoots),
            };
            ProcessResult result;
            if (detection.HasCustomTest)
            {
                string existing = Environment.GetEnvironmentVariable("JAVA_TOOL_OPTIONS") ?? string.Empty;
                environment["JAVA_TOOL_OPTIONS"] = (existing + " " + argLine).Trim();
                Console.WriteLine("  " + label.PadRight(10) + " configured command: " + detection.TestCommand);
                result = Shell.RunCommand(detection.TestCommand, detection.WorkDirectory, environment);
            }
            else if (gradle)
            {
                string initScript = Path.Combine(directory, "realdiff.init.gradle");
                File.WriteAllText(initScript,
                    "allprojects { tasks.withType(Test).configureEach { "
                    + "jvmArgs '--add-opens=java.base/java.util=ALL-UNNAMED'; "
                    + "jvmArgs '" + EscapeGradleString("-javaagent:" + agent) + "' } }" + Environment.NewLine);
                var arguments = new[] { "--no-daemon", "--rerun-tasks", "--init-script", initScript, "test" };
                Console.WriteLine("  " + label.PadRight(10) + " command: " + maven.DisplayName + " --rerun-tasks test (forked JVM += add-opens + javaagent)");
                result = maven.Run(arguments, detection.WorkDirectory, environment);
            }
            else
            {
                var arguments = new[]
                {
                    "--batch-mode",
                    "--no-transfer-progress",
                    "-f",
                    detection.EntryPoint,
                    "test",
                    "-DargLine=" + argLine,
                };
                Console.WriteLine("  " + label.PadRight(10) + " command: " + maven.DisplayName + " test -DargLine=<add-opens + javaagent>");
                result = maven.Run(arguments, detection.WorkDirectory, environment);
            }
            TraceSummary summary = ValidateTrace(directory, label, result.Output);
            ReportTrace(label, summary, result.ExitCode);
            return directory;
        }

        private static string EscapeGradleString(string value) =>
            value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

        private string RunNodeTests(
            string label,
            LanguageDetection detection,
            string repositoryRoot,
            string manager,
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
                ["REALDIFF_TRACE"] = Path.Combine(directory, "run.ndjson"),
                ["REALDIFF_NAMESPACES"] = scope,
                ["REALDIFF_NODE_ROOT"] = tracer,
                ["REALDIFF_REPOSITORY_ROOT"] = repositoryRoot,
                ["CI"] = "true",
                ["NODE_OPTIONS"] = string.IsNullOrWhiteSpace(existingOptions)
                    ? additions
                    : existingOptions.Trim() + " " + additions,
            };
            ProcessResult result;
            if (detection.HasCustomTest)
            {
                Console.WriteLine("  " + label.PadRight(10) + " configured command: " + detection.TestCommand);
                result = Shell.RunCommand(detection.TestCommand, detection.WorkDirectory, environment);
            }
            else
            {
                Console.WriteLine("  " + label.PadRight(10) + " command: " + manager + " run test (NODE_OPTIONS += --require register.cjs --loader loader.mjs)");
                result = RunScriptCommand(manager, new[] { "run", "test" }, detection.WorkDirectory, environment);
            }
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

        private static string ResolveJavaAgent()
        {
            string? configured = Environment.GetEnvironmentVariable("REALDIFF_JAVA_AGENT");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                string fullPath = Path.GetFullPath(configured);
                if (!File.Exists(fullPath))
                {
                    throw new CliException("REALDIFF_JAVA_AGENT does not name an existing jar: " + fullPath);
                }

                return fullPath;
            }

            string packaged = Path.Combine(AppContext.BaseDirectory, "tracers", "java", "realdiff-java-agent.jar");
            if (File.Exists(packaged))
            {
                return packaged;
            }

            foreach (string root in CandidateSourceRoots())
            {
                string target = Path.Combine(root, "src", "RealDiff.Java.Agent", "target");
                if (!Directory.Exists(target))
                {
                    continue;
                }

                string? jar = Directory.EnumerateFiles(target, "realdiff-java-agent-*.jar", SearchOption.TopDirectoryOnly)
                    .Where(path => !Path.GetFileName(path).StartsWith("original-", StringComparison.Ordinal))
                    .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                    .FirstOrDefault();
                if (jar != null)
                {
                    return jar;
                }
            }

            throw new CliException(
                "RealDiff Java agent was not found. Set REALDIFF_JAVA_AGENT, install "
                + packaged + ", or build src/RealDiff.Java.Agent first.");
        }

        private static string ResolveNodeTracer()
        {
            string? configured = Environment.GetEnvironmentVariable("REALDIFF_NODE_TRACER");
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
                string source = Path.Combine(root, "src", "RealDiff.Node");
                if (Directory.Exists(source))
                {
                    return ValidateNodeTracer(source);
                }
            }

            throw new CliException(
                "RealDiff Node tracer was not found. Set REALDIFF_NODE_TRACER or install it at " + packaged + ".");
        }

        private static PythonRuntimeInfo ResolvePythonRuntime(string workingDirectory)
        {
            string? configured = Environment.GetEnvironmentVariable("REALDIFF_PYTHON");
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);
            if (OperatingSystem.IsWindows())
            {
                candidates.Add(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "Python", "Python312", "python.exe"));
                candidates.Add("python.exe");
            }
            else
            {
                candidates.Add("python3.12");
                candidates.Add("python3");
                candidates.Add("python");
            }

            foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Path.IsPathRooted(candidate) && !File.Exists(candidate)) continue;
                try
                {
                    ProcessResult result = Shell.Run(
                        candidate,
                        new[]
                        {
                            "-c",
                            "import sys; print('.'.join(map(str, sys.version_info[:3]))); "
                                + "raise SystemExit(0 if sys.version_info >= (3, 12) and hasattr(sys, 'monitoring') else 12)",
                        },
                        workingDirectory);
                    if (result.ExitCode == 12)
                    {
                        throw new CliException(
                            "RealDiff Python tracing requires Python 3.12+ with sys.monitoring; " + candidate + " is older.",
                            ExitCodes.RunInvalid);
                    }
                    if (result.Ok)
                    {
                        return new PythonRuntimeInfo(candidate, result.Output.Trim());
                    }
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
            }
            throw new CliException(
                "Python 3.12+ with sys.monitoring was not found. Set REALDIFF_PYTHON to the interpreter path; sys.settrace is not supported.",
                ExitCodes.RunInvalid);
        }

        private static string ResolvePythonTracer()
        {
            string? configured = Environment.GetEnvironmentVariable("REALDIFF_PYTHON_TRACER");
            if (!string.IsNullOrWhiteSpace(configured)) return ValidatePythonTracer(Path.GetFullPath(configured));
            string packaged = Path.Combine(AppContext.BaseDirectory, "tracers", "python");
            if (Directory.Exists(packaged)) return ValidatePythonTracer(packaged);
            foreach (string root in CandidateSourceRoots())
            {
                string source = Path.Combine(root, "src", "RealDiff.Python");
                if (Directory.Exists(source)) return ValidatePythonTracer(source);
            }
            throw new CliException(
                "RealDiff Python tracer was not found. Set REALDIFF_PYTHON_TRACER or install it at " + packaged + ".",
                ExitCodes.RunInvalid);
        }

        private static string ValidatePythonTracer(string directory)
        {
            string[] required =
            {
                "sitecustomize.py",
                Path.Combine("realdiff_python", "monitor.py"),
                Path.Combine("realdiff_python", "runtime.py"),
                Path.Combine("realdiff_python", "canonical.py"),
                Path.Combine("realdiff_python", "pytest_plugin.py"),
            };
            string[] missing = required.Where(path => !File.Exists(Path.Combine(directory, path))).ToArray();
            if (missing.Length > 0)
            {
                throw new CliException(
                    "RealDiff Python tracer is incomplete at " + directory + ". Missing: " + string.Join(", ", missing) + ".",
                    ExitCodes.RunInvalid);
            }
            return directory;
        }

        private static string ResolveRustTracer()
        {
            string? configured = Environment.GetEnvironmentVariable("REALDIFF_RUST_TRACER");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            {
                return Path.GetFullPath(configured);
            }
            string fileName = OperatingSystem.IsWindows() ? "realdiff-rust-rewrite.exe" : "realdiff-rust-rewrite";
            string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : "osx";
            string architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            string packaged = Path.Combine(AppContext.BaseDirectory, "tracers", "rust", os + "-" + architecture, fileName);
            if (File.Exists(packaged))
            {
                return packaged;
            }
            foreach (string root in CandidateSourceRoots())
            {
                string candidate = Path.Combine(root, "src", "RealDiff.Rust.Tracer", "target", "release", fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            throw new CliException("RealDiff Rust tracer was not found. Set REALDIFF_RUST_TRACER or build src/RealDiff.Rust.Tracer.");
        }

        private static string ResolveGoRewriter()
        {
            string? configured = Environment.GetEnvironmentVariable("REALDIFF_GO_REWRITER");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            {
                return Path.GetFullPath(configured);
            }
            string fileName = OperatingSystem.IsWindows() ? "realdiff-go-rewrite.exe" : "realdiff-go-rewrite";
            string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : "osx";
            string architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            string packaged = Path.Combine(AppContext.BaseDirectory, "tracers", "go", os + "-" + architecture, fileName);
            if (File.Exists(packaged))
            {
                return packaged;
            }
            foreach (string root in CandidateSourceRoots())
            {
                string candidate = Path.Combine(root, "artifacts", "go", fileName);
                if (File.Exists(candidate)) return candidate;
            }
            throw new CliException(
                "RealDiff Go rewriter was not found. Set REALDIFF_GO_REWRITER or install it at " + packaged + ".",
                ExitCodes.RunInvalid);
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
                    "RealDiff Node tracer is not installed at " + directory + ". Missing: "
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
                        && Directory.Exists(Path.Combine(root, "src", "RealDiff.Node")))
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

        private static MavenCommand ResolveGradle(string entryPoint, string repositoryRoot)
        {
            string entryDirectory = Path.GetDirectoryName(entryPoint)!;
            foreach (string directory in new[] { entryDirectory, repositoryRoot }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string windowsWrapper = Path.Combine(directory, "gradlew.bat");
                if (OperatingSystem.IsWindows() && File.Exists(windowsWrapper))
                {
                    return MavenCommand.Script(windowsWrapper);
                }
                string wrapper = Path.Combine(directory, "gradlew");
                if (!OperatingSystem.IsWindows() && File.Exists(wrapper))
                {
                    return MavenCommand.Direct(wrapper);
                }
            }
            return OperatingSystem.IsWindows() ? MavenCommand.Script("gradle") : MavenCommand.Direct("gradle");
        }

        private static bool IsGradleEntryPoint(string entryPoint) =>
            entryPoint.EndsWith("build.gradle", StringComparison.OrdinalIgnoreCase)
            || entryPoint.EndsWith("build.gradle.kts", StringComparison.OrdinalIgnoreCase);

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