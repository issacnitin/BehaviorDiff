using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RealDiff.Cli
{
    internal static class Program
    {
        internal static int Main(string[] args)
        {
            if (args.Length == 2 && (args[0] == "detect" || args[0] == "detect-language"))
            {
                try
                {
                    LanguageDetection detection = LanguageDetector.Detect(args[1]);
                    Console.WriteLine(LanguageDetector.Render(detection));
                    return ExitCodes.NoUnexpected;
                }
                catch (CliException ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    return ex.ExitCode;
                }
            }

            if (args.Length > 0 && args[0] == "post")
            {
                try
                {
                    return PostingCommand.Run(args.Skip(1).ToArray());
                }
                catch (CliException ex)
                {
                    Console.Error.WriteLine("POST FAILED: " + ex.Message);
                    return ExitCodes.BuildOrTestFailure;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("POST FAILED: " + ex.GetType().Name + ": " + ex.Message);
                    return ExitCodes.BuildOrTestFailure;
                }
            }

            if (args.Length > 0 && args[0] == "baseline")
            {
                try
                {
                    return BaselineCommand.Run(args.Skip(1).ToArray());
                }
                catch (CliException ex)
                {
                    Console.Error.WriteLine("BASELINE FAILED: " + ex.Message);
                    return ExitCodes.BuildOrTestFailure;
                }
            }

            bool warmOnly = args.Length > 0 && args[0] == "warm";
            int firstOption = warmOnly ? 1 : 0;
            string? baseRef = null;
            string? prRef = null;
            string? targetRef = null;
            string? ciProvider = null;
            string? work = null;
            string? findings = null;
            string? baseline = null;
            string? cacheDirectory = null;
            TimeSpan cacheRetention = TimeSpan.FromDays(1);
            TimeSpan? traceRetention = null;
            bool keep = false;
            bool noBaseline = false;
            bool strict = false;
            var positional = new List<string>();

            for (int i = firstOption; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--base": baseRef = Next(args, ref i); break;
                    case "--pr": prRef = Next(args, ref i); break;
                    case "--target": targetRef = Next(args, ref i); break;
                    case "--ci": ciProvider = Next(args, ref i); break;
                    case "--work": work = Next(args, ref i); break;
                    case "--findings": findings = Next(args, ref i); break;
                    case "--baseline": baseline = Next(args, ref i); break;
                    case "--no-baseline": noBaseline = true; break;
                    case "--cache-dir": cacheDirectory = Next(args, ref i); break;
                    case "--cache-retention": cacheRetention = ParseDuration(Next(args, ref i)); break;
                    case "--no-cache": cacheDirectory = null; break;
                    case "--keep-traces": traceRetention = ParseDuration(Next(args, ref i)); break;
                    case "--keep": keep = true; break;
                    case "--strict": strict = true; break;
                    case "--engine":
                        Console.Error.WriteLine("--engine was removed; RealDiff uses the Rust engine.");
                        return ExitCodes.BuildOrTestFailure;
                    case "-h":
                    case "--help":
                        Usage();
                        return ExitCodes.NoUnexpected;
                    default:
                        if (args[i].StartsWith("--ci=", StringComparison.Ordinal))
                        {
                            ciProvider = args[i].Substring("--ci=".Length);
                        }
                        else if (args[i].StartsWith("--engine=", StringComparison.Ordinal))
                        {
                            Console.Error.WriteLine("--engine was removed; RealDiff uses the Rust engine.");
                            return ExitCodes.BuildOrTestFailure;
                        }
                        else
                        {
                            positional.Add(args[i]);
                        }

                        break;
                }
            }

            string? repo = positional.FirstOrDefault();
            if (warmOnly)
            {
                baseRef = targetRef;
                prRef = targetRef;
            }

            if ((warmOnly && (repo is null || targetRef is null || cacheDirectory is null))
                || (!warmOnly && ciProvider is null && (repo is null || baseRef is null || prRef is null)))
            {
                Usage();
                return ExitCodes.BuildOrTestFailure;
            }

            string workDirectory = work ?? Path.Combine(
                Path.GetTempPath(), "realdiff", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            workDirectory = Path.GetFullPath(workDirectory);
            string findingsPath = findings ?? Path.Combine(workDirectory, "findings.json");
            Pipeline? pipeline = null;

            try
            {
                string resolvedRepository = RefResolution.ResolveRepository(repo, ciProvider);
                LoadedRepositoryConfig repositoryConfig = RepositoryConfigLoader.Load(resolvedRepository);
                RepositoryConfigLoader.ApplyEnvironment(repositoryConfig);
                string? configuredBaseline = baseline is null
                    ? RepositoryConfigLoader.MaterializeBaseline(
                        repositoryConfig,
                        Path.Combine(workDirectory, "config-baseline.yml"))
                    : null;
                string? baselinePath = noBaseline
                    ? null
                    : Path.GetFullPath(baseline
                        ?? configuredBaseline
                        ?? Path.Combine(resolvedRepository, ".realdiff", "baseline.yml"));
                if (baseline is null && configuredBaseline is null && baselinePath is not null && !File.Exists(baselinePath))
                {
                    baselinePath = null;
                }

                if (baselinePath is not null)
                {
                    EngineDispatch.ValidateBaseline(baselinePath);
                }

                pipeline = new Pipeline(
                    resolvedRepository,
                    baseRef,
                    prRef,
                    ciProvider,
                    workDirectory,
                    findingsPath,
                    baselinePath,
                    keep,
                    cacheDirectory,
                    cacheRetention,
                    traceRetention,
                    warmOnly,
                    strict);
                return pipeline.Run();
            }
            catch (CliException ex)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("FAILED: " + ex.Message);
                ResolvedRefs? refs = pipeline?.ResolvedRefs;
                EngineDispatch.WriteInvalidFindings(
                    findingsPath,
                    ex.ExitCode == ExitCodes.RunInvalid ? "refused" : "failed",
                    ex.ExitCode,
                    ex.Message,
                    refs?.BaseSha,
                    refs?.PrSha,
                    refs?.MergeBaseSha);
                return ex.ExitCode;
            }
            catch (DiffInputException ex)
            {
                string reason = ex.GetType().Name + ": " + ex.Message;
                Console.Error.WriteLine();
                Console.Error.WriteLine("REFUSED: " + reason);
                ResolvedRefs? refs = pipeline?.ResolvedRefs;
                EngineDispatch.WriteInvalidFindings(
                    findingsPath,
                    "refused",
                    ExitCodes.RunInvalid,
                    reason,
                    refs?.BaseSha,
                    refs?.PrSha,
                    refs?.MergeBaseSha);
                return ExitCodes.RunInvalid;
            }
            catch (Exception ex)
            {
                string reason = ex.GetType().Name + ": " + ex.Message;
                Console.Error.WriteLine();
                Console.Error.WriteLine("FAILED: " + reason);
                ResolvedRefs? refs = pipeline?.ResolvedRefs;
                EngineDispatch.WriteInvalidFindings(
                    findingsPath,
                    "failed",
                    ExitCodes.BuildOrTestFailure,
                    reason,
                    refs?.BaseSha,
                    refs?.PrSha,
                    refs?.MergeBaseSha);
                return ExitCodes.BuildOrTestFailure;
            }
        }

        private static string Next(string[] args, ref int i)
        {
            if (i + 1 >= args.Length)
            {
                throw new CliException("Missing value for " + args[i]);
            }

            return args[++i];
        }

        private static TimeSpan ParseDuration(string value)
        {
            if (value.Length < 2
                || !double.TryParse(value.Substring(0, value.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double amount)
                || amount <= 0)
            {
                throw new CliException("Retention duration must be a positive value such as 12h or 7d.");
            }

            return char.ToLowerInvariant(value[value.Length - 1]) switch
            {
                'h' => TimeSpan.FromHours(amount),
                'd' => TimeSpan.FromDays(amount),
                _ => throw new CliException("Retention duration must end in h (hours) or d (days)."),
            };
        }

        private static void Usage()
        {
            Console.WriteLine("usage: realdiff <repo> --base <ref> --pr <ref> [--work <dir>] [--findings <file>] [--baseline <file>|--no-baseline] [--strict] [--cache-dir <dir>] [--cache-retention <12h|7d>] [--keep-traces <12h|7d>] [--keep]");
            Console.WriteLine("       realdiff warm <repo> --target <ref> --cache-dir <dir> [--cache-retention <12h|7d>] [--work <dir>] [--keep]");
            Console.WriteLine("       realdiff detect <repo>");
            Console.WriteLine("       realdiff [<repo>] --ci=azuredevops [--work <dir>] [--findings <file>] [--keep]");
            Console.WriteLine("       realdiff [<repo>] --ci=github [--work <dir>] [--findings <file>] [--keep]");
            Console.WriteLine("       realdiff post --provider=<azuredevops|github> --findings <file> [--gate warn-only|fail-on-findings]");
            Console.WriteLine("       realdiff baseline write --findings <file> [--repo <dir>] [--output <file>] [--expires 30d|--no-expiry]");
            Console.WriteLine("       realdiff baseline apply --findings <file> --baseline <file>");
            Console.WriteLine();
            Console.WriteLine("  exit 0  analyzed, no unexpected divergences");
            Console.WriteLine("  exit 1  analyzed, unexpected divergences found");
            Console.WriteLine("  exit 3  could not be trusted (coverage, volume, or call-tree refusal)");
            Console.WriteLine("  exit 4  RealDiff could not instrument this repository");
            Console.WriteLine("  exit 5  this repository does not build in this environment, before instrumentation");
        }
    }

    internal sealed class Pipeline
    {
        private readonly string _repo;
        private readonly string? _baseRef;
        private readonly string? _prRef;
        private readonly string? _ciProvider;
        private readonly string _work;
        private readonly string _findings;
        private readonly string? _baseline;
        private readonly bool _keep;
        private readonly TraceCacheSession _cache;
        private readonly bool _warmOnly;
        private readonly TimeSpan? _traceRetention;
        private readonly bool _strict;
        private readonly PipelineTimings _timings = new PipelineTimings();

        internal ResolvedRefs? ResolvedRefs { get; private set; }

        internal Pipeline(
            string repo,
            string? baseRef,
            string? prRef,
            string? ciProvider,
            string work,
            string findings,
            string? baseline,
            bool keep,
            string? cacheDirectory,
            TimeSpan cacheRetention,
            TimeSpan? traceRetention,
            bool warmOnly,
            bool strict)
        {
            _repo = repo;
            _baseRef = baseRef;
            _prRef = prRef;
            _ciProvider = ciProvider;
            _work = work;
            _findings = findings;
            _baseline = baseline;
            _keep = keep;
            _cache = new TraceCacheSession(
                cacheDirectory is null ? null : new LocalDirectoryTraceCacheStore(cacheDirectory, cacheRetention),
                work,
                _timings);
            _traceRetention = traceRetention;
            _warmOnly = warmOnly;
            _strict = strict;
        }

        internal int Run()
        {
            SweepExpiredTraces(Path.GetDirectoryName(_work));
            Directory.CreateDirectory(_work);
            Console.WriteLine("realdiff");
            Console.WriteLine("  repo : " + _repo);
            Console.WriteLine("  work : " + _work);
            Console.WriteLine("  mode : " + (_warmOnly ? "warm base trace cache" : "analyze PR"));
            Console.WriteLine("  engine: rust");

            if (!Directory.Exists(Path.Combine(_repo, ".git")) && !File.Exists(Path.Combine(_repo, ".git")))
            {
                throw new CliException(_repo + " is not a git repository.");
            }

            ResolvedRefs refs = RefResolution.Resolve(_repo, _baseRef, _prRef, _ciProvider);
            ResolvedRefs = refs;
            Console.WriteLine("  base       : " + refs.BaseLabel + " -> " + refs.BaseSha);
            Console.WriteLine("  pr         : " + refs.PrLabel + " -> " + refs.PrSha);
            Console.WriteLine("  merge base : " + refs.MergeBaseSha);
            Console.WriteLine("  PR commits : " + refs.PrCommitCount);
            Console.WriteLine("  changed from merge base: " + refs.ChangedFiles.Count);

            string baseTree = Path.Combine(_work, "base");
            string prTree = Path.Combine(_work, "pr");

            try
            {
                Console.WriteLine();
                Console.WriteLine("=== 1. worktrees ===");
                Shell.Git(_repo, "worktree", "add", "--detach", baseTree, refs.BaseSha);
                if (!_warmOnly)
                {
                    Shell.Git(_repo, "worktree", "add", "--detach", prTree, refs.PrSha);
                }

                LanguageDetection baseDetection = LanguageDetector.Detect(baseTree);
                LanguageDetection prDetection = _warmOnly ? baseDetection : LanguageDetector.Detect(prTree);
                if (!_warmOnly)
                {
                    AssertLanguageSymmetry(baseDetection, prDetection);
                }
                ApplyDetectionEnvironment(baseDetection);
                Console.WriteLine("  language   : " + baseDetection.Language.ToString().ToLowerInvariant());
                Console.WriteLine("  entry point: " + baseDetection.Evidence);
                Console.WriteLine("  workdir    : " + baseDetection.Workdir);
                Console.WriteLine("  build      : " + baseDetection.BuildCommand);
                Console.WriteLine("  test       : " + baseDetection.TestCommand);

                if (baseDetection.Language != RepositoryLanguage.DotNet)
                {
                    int removed = CrossLanguageExecution.StripBuildOutput(baseTree)
                        + (_warmOnly ? 0 : CrossLanguageExecution.StripBuildOutput(prTree));
                    Console.WriteLine("  stale target/node_modules/dist removed: " + removed);
                    var execution = new CrossLanguageExecution(_work, _cache, _timings);
                    if (_warmOnly)
                    {
                        execution.Warm(baseDetection, baseTree, refs.BaseSha);
                        _cache.Print();
                        return ExitCodes.NoUnexpected;
                    }

                    CrossLanguageRunSet runs = execution.Run(
                        baseDetection,
                        prDetection,
                        baseTree,
                        prTree,
                        refs.BaseSha);
                    _cache.Print();
                    return Analyze(refs, runs.BaseRoot, prTree, runs.Base1, runs.Base2, runs.Base3, runs.Pr);
                }

                Console.WriteLine("  stale bin/obj removed: " + (StripBuildOutput(baseTree) + (_warmOnly ? 0 : StripBuildOutput(prTree))));

                Console.WriteLine();
                Console.WriteLine("=== 2. scan ===");
                RepoScanResult scan = RepoScan.Scan(baseDetection.WorkDirectory);
                ReportScan(scan);

                Console.WriteLine();
                Console.WriteLine("=== 3. repo builds unmodified ===");
                var buildStopwatch = Stopwatch.StartNew();
                BuildUnmodified("base", baseDetection);
                if (!_warmOnly)
                {
                    BuildUnmodified("pr", prDetection);
                    Console.WriteLine("  both worktrees build without instrumentation");
                }
                buildStopwatch.Stop();
                _timings.BuildMilliseconds += buildStopwatch.ElapsedMilliseconds;

                Console.WriteLine();
                Console.WriteLine("=== 4. resolve xunit versions and TFMs ===");
                List<TestProject> selectedProjects = SelectTestProjects(scan.XunitProjects, baseDetection, baseTree);
                var baseProjects = selectedProjects.Select(p => Assets.Read(p.Path)).ToList();
                var prProjects = _warmOnly ? new List<ResolvedTestProject>() : baseProjects
                    .Select(p => Path.Combine(prTree, Path.GetRelativePath(baseTree, p.Path)))
                    .Where(File.Exists)
                    .Select(Assets.Read)
                    .ToList();

                ReportResolved(baseProjects);
                if (!_warmOnly)
                {
                    AssertSymmetry(baseProjects, prProjects);
                }

                string kit = InjectionKit.Build(_work);
                string[] scopePrefixes = baseDetection.IncludeNamespaces;
                string scope = string.Join(";", scopePrefixes);
                Console.WriteLine("  tracer namespace scope: " + (scope.Length == 0 ? "<empty>" : scope));
                if (scope.Length == 0)
                {
                    throw new CliException("Could not derive any namespace scope from the repository's project names.");
                }

                string scopeConfig = ScopeConfig(scope);
                string tracerVersion = TracerFingerprint.ForDirectory(
                    AppContext.BaseDirectory,
                    path =>
                    {
                        string name = Path.GetFileName(path);
                        return name is "realdiff.dll" or "realdiff-weaver.dll" or "RealDiff.Contracts.dll"
                            or "RealDiff.Tracer.dll" or "RealDiff.Tracer.Xunit.dll" or "Mono.Cecil.dll";
                    });
                var cacheKey = new TraceCacheKey(refs.BaseSha, "dotnet", tracerVersion, scopeConfig);
                bool cacheHit = _cache.TryRestore(cacheKey, out TraceCacheEntry? cacheEntry);
                if (_warmOnly)
                {
                    if (!cacheHit)
                    {
                        WarmDotNet(cacheKey, baseTree, baseProjects, scopePrefixes, kit, scope, baseDetection);
                    }

                    _cache.Print();
                    return ExitCodes.NoUnexpected;
                }

                var baseTraceStopwatch = new Stopwatch();

                Console.WriteLine();
                Console.WriteLine("=== 5. trace adapters (one per test project, per resolved xunit version) ===");
                if (!cacheHit)
                {
                    baseTraceStopwatch.Start();
                    buildStopwatch.Restart();
                    AdapterBuilder.BuildAll(Path.Combine(_work, "base-adapters"), kit, baseProjects);
                    buildStopwatch.Stop();
                    _timings.BuildMilliseconds += buildStopwatch.ElapsedMilliseconds;
                    baseTraceStopwatch.Stop();
                }

                buildStopwatch.Restart();
                AdapterBuilder.BuildAll(Path.Combine(_work, "pr-adapters"), kit, prProjects);
                buildStopwatch.Stop();
                _timings.BuildMilliseconds += buildStopwatch.ElapsedMilliseconds;
                foreach (ResolvedTestProject project in baseProjects)
                {
                    Console.WriteLine("  " + project.Name + " -> " + (project.UsesExistingTracerXunit
                        ? "existing RealDiff.Tracer.Xunit"
                        : project.XunitPackage + " " + project.XunitVersion) + " / " + project.TraceTfm);
                }

                Console.WriteLine();
                Console.WriteLine("=== 6. instrumented build ===");
                if (!cacheHit)
                {
                    baseTraceStopwatch.Start();
                    buildStopwatch.Restart();
                    BuildInstrumented("base", baseTree, kit, baseProjects);
                    buildStopwatch.Stop();
                    _timings.BuildMilliseconds += buildStopwatch.ElapsedMilliseconds;
                    baseTraceStopwatch.Stop();
                }

                buildStopwatch.Restart();
                BuildInstrumented("pr", prTree, kit, prProjects);
                buildStopwatch.Stop();
                _timings.BuildMilliseconds += buildStopwatch.ElapsedMilliseconds;

                Console.WriteLine();
                Console.WriteLine("=== 6b. weave project assemblies ===");
                if (!cacheHit)
                {
                    baseTraceStopwatch.Start();
                    var weaveStopwatch = Stopwatch.StartNew();
                    WeaveOutputs("base", baseProjects, scopePrefixes);
                    weaveStopwatch.Stop();
                    _timings.WeaveMilliseconds += weaveStopwatch.ElapsedMilliseconds;
                    baseTraceStopwatch.Stop();
                }

                var prWeaveStopwatch = Stopwatch.StartNew();
                WeaveOutputs("pr", prProjects, scopePrefixes);
                prWeaveStopwatch.Stop();
                _timings.WeaveMilliseconds += prWeaveStopwatch.ElapsedMilliseconds;

                Console.WriteLine();
                Console.WriteLine("=== 7. test runs ===");
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
                    AssertTestIdsPresent(base1);
                }
                else
                {
                    baseTraceStopwatch.Start();
                    var runStopwatch = Stopwatch.StartNew();
                    base1 = RunTests("base_run1", baseDetection, baseProjects, scope);
                    base2 = RunTests("base_run2", baseDetection, baseProjects, scope);
                    base3 = RunTests("base_run3", baseDetection, baseProjects, scope);
                    runStopwatch.Stop();
                    _timings.InstrumentedRunMilliseconds += runStopwatch.ElapsedMilliseconds;
                    baseRoot = baseTree;
                    baseTraceStopwatch.Stop();
                    _cache.Store(cacheKey, baseRoot, baseTraceStopwatch.ElapsedMilliseconds);
                }

                var prRunStopwatch = Stopwatch.StartNew();
                string pr = RunTests("pr_run", prDetection, prProjects, scope);
                prRunStopwatch.Stop();
                _timings.InstrumentedRunMilliseconds += prRunStopwatch.ElapsedMilliseconds;

                AssertTestIdsPresent(base1);
                _cache.Print();
                return Analyze(refs, baseRoot, prTree, base1, base2, base3, pr);
            }
            finally
            {
                ApplyTraceRetention();
                if (_keep)
                {
                    Console.WriteLine();
                    Console.WriteLine("worktrees kept at " + _work);
                }
                else
                {
                    Cleanup(baseTree);
                    Cleanup(prTree);
                }
            }
        }

        private void WarmDotNet(
            TraceCacheKey cacheKey,
            string baseTree,
            List<ResolvedTestProject> baseProjects,
            IEnumerable<string> namespacePrefixes,
            string kit,
            string scope,
            LanguageDetection detection)
        {
            var stopwatch = Stopwatch.StartNew();
            AdapterBuilder.BuildAll(Path.Combine(_work, "base-adapters"), kit, baseProjects);
            BuildInstrumented("base", baseTree, kit, baseProjects);
            WeaveOutputs("base", baseProjects, namespacePrefixes);
            string base1 = RunTests("base_run1", detection, baseProjects, scope);
            RunTests("base_run2", detection, baseProjects, scope);
            RunTests("base_run3", detection, baseProjects, scope);
            AssertTestIdsPresent(base1);
            stopwatch.Stop();
            _cache.Store(cacheKey, baseTree, stopwatch.ElapsedMilliseconds);
        }

        internal static string ScopeConfig(string scope) => scope + "\nexclude="
            + (Environment.GetEnvironmentVariable("REALDIFF_EXCLUDE_NAMESPACES") ?? string.Empty)
            + "\nredactNames=" + (Environment.GetEnvironmentVariable("REALDIFF_REDACT_NAMES") ?? string.Empty)
            + "\nredactTypes=" + (Environment.GetEnvironmentVariable("REALDIFF_REDACT_TYPES") ?? string.Empty)
            + "\nredactPaths=" + (Environment.GetEnvironmentVariable("REALDIFF_REDACT_PATHS") ?? string.Empty);

        private void ApplyTraceRetention()
        {
            string[] runNames = { "base_run1", "base_run2", "base_run3", "pr_run" };
            if (_traceRetention is null)
            {
                foreach (string run in runNames)
                {
                    string directory = Path.Combine(_work, run);
                    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                }

                Console.WriteLine("  trace retention: deleted after analysis");
                return;
            }

            DateTimeOffset expires = DateTimeOffset.UtcNow + _traceRetention.Value;
            File.WriteAllText(
                Path.Combine(_work, "trace-retention.json"),
                "{\"schema\":\"realdiff.trace-retention/1\",\"expiresUtc\":\""
                + expires.ToString("O", CultureInfo.InvariantCulture) + "\"}" + Environment.NewLine);
            Console.WriteLine("  trace retention: kept until " + expires.ToString("O", CultureInfo.InvariantCulture));
        }

        private static void SweepExpiredTraces(string? root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return;
            }

            var markers = new List<string>();
            string rootMarker = Path.Combine(root, "trace-retention.json");
            if (File.Exists(rootMarker)) markers.Add(rootMarker);
            try
            {
                markers.AddRange(Directory.EnumerateDirectories(root)
                    .Select(directory => Path.Combine(directory, "trace-retention.json"))
                    .Where(File.Exists));
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            foreach (string marker in markers)
            {
                try
                {
                    using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(marker));
                    string? expiry = document.RootElement.GetProperty("expiresUtc").GetString();
                    if (!DateTimeOffset.TryParse(expiry, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset expires)
                        || expires > DateTimeOffset.UtcNow)
                    {
                        continue;
                    }

                    string directory = Path.GetDirectoryName(marker)!;
                    foreach (string run in new[] { "base_run1", "base_run2", "base_run3", "pr_run" })
                    {
                        string runDirectory = Path.Combine(directory, run);
                        if (Directory.Exists(runDirectory)) Directory.Delete(runDirectory, recursive: true);
                    }

                    File.Delete(marker);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (System.Text.Json.JsonException)
                {
                }
            }
        }

        private static void AssertLanguageSymmetry(LanguageDetection baseDetection, LanguageDetection prDetection)
        {
            if (baseDetection.Language != prDetection.Language)
            {
                throw new CliException(
                    "Base language is " + baseDetection.Language.ToString().ToLowerInvariant()
                    + " but PR language is " + prDetection.Language.ToString().ToLowerInvariant()
                    + ". Cross-language comparison is not supported.",
                    ExitCodes.RunInvalid);
            }

            if (!string.Equals(baseDetection.Evidence, prDetection.Evidence, StringComparison.Ordinal))
            {
                throw new CliException(
                    "Base build entry point is " + baseDetection.Evidence + " but PR build entry point is "
                    + prDetection.Evidence + ". The same relative entry point is required on both sides.",
                    ExitCodes.RunInvalid);
            }

            foreach ((string Name, string Base, string Pr) value in new[]
            {
                ("workdir", baseDetection.Workdir, prDetection.Workdir),
                ("build command", baseDetection.BuildCommand, prDetection.BuildCommand),
                ("test command", baseDetection.TestCommand, prDetection.TestCommand),
                ("test projects", string.Join("\n", baseDetection.TestProjects), string.Join("\n", prDetection.TestProjects)),
                ("include namespaces", string.Join("\n", baseDetection.IncludeNamespaces), string.Join("\n", prDetection.IncludeNamespaces)),
                ("exclude namespaces", string.Join("\n", baseDetection.ExcludeNamespaces), string.Join("\n", prDetection.ExcludeNamespaces)),
            })
            {
                if (!string.Equals(value.Base, value.Pr, StringComparison.Ordinal))
                {
                    throw new CliException(
                        "Base and PR resolved different " + value.Name + " values. The same build, test, and instrumentation configuration must run on both sides."
                        + Environment.NewLine + "    base: " + value.Base
                        + Environment.NewLine + "    pr  : " + value.Pr,
                        ExitCodes.RunInvalid);
                }
            }
        }

        private static void ApplyDetectionEnvironment(LanguageDetection detection)
        {
            if (detection.ExcludeNamespaces.Length > 0)
            {
                Environment.SetEnvironmentVariable(
                    "REALDIFF_EXCLUDE_NAMESPACES",
                    string.Join(";", detection.ExcludeNamespaces));
            }
        }

        private static List<TestProject> SelectTestProjects(
            IEnumerable<TestProject> projects,
            LanguageDetection detection,
            string repositoryRoot)
        {
            if (detection.TestProjects.Length == 0)
            {
                return projects.ToList();
            }

            List<TestProject> selected = projects.Where(project => detection.TestProjects.Any(pattern =>
            {
                string repositoryRelative = Path.GetRelativePath(repositoryRoot, project.Path).Replace('\\', '/');
                string workdirRelative = Path.GetRelativePath(detection.WorkDirectory, project.Path).Replace('\\', '/');
                return GlobMatches(pattern, repositoryRelative) || GlobMatches(pattern, workdirRelative);
            })).ToList();
            if (selected.Count == 0)
            {
                throw new CliException(
                    "Configured test_projects matched no xUnit test projects: " + string.Join(", ", detection.TestProjects),
                    ExitCodes.RunInvalid);
            }
            return selected;
        }

        private static bool GlobMatches(string pattern, string value)
        {
            string expression = "^" + Regex.Escape(pattern.Replace('\\', '/'))
                .Replace("\\*\\*", ".*", StringComparison.Ordinal)
                .Replace("\\*", "[^/]*", StringComparison.Ordinal)
                .Replace("\\?", "[^/]", StringComparison.Ordinal) + "$";
            return Regex.IsMatch(value, expression, RegexOptions.CultureInvariant);
        }

        private int Analyze(
            ResolvedRefs refs,
            string baseTree,
            string prTree,
            string base1,
            string base2,
            string base3,
            string pr)
        {
            Console.WriteLine();
            Console.WriteLine("=== 8. changed files ===");
            string changedList = WriteChangedFiles(refs);

            Console.WriteLine();
            Console.WriteLine("=== 9. engine part 1 ===");
            var diffStopwatch = Stopwatch.StartNew();
            string divergenceSet = Path.Combine(_work, "divergence-set.json");
            var diffOptions = new DiffOptions
            {
                Base1 = base1,
                Base2 = base2,
                Base3 = base3,
                Pr = pr,
                BaseRoot = baseTree,
                PrRoot = prTree,
                ChangedFiles = changedList,
                Output = divergenceSet,
            };
            int diffExit = EngineDispatch.RunDiff(diffOptions);
            diffStopwatch.Stop();
            _timings.DiffMilliseconds += diffStopwatch.ElapsedMilliseconds;
            if (diffExit != 0)
            {
                string reason = diffOptions.RefusalReason ?? "The comparison was refused before a DivergenceSet was produced.";
                EngineDispatch.WriteInvalidFindings(
                    _findings,
                    "refused",
                    ExitCodes.RunInvalid,
                    reason,
                    refs.BaseSha,
                    refs.PrSha,
                    refs.MergeBaseSha);
                Console.WriteLine();
                Console.WriteLine("RESULT: COULD NOT ANALYZE. The comparison was refused before any finding was produced;");
                Console.WriteLine("        this is not a statement that the PR is clean.");
                return ExitCodes.RunInvalid;
            }

            Console.WriteLine();
            Console.WriteLine("=== 10. engine part 2 ===");
            var frontierStopwatch = Stopwatch.StartNew();
            string report = Path.Combine(_work, "frontier-report.json");
            var frontierOptions = new FrontierOptions
            {
                Input = divergenceSet,
                ChangedFiles = changedList,
                Output = report,
            };
            int frontierExit = EngineDispatch.RunFrontier(frontierOptions);
            frontierStopwatch.Stop();
            _timings.FrontierMilliseconds += frontierStopwatch.ElapsedMilliseconds;
            if (frontierExit != 0)
            {
                string reason = frontierOptions.RefusalReason ?? "Frontier detection was refused before a report was produced.";
                EngineDispatch.WriteInvalidFindings(
                    _findings,
                    "refused",
                    ExitCodes.RunInvalid,
                    reason,
                    refs.BaseSha,
                    refs.PrSha,
                    refs.MergeBaseSha);
                Console.WriteLine();
                Console.WriteLine("RESULT: COULD NOT ANALYZE. Frontier detection was refused; no verdict was produced.");
                return ExitCodes.RunInvalid;
            }

            int exitCode = Summarize(report);
            EngineDispatch.WriteFindings(
                divergenceSet,
                report,
                _findings,
                exitCode,
                refs.BaseSha,
                refs.PrSha,
                refs.MergeBaseSha,
                _cache.Report.Status,
                _cache.Report.Key,
                _cache.Report.Backend,
                _cache.Report.SavedWallClockMilliseconds,
                _timings.BuildMilliseconds,
                _timings.WeaveMilliseconds,
                _timings.InstrumentedRunMilliseconds,
                _timings.CacheRestoreMilliseconds,
                _timings.CacheStoreMilliseconds,
                _timings.DiffMilliseconds,
                _timings.FrontierMilliseconds,
                _strict);
            int policyExitCode = exitCode;
            if (_baseline is not null)
            {
                Console.WriteLine();
                Console.WriteLine("=== baseline policy ===");
                BaselineResult baseline = EngineDispatch.ApplyBaseline(_findings, _baseline);
                BaselineCommand.Report(baseline, _baseline);
                policyExitCode = baseline.ExitCode;
            }

            _timings.Report();
            return policyExitCode;
        }

        private static void ReportScan(RepoScanResult scan)
        {
            Console.WriteLine("  xunit test projects : " + scan.XunitProjects.Count);
            foreach (TestProject project in scan.XunitProjects)
            {
                Console.WriteLine("    " + Path.GetFileName(project.Path));
            }

            foreach (TestProject project in scan.OtherFrameworks)
            {
                Console.WriteLine("    SKIPPED " + Path.GetFileName(project.Path) + "  [" + project.Framework + "]");
            }

            if (scan.XunitProjects.Count == 0)
            {
                string detail = scan.OtherFrameworks.Count == 0
                    ? "No test projects were found at all."
                    : "Found test projects using: " + string.Join(", ", scan.OtherFrameworks.Select(p => p.Framework).Distinct()) + ".";

                throw new CliException(
                    "No xunit test projects found. " + detail + Environment.NewLine
                    + "    The tracer stamps events with a TestId through an xunit BeforeAfterTestAttribute, so a "
                    + "non-xunit suite would run and produce a trace with no test identity - indistinguishable from "
                    + "a clean result. Refusing before either worktree is built.",
                    ExitCodes.RunInvalid);
            }

            if (scan.DebugTypeOverrides.Count > 0)
            {
                // Reported, not refused. The build passes -p:DebugType=portable as a global property, which
                // an MSBuild project cannot override, so these settings do not survive. The real check is
                // downstream and measured rather than guessed: the engine refuses any assembly whose
                // members failed to resolve source lines, whatever the reason.
                Console.WriteLine("  NOTE: DebugType is set away from portable in the repository:");
                foreach (string over in scan.DebugTypeOverrides.Distinct())
                {
                    Console.WriteLine("    " + over);
                }

                Console.WriteLine("    Overridden by -p:DebugType=portable; source resolution is verified from the manifest.");
            }
        }

        private static void ReportResolved(List<ResolvedTestProject> projects)
        {
            foreach (ResolvedTestProject project in projects)
            {
                Console.WriteLine("  " + project.Name);
                Console.WriteLine("    xunit     : " + (project.XunitVersion.Length == 0 ? "<unresolved>" : project.XunitPackage + " " + project.XunitVersion));
                Console.WriteLine("    tfms      : " + string.Join(", ", project.AllTfms));
                Console.WriteLine("    tracing   : " + (project.TraceTfm.Length == 0 ? "<none>" : project.TraceTfm) + "  (highest traceable)");
                foreach ((string tfm, string reason) in project.RejectedTfms)
                {
                    Console.WriteLine("    rejected  : " + tfm + " - " + reason);
                }
            }

            var untraceable = projects.Where(p => p.TraceTfm.Length == 0).ToList();
            if (untraceable.Count > 0)
            {
                throw new CliException(
                    "These test projects have no traceable target framework:" + Environment.NewLine
                    + string.Join(Environment.NewLine, untraceable.Select(p => "      " + p.Name + " targets " + string.Join(", ", p.AllTfms)))
                    + Environment.NewLine
                    + "    The tracer requires net5.0 or later. Refusing rather than producing an empty trace.");
            }

            var noVersion = projects.Where(p => p.XunitVersion.Length == 0).ToList();
            if (noVersion.Count > 0)
            {
                throw new CliException(
                    "Could not resolve an xunit version for: " + string.Join(", ", noVersion.Select(p => p.Name)));
            }
        }

        /// <summary>
        /// Base and PR must trace the same target framework. A difference changes which members are
        /// instrumented, which surfaces as manifest gaps rather than as findings.
        /// </summary>
        private static void AssertSymmetry(List<ResolvedTestProject> baseProjects, List<ResolvedTestProject> prProjects)
        {
            if (baseProjects.Count != prProjects.Count)
            {
                throw new CliException(
                    "Base has " + baseProjects.Count + " xunit test project(s), PR has " + prProjects.Count
                    + ". An asymmetric project set produces coverage gaps, not findings.");
            }

            foreach (ResolvedTestProject baseProject in baseProjects)
            {
                ResolvedTestProject? prProject = prProjects.FirstOrDefault(p => p.Name == baseProject.Name);
                if (prProject is null)
                {
                    throw new CliException("Test project " + baseProject.Name + " exists in base but not in PR.");
                }

                if (baseProject.TraceTfm != prProject.TraceTfm)
                {
                    throw new CliException(
                        baseProject.Name + " would be traced on " + baseProject.TraceTfm + " in base but "
                        + prProject.TraceTfm + " in PR. Different frameworks instrument different members, "
                        + "so the comparison would report coverage differences as behavior differences.");
                }

                    if (baseProject.UsesExistingTracerXunit != prProject.UsesExistingTracerXunit)
                    {
                        throw new CliException(
                        baseProject.Name + " references RealDiff.Tracer.Xunit on only one side. "
                        + "Different test-correlation adapters make base/PR traces incomparable.");
                    }
            }

            Console.WriteLine("  base/PR trace framework symmetry: OK");
        }

        private static int StripBuildOutput(string tree)
        {
            int removed = 0;
            foreach (string directory in Directory.EnumerateDirectories(tree, "*", SearchOption.AllDirectories)
                .Where(d => Path.GetFileName(d) is "bin" or "obj")
                .OrderByDescending(d => d.Length)
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

        /// <summary>
        /// The repo must build before anything is injected, so a failure can be attributed. A break that
        /// appears only after injection is ours; a break in both is the repository's.
        /// </summary>
        private static void BuildUnmodified(string label, LanguageDetection detection)
        {
            ProcessResult result = detection.HasCustomBuild
                ? Shell.RunCommand(detection.BuildCommand, detection.WorkDirectory)
                : Shell.Run(
                    "dotnet",
                    new[]
                    {
                        "build", detection.EntryPoint, "-c", "Release", "--nologo", "-v", "quiet",
                        "-p:DebugType=portable",
                        "-p:EnableSourceControlManagerQueries=false",
                    },
                    detection.WorkDirectory);

            if (!result.Ok)
            {
                throw new CliException(
                    "This repository does not build in this environment, before any instrumentation." + Environment.NewLine
                    + "    Worktree: " + label + Environment.NewLine
                    + "    RealDiff has changed nothing at this point; the failure below is the repository's."
                    + Environment.NewLine + Shell.Tail(result.Output, 25),
                    ExitCodes.RepoDoesNotBuild);
            }
        }

        private void BuildInstrumented(string label, string tree, string kit, List<ResolvedTestProject> projects)
        {
            foreach (ResolvedTestProject project in projects)
            {
                var arguments = new List<string>
                {
                    "build", project.Path, "-c", "Release", "--nologo", "-v", "quiet",
                    "-p:DebugType=portable",
                    "-p:EnableSourceControlManagerQueries=false",
                };
                if (!project.UsesExistingTracerXunit)
                {
                    arguments.Add("-p:CustomAfterMicrosoftCommonTargets=" + Path.Combine(kit, "RealDiff.Inject.targets"));
                    arguments.Add("-p:RealDiffKitDir=" + kit + Path.DirectorySeparatorChar);
                    arguments.Add("-p:RealDiffAdapterPath=" + project.AdapterAssemblyPath);
                    arguments.Add("-p:RealDiffTraceTfm=" + project.TraceTfm);
                    arguments.Add("-p:RealDiffTestProjects=" + project.Path);
                }

                ProcessResult result = Shell.Run(
                    "dotnet",
                    arguments,
                    tree);

                if (!result.Ok)
                {
                    throw new CliException(
                        "The " + label + " worktree built clean unmodified but failed with instrumentation injected into "
                        + project.Name + ". This failure is RealDiff's, not the repository's."
                        + Environment.NewLine + Shell.Tail(result.Output, 25));
                }

                StageRuntimeDependencies(project, kit);
            }

            Console.WriteLine("  " + label + " built with instrumentation");
        }

        private static void StageRuntimeDependencies(ResolvedTestProject project, string kit)
        {
            string output = Path.Combine(
                Path.GetDirectoryName(project.Path)!,
                "bin",
                "Release",
                project.TraceTfm);
            Directory.CreateDirectory(output);

            foreach (string assembly in new[] { "RealDiff.Contracts.dll", "RealDiff.Tracer.dll" })
            {
                File.Copy(Path.Combine(kit, assembly), Path.Combine(output, assembly), overwrite: true);
            }

            if (!project.UsesExistingTracerXunit)
            {
                File.Copy(
                    project.AdapterAssemblyPath,
                    Path.Combine(output, Path.GetFileName(project.AdapterAssemblyPath)),
                    overwrite: true);
            }

            string runtime = Path.Combine(kit, "runtime");
            if (Directory.Exists(runtime))
            {
                foreach (string dependency in Directory.GetFiles(runtime, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    File.Copy(dependency, Path.Combine(output, Path.GetFileName(dependency)), overwrite: true);
                }
            }
        }

        private static void WeaveOutputs(
            string label,
            IReadOnlyList<ResolvedTestProject> projects,
            IEnumerable<string> namespacePrefixes)
        {
            string weaver = Path.Combine(AppContext.BaseDirectory, "realdiff-weaver.dll");
            if (!File.Exists(weaver))
            {
                throw new CliException("Cecil weaver missing from CLI output: " + weaver);
            }

            string include = string.Join(",", namespacePrefixes);
            string? exclude = Environment.GetEnvironmentVariable("REALDIFF_EXCLUDE_NAMESPACES");
            int wovenAssemblies = 0;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ResolvedTestProject project in projects)
            {
                string output = Path.Combine(
                    Path.GetDirectoryName(project.Path)!,
                    "bin",
                    "Release",
                    project.TraceTfm);
                if (!Directory.Exists(output))
                {
                    throw new CliException("Instrumented build produced no output directory: " + output);
                }

                string testAssembly = Path.GetFileNameWithoutExtension(project.Path) + ".dll";
                foreach (string assembly in Directory.GetFiles(output, "*.dll", SearchOption.TopDirectoryOnly)
                    .Where(path => File.Exists(Path.ChangeExtension(path, ".pdb")))
                    .Where(path => !Path.GetFileName(path).StartsWith("RealDiff.", StringComparison.Ordinal))
                    .OrderBy(path => path, StringComparer.Ordinal))
                {
                    if (!visited.Add(Path.GetFullPath(assembly)))
                    {
                        continue;
                    }

                    var arguments = new List<string>
                    {
                        weaver,
                        "--assembly",
                        assembly,
                        "--include",
                        include,
                    };
                    if (!string.IsNullOrWhiteSpace(exclude))
                    {
                        arguments.Add("--exclude");
                        arguments.Add(exclude!);
                    }

                    if (string.Equals(Path.GetFileName(assembly), testAssembly, StringComparison.OrdinalIgnoreCase))
                    {
                        arguments.Add("--test-assembly");
                    }

                    ProcessResult result = Shell.Run("dotnet", arguments, output);
                    if (!result.Ok)
                    {
                        throw new CliException(
                            "Cecil weaving failed for " + assembly + "." + Environment.NewLine
                            + Shell.Tail(result.Output, 20));
                    }

                    string woven = assembly + ".woven";
                    if (!File.Exists(woven))
                    {
                        throw new CliException("Weaver reported success but produced no output for " + assembly + ".");
                    }

                    if (result.Output.Contains("discovered : 0", StringComparison.Ordinal))
                    {
                        File.Delete(woven);
                        continue;
                    }

                    File.Move(woven, assembly, overwrite: true);
                    wovenAssemblies++;
                }
            }

            if (wovenAssemblies == 0)
            {
                throw new CliException(
                    "Cecil found no in-scope project assembly in the " + label + " test outputs. "
                    + "Refusing before a zero-event run.",
                    ExitCodes.RunInvalid);
            }

            Console.WriteLine("  " + label + " woven project assemblies: " + wovenAssemblies);
        }

        private string RunTests(string label, LanguageDetection detection, List<ResolvedTestProject> projects, string scope)
        {
            string directory = Path.Combine(_work, label);
            Directory.CreateDirectory(directory);
            var testOutput = new List<string>();

            var environment = new Dictionary<string, string>
            {
                ["REALDIFF_TRACE"] = Path.Combine(directory, "run.ndjson"),
                ["REALDIFF_NAMESPACES"] = scope,
            };

            if (detection.HasCustomTest)
            {
                ProcessResult result = Shell.RunCommand(
                    detection.TestCommand,
                    detection.WorkDirectory,
                    environment);
                testOutput.Add("configured:" + Environment.NewLine + result.Output);
            }
            else
            {
                foreach (ResolvedTestProject project in projects)
                {
                    ProcessResult result = Shell.Run(
                        "dotnet",
                        new[] { "test", project.Path, "-c", "Release", "-f", project.TraceTfm, "--no-build", "--nologo" },
                        detection.WorkDirectory,
                        environment);
                    testOutput.Add(project.Name + ":" + Environment.NewLine + result.Output);

                    // A failing assertion is an observation, not a pipeline failure: the PR may have changed
                    // behavior a test asserts on. Only a host that never started is fatal.
                    if (!result.Ok && result.Output.Contains("MSB", StringComparison.Ordinal))
                    {
                        throw new CliException(
                            "Test host failed to start for " + project.Name + " in " + label + "."
                            + Environment.NewLine + Shell.Tail(result.Output, 20));
                    }
                }
            }

            var traces = Directory.GetFiles(directory, "run.*.ndjson")
                .Where(f => !f.Contains(".manifest.", StringComparison.Ordinal))
                .ToList();

            long bytes = traces.Sum(f => new FileInfo(f).Length);
            Console.WriteLine("  " + label.PadRight(10) + " traces=" + traces.Count + " bytes=" + bytes);

            // The tracer runs inside a test host whose exit code belongs to xunit, so it reports
            // run-invalidating conditions through a marker file rather than by failing the process.
            var markers = Directory.GetFiles(directory, "*.FAILED");
            if (markers.Length > 0)
            {
                throw new CliException(
                    "The tracer reported a run-invalidating condition during " + label + ":" + Environment.NewLine
                    + string.Join(Environment.NewLine, markers.SelectMany(File.ReadAllLines).Distinct().Select(l => "    " + l)),
                    ExitCodes.RunInvalid);
            }

            if (traces.Count == 0 || bytes == 0)
            {
                throw new CliException(
                    "NO EVENTS: " + label + " produced " + traces.Count + " trace file(s) totalling " + bytes
                    + " bytes. The tracer initialized but recorded nothing, so either no test executed, "
                    + "no member was instrumented, or the configured test command bypassed the injected test host. "
                    + "This is not a question of test identity - see the tracer "
                    + "log and the coverage manifest in " + directory + "." + Environment.NewLine
                    + "    Test host output:" + Environment.NewLine
                    + Shell.Tail(string.Join(Environment.NewLine, testOutput), 30),
                    ExitCodes.RunInvalid);
            }

            return directory;
        }

        /// <summary>
        /// Distinct from the no-events case on purpose. "Nothing ran" and "things ran but are unlabelled"
        /// live in different layers, and a guard that names the wrong one sends you to the wrong code.
        /// </summary>
        internal static void AssertTestIdsPresent(string runDirectory)
        {
            int withTestId = 0;
            int total = 0;

            foreach (string file in Directory.GetFiles(runDirectory, "run.*.ndjson")
                .Where(f => !f.Contains(".manifest.", StringComparison.Ordinal)))
            {
                foreach (string line in File.ReadLines(file))
                {
                    total++;
                    if (line.Contains("\"testId\":\"", StringComparison.Ordinal)
                        && !line.Contains("\"testId\":\"(no-test)\"", StringComparison.Ordinal))
                    {
                        withTestId++;
                    }

                    if (total >= 20000)
                    {
                        break;
                    }
                }
            }

            double share = total == 0 ? 0 : withTestId * 100.0 / total;
            Console.WriteLine("  events carrying a TestId: " + share.ToString("F1", CultureInfo.InvariantCulture) + "%  (of " + total + " events)");

            if (total > 0 && share < 50)
            {
                throw new CliException(
                    "UNLABELLED EVENTS: " + total + " events were produced but only "
                    + share.ToString("F1", CultureInfo.InvariantCulture) + "% carry a TestId. Instrumentation "
                    + "worked; test identity did not. The test-framework correlation adapter is not labeling "
                    + "enough events, so observations cannot be correlated across runs.",
                    ExitCodes.RunInvalid);
            }
        }

        private string WriteChangedFiles(ResolvedRefs refs)
        {
            string path = Path.Combine(_work, "changed-files.txt");
            File.WriteAllLines(path, refs.ChangedFiles);

            Console.WriteLine("  changed files: " + refs.ChangedFiles.Count);
            foreach (string file in refs.ChangedFiles.Take(15))
            {
                Console.WriteLine("    " + file);
            }

            return path;
        }

        private static int Summarize(string reportPath)
        {
            var report = System.Text.Json.JsonDocument.Parse(File.ReadAllText(reportPath));
            System.Text.Json.JsonElement counts = report.RootElement.GetProperty("counts");
            System.Text.Json.JsonElement coverage = report.RootElement
                .GetProperty("changedFileCoverage")
                .GetProperty("summary");
            int unexpected = counts.GetProperty("unexpected").GetInt32();
            int expected = counts.GetProperty("expected").GetInt32();
            int untested = counts.GetProperty("untested").GetInt32();
            int editedFiles = coverage.GetProperty("editedFiles").GetInt32();
            int exercisedFiles = coverage.GetProperty("exercisedEditedFiles").GetInt32();
            int tracedMembers = coverage.GetProperty("tracedMembers").GetInt32();
            int observedCallSites = coverage.GetProperty("observedCallSites").GetInt32();
            int totalCalls = coverage.GetProperty("totalCallCount").GetInt32();

            Console.WriteLine();
            Console.WriteLine("COVERAGE: " + exercisedFiles + " of " + editedFiles
                + " edited files were exercised by tests.");
            Console.WriteLine("          " + tracedMembers + " members, " + observedCallSites
                + " call sites, " + totalCalls + " total calls observed in representative base/PR runs.");
            if (unexpected == 0)
            {
                Console.WriteLine("RESULT: ANALYZED. No unexpected behavior changes across " + editedFiles
                    + " edited files (" + tracedMembers + " members, " + observedCallSites
                    + " call sites observed).");
                Console.WriteLine("        " + expected + " change(s) confined to edited files; " + untested + " untested.");
                return ExitCodes.NoUnexpected;
            }

            Console.WriteLine("RESULT: ANALYZED, " + unexpected + " unexpected behavior change(s) in files the PR did not edit.");
            return ExitCodes.UnexpectedFound;
        }

        private void Cleanup(string tree)
        {
            try
            {
                Shell.Run("git", new[] { "worktree", "remove", "--force", tree }, _repo);
            }
            catch (Exception)
            {
            }
        }
    }
}
