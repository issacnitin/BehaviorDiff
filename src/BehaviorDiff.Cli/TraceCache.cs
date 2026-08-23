using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BehaviorDiff.Cli
{
    internal sealed record TraceCacheKey(
        string TargetSha,
        string Language,
        string TracerVersion,
        string ScopeConfig)
    {
        internal string Id
        {
            get
            {
                string canonical = string.Join("\n", TargetSha, Language, TracerVersion, ScopeConfig);
                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
            }
        }
    }

    internal sealed record TraceCacheEntry(string BaseRoot, long TraceWallClockMilliseconds);

    internal sealed record TraceCacheReport(
        string Status,
        string Key,
        string Backend,
        long SavedWallClockMilliseconds);

    internal interface ITraceCacheStore
    {
        bool TryRestore(TraceCacheKey key, string destination, out TraceCacheEntry? entry);

        void Store(TraceCacheKey key, string source, TraceCacheEntry entry);
    }

    internal sealed class TraceCacheSession
    {
        private readonly ITraceCacheStore? _store;
        private readonly string _work;
        private readonly PipelineTimings _timings;

        internal TraceCacheSession(ITraceCacheStore? store, string work, PipelineTimings timings)
        {
            _store = store;
            _work = work;
            _timings = timings;
            Report = new TraceCacheReport(store is null ? "disabled" : "miss", string.Empty, BackendName, 0);
        }

        internal TraceCacheReport Report { get; private set; }

        private string BackendName => _store is null ? "none" : "local-directory";

        internal bool TryRestore(TraceCacheKey key, out TraceCacheEntry? entry)
        {
            entry = null;
            Report = new TraceCacheReport(_store is null ? "disabled" : "miss", key.Id, BackendName, 0);
            if (_store is null)
            {
                return false;
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                if (!_store.TryRestore(key, _work, out entry) || entry is null)
                {
                    return false;
                }

                Report = new TraceCacheReport("hit", key.Id, BackendName, entry.TraceWallClockMilliseconds);
                return true;
            }
            catch (Exception ex)
            {
                foreach (int run in new[] { 1, 2, 3 })
                {
                    string directory = RunPath(_work, run);
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }

                Console.WriteLine("  base trace cache warning: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
            finally
            {
                stopwatch.Stop();
                _timings.CacheRestoreMilliseconds += stopwatch.ElapsedMilliseconds;
            }
        }

        internal void Store(TraceCacheKey key, string baseRoot, long traceWallClockMilliseconds)
        {
            if (_store is null)
            {
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                _store.Store(key, _work, new TraceCacheEntry(baseRoot, traceWallClockMilliseconds));
            }
            catch (Exception ex)
            {
                Console.WriteLine("  base trace cache warning: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                stopwatch.Stop();
                _timings.CacheStoreMilliseconds += stopwatch.ElapsedMilliseconds;
            }
        }

        internal void Print()
        {
            Console.WriteLine("  base trace cache: " + Report.Status
                + " key=" + (Report.Key.Length > 12 ? Report.Key.Substring(0, 12) : Report.Key)
                + " backend=" + Report.Backend
                + " wall-clock-saved=" + (Report.SavedWallClockMilliseconds / 1000.0)
                    .ToString("F2", CultureInfo.InvariantCulture) + "s");
        }

        internal static string RunPath(string work, int number) => Path.Combine(work, "base_run" + number);
    }

    internal static class TracerFingerprint
    {
        internal static string ForDirectory(string directory, Func<string, bool>? include = null)
        {
            IEnumerable<string> files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(path => include is null || include(path))
                .OrderBy(path => Path.GetRelativePath(directory, path).Replace('\\', '/'), StringComparer.Ordinal);
            return HashFiles(directory, files);
        }

        internal static string ForFile(string file) => HashFiles(Path.GetDirectoryName(file)!, new[] { file });

        private static string HashFiles(string root, IEnumerable<string> files)
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (string file in files)
            {
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                hash.AppendData(Encoding.UTF8.GetBytes(relative));
                hash.AppendData(new byte[] { 0 });
                using FileStream stream = File.OpenRead(file);
                var buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                }

                hash.AppendData(new byte[] { 0 });
            }

            return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
    }

    internal sealed class LocalDirectoryTraceCacheStore : ITraceCacheStore
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        private readonly string _root;
        private readonly TimeSpan _retention;

        internal LocalDirectoryTraceCacheStore(string root, TimeSpan retention)
        {
            _root = Path.GetFullPath(root);
            _retention = retention;
            PruneExpired();
        }

        public bool TryRestore(TraceCacheKey key, string destination, out TraceCacheEntry? entry)
        {
            entry = null;
            string cacheDirectory = Path.Combine(_root, key.Id);
            string metadataPath = Path.Combine(cacheDirectory, "metadata.json");
            if (!File.Exists(metadataPath))
            {
                return false;
            }

            CacheMetadata? metadata = JsonSerializer.Deserialize<CacheMetadata>(File.ReadAllText(metadataPath), Json);
            if (metadata is null
                || metadata.Schema != "behaviordiff.trace-cache/1"
                || metadata.TargetSha != key.TargetSha
                || metadata.Language != key.Language
                || metadata.TracerVersion != key.TracerVersion
                || metadata.ScopeConfig != key.ScopeConfig
                || string.IsNullOrWhiteSpace(metadata.BaseRoot)
                || metadata.TraceWallClockMilliseconds < 0
                || !DateTimeOffset.TryParse(metadata.CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset created)
                || created + _retention <= DateTimeOffset.UtcNow)
            {
                Directory.Delete(cacheDirectory, recursive: true);
                return false;
            }

            foreach (string run in RunNames)
            {
                string source = Path.Combine(cacheDirectory, run);
                if (!IsCompleteRun(source))
                {
                    return false;
                }
            }

            Directory.CreateDirectory(destination);
            foreach (string run in RunNames)
            {
                CopyDirectory(Path.Combine(cacheDirectory, run), Path.Combine(destination, run));
            }

            entry = new TraceCacheEntry(metadata.BaseRoot, metadata.TraceWallClockMilliseconds);
            return true;
        }

        public void Store(TraceCacheKey key, string source, TraceCacheEntry entry)
        {
            foreach (string run in RunNames)
            {
                if (!IsCompleteRun(Path.Combine(source, run)))
                {
                    throw new InvalidDataException("Cannot cache incomplete trace run " + run + ".");
                }
            }

            Directory.CreateDirectory(_root);
            string finalDirectory = Path.Combine(_root, key.Id);
            string stagingDirectory = finalDirectory + ".tmp-" + Guid.NewGuid().ToString("N");
            string lockPath = Path.Combine(_root, key.Id + ".lock");
            using FileStream cacheLock = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                Directory.CreateDirectory(stagingDirectory);
                foreach (string run in RunNames)
                {
                    CopyDirectory(Path.Combine(source, run), Path.Combine(stagingDirectory, run));
                }

                var metadata = new CacheMetadata
                {
                    Schema = "behaviordiff.trace-cache/1",
                    TargetSha = key.TargetSha,
                    Language = key.Language,
                    TracerVersion = key.TracerVersion,
                    ScopeConfig = key.ScopeConfig,
                    BaseRoot = entry.BaseRoot,
                    TraceWallClockMilliseconds = entry.TraceWallClockMilliseconds,
                    CreatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                };
                File.WriteAllText(
                    Path.Combine(stagingDirectory, "metadata.json"),
                    JsonSerializer.Serialize(metadata, Json) + Environment.NewLine);

                if (Directory.Exists(finalDirectory))
                {
                    Directory.Delete(finalDirectory, recursive: true);
                }

                Directory.Move(stagingDirectory, finalDirectory);
            }
            finally
            {
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
            }
        }

        private static readonly IReadOnlyList<string> RunNames = new[] { "base_run1", "base_run2", "base_run3" };

        private void PruneExpired()
        {
            if (!Directory.Exists(_root))
            {
                return;
            }

            IReadOnlyList<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(_root).ToArray();
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            foreach (string directory in directories)
            {
                string metadataPath = Path.Combine(directory, "metadata.json");
                try
                {
                    if (!File.Exists(metadataPath)) continue;
                    CacheMetadata? metadata = JsonSerializer.Deserialize<CacheMetadata>(File.ReadAllText(metadataPath), Json);
                    if (metadata != null
                        && DateTimeOffset.TryParse(metadata.CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset created)
                        && created + _retention <= DateTimeOffset.UtcNow)
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (JsonException)
                {
                }
            }
        }

        private static bool IsCompleteRun(string directory) =>
            Directory.Exists(directory)
            && Directory.EnumerateFiles(directory, "run.*.ndjson")
                .Any(path => !path.Contains(".manifest.", StringComparison.Ordinal) && new FileInfo(path).Length > 0)
            && Directory.EnumerateFiles(directory, "run.*.manifest.ndjson")
                .Any(path => new FileInfo(path).Length > 0)
            && !Directory.EnumerateFiles(directory, "*.FAILED").Any();

        private static void CopyDirectory(string source, string destination)
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }

            Directory.CreateDirectory(destination);
            foreach (string file in Directory.EnumerateFiles(source))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
            }
        }

        private sealed class CacheMetadata
        {
            public string Schema { get; init; } = string.Empty;
            public string TargetSha { get; init; } = string.Empty;
            public string Language { get; init; } = string.Empty;
            public string TracerVersion { get; init; } = string.Empty;
            public string ScopeConfig { get; init; } = string.Empty;
            public string BaseRoot { get; init; } = string.Empty;
            public long TraceWallClockMilliseconds { get; init; }
            public string CreatedUtc { get; init; } = string.Empty;
        }
    }
}