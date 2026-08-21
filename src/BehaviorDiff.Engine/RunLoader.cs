using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BehaviorDiff.Contracts;

namespace BehaviorDiff.Engine
{
    /// <summary>One run: every process's events merged, plus the merged coverage manifest.</summary>
    internal sealed class RunData
    {
        internal RunData(
            string name,
            string root,
            IReadOnlyList<LoadedEvent> events,
            IReadOnlyDictionary<string, ManifestEntry> members,
            IReadOnlyDictionary<string, AssemblyManifestEntry> assemblies,
            IReadOnlyList<string> traceFiles,
            string schema,
            string language)
        {
            Name = name;
            Root = root;
            Events = events;
            Members = members;
            Assemblies = assemblies;
            TraceFiles = traceFiles;
            Schema = schema;
            Language = language;
        }

        internal string Name { get; }

        /// <summary>Worktree root the absolute FilePaths were made relative to.</summary>
        internal string Root { get; }

        internal IReadOnlyList<LoadedEvent> Events { get; }

        internal IReadOnlyDictionary<string, ManifestEntry> Members { get; }

        internal IReadOnlyDictionary<string, AssemblyManifestEntry> Assemblies { get; }

        internal IReadOnlyList<string> TraceFiles { get; }

        internal string Schema { get; }

        internal string Language { get; }

        internal int SubjectEventCount => Events.Count(e => !e.Event.IsHarness);

        internal int HarnessEventCount => Events.Count(e => e.Event.IsHarness);
    }

    /// <summary>
    /// A trace event plus where it came from. Provenance is kept because ordering within a key is file
    /// order within one process, and merging several processes would otherwise lose that.
    /// </summary>
    internal sealed class LoadedEvent
    {
        internal LoadedEvent(TraceEvent traceEvent, string processKey, int lineNumber, string? relativePath)
        {
            Event = traceEvent;
            ProcessKey = processKey;
            LineNumber = lineNumber;
            RelativePath = relativePath;
        }

        internal TraceEvent Event { get; }

        internal string ProcessKey { get; }

        internal int LineNumber { get; }

        /// <summary>Repo-relative, forward-slashed. Null when the event carried no path.</summary>
        internal string? RelativePath { get; }
    }

    internal sealed class LoadReport
    {
        internal int MalformedLines { get; set; }

        internal int AbsolutePathsRemaining { get; set; }

        internal int PathsNormalized { get; set; }

        internal int PathsAlreadyRelative { get; set; }

        internal int PathsMissing { get; set; }

        internal bool RootWasInferred { get; set; }
    }

    internal static class RunLoader
    {
        internal static RunData Load(string name, string directory, string? explicitRoot, LoadReport report)
        {
            if (!Directory.Exists(directory))
            {
                throw new DiffInputException("Run '" + name + "': directory not found: " + directory);
            }

            var manifestFiles = Directory.GetFiles(directory, "*.manifest.ndjson").OrderBy(p => p, StringComparer.Ordinal).ToList();
            var traceFiles = Directory.GetFiles(directory, "*.ndjson")
                .Where(p => !p.EndsWith(".manifest.ndjson", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            if (traceFiles.Count == 0)
            {
                throw new DiffInputException("Run '" + name + "': no trace files (*.ndjson) in " + directory);
            }

            var raw = new List<(TraceEvent Event, string ProcessKey, int Line)>();
            var recordCounts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (string file in traceFiles)
            {
                string processKey = Path.GetFileNameWithoutExtension(file);
                foreach (TraceLineResult result in NdjsonTraceReader.ReadWithDiagnostics(file))
                {
                    recordCounts.TryGetValue(processKey, out long records);
                    recordCounts[processKey] = records + 1;
                    if (result.Event is null)
                    {
                        report.MalformedLines++;
                        continue;
                    }

                    raw.Add((result.Event, processKey, (int)result.LineNumber));
                }
            }

            string root = explicitRoot ?? InferRoot(raw.Select(r => r.Event.FilePath));
            report.RootWasInferred = explicitRoot is null;

            var events = new List<LoadedEvent>(raw.Count);
            foreach ((TraceEvent traceEvent, string processKey, int line) in raw)
            {
                string? relative = NormalizePath(traceEvent.FilePath, root, report);
                events.Add(new LoadedEvent(traceEvent, processKey, line, relative));
            }

            var members = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
            var assemblies = new Dictionary<string, AssemblyManifestEntry>(StringComparer.Ordinal);
            string? schema = null;
            string? language = null;
            foreach (string manifestPath in manifestFiles)
            {
                CoverageManifest manifest = ManifestFile.Read(manifestPath);
                ValidateManifest(manifestPath, manifest, recordCounts);

                if (manifest.Metadata is null)
                {
                    throw new DiffInputException("Manifest has no run metadata: " + manifestPath);
                }

                if (!string.Equals(manifest.Metadata.Schema, TraceFormat.Schema, StringComparison.Ordinal))
                {
                    throw new DiffInputException("Manifest schema '" + manifest.Metadata.Schema
                        + "' is unsupported; expected '" + TraceFormat.Schema + "': " + manifestPath);
                }

                if (schema != null && !string.Equals(schema, manifest.Metadata.Schema, StringComparison.Ordinal)
                    || language != null && !string.Equals(language, manifest.Metadata.Language, StringComparison.Ordinal))
                {
                    throw new DiffInputException("Process manifests disagree on schema or language in run '" + name + "'.");
                }

                schema = manifest.Metadata.Schema;
                language = manifest.Metadata.Language;
                MergeMembers(members, manifest.Members);
                MergeAssemblies(assemblies, manifest.Assemblies);
            }

            if (manifestFiles.Count == 0 || schema is null || language is null)
            {
                throw new DiffInputException("Run '" + name + "': no versioned coverage manifests in " + directory);
            }

            ValidateOrdinals(name, raw);
            return new RunData(name, root, events, members, assemblies, traceFiles, schema, language);
        }

        private static void ValidateManifest(
            string path,
            CoverageManifest manifest,
            IReadOnlyDictionary<string, long> recordCounts)
        {
            foreach (AssemblyManifestEntry module in manifest.Assemblies)
            {
                int memberRecords = manifest.Members.Count(member =>
                    string.Equals(member.Assembly, module.Assembly, StringComparison.Ordinal));
                if (module.PatchFailedMembers != 0
                    || module.DiscoveredMembers != module.PatchedMembers + module.SkippedMembers
                    || module.DiscoveredMembers != memberRecords)
                {
                    throw new DiffInputException("Manifest member accounting does not reconcile for module '"
                        + module.Assembly + "': discovered=" + module.DiscoveredMembers
                        + " instrumented=" + module.PatchedMembers + " skipped=" + module.SkippedMembers
                        + " failed=" + module.PatchFailedMembers + " records=" + memberRecords + ".");
                }
            }

            if (manifest.WriterStats is null)
            {
                throw new DiffInputException("Manifest has no writer accounting: " + path);
            }

            WriterStatsEntry writer = manifest.WriterStats;
            string fileName = Path.GetFileName(path);
            const string suffix = ".manifest.ndjson";
            string processKey = fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? fileName.Substring(0, fileName.Length - suffix.Length)
                : fileName;
            recordCounts.TryGetValue(processKey, out long physicalRecords);
            if (writer.Dropped != 0 || writer.Enqueued != writer.Written || writer.Written != physicalRecords)
            {
                throw new DiffInputException("Trace writer accounting does not reconcile for '" + processKey
                    + "': enqueued=" + writer.Enqueued + " written=" + writer.Written
                    + " records=" + physicalRecords + " dropped=" + writer.Dropped + ".");
            }
        }

        private static void ValidateOrdinals(
            string runName,
            IReadOnlyList<(TraceEvent Event, string ProcessKey, int Line)> events)
        {
            foreach (IGrouping<string, (TraceEvent Event, string ProcessKey, int Line)> group in events.GroupBy(
                item => item.ProcessKey + "\0" + item.Event.TestId + "\0" + item.Event.MethodFullName,
                StringComparer.Ordinal))
            {
                int expected = 0;
                foreach (int ordinal in group.Select(item => item.Event.Ordinal).OrderBy(value => value))
                {
                    if (ordinal != expected++)
                    {
                        throw new DiffInputException("Run '" + runName
                            + "' has a duplicate or non-contiguous call ordinal for " + group.Key.Replace('\0', '|') + ".");
                    }
                }
            }
        }

        /// <summary>
        /// Longest common directory prefix of the run's absolute paths. Only a starting point - the
        /// overlap check between base and PR is what actually catches a wrong root.
        /// </summary>
        private static string InferRoot(IEnumerable<string?> paths)
        {
            string? prefix = null;
            foreach (string? path in paths)
            {
                if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path))
                {
                    continue;
                }

                string directory = Path.GetDirectoryName(path) ?? string.Empty;
                prefix = prefix is null ? directory : CommonPrefix(prefix, directory);
            }

            return prefix ?? string.Empty;
        }

        private static string CommonPrefix(string a, string b)
        {
            string[] left = a.Split('\\', '/');
            string[] right = b.Split('\\', '/');
            int count = 0;
            while (count < left.Length && count < right.Length
                && string.Equals(left[count], right[count], StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }

            return string.Join(Path.DirectorySeparatorChar.ToString(), left, 0, count);
        }

        private static string? NormalizePath(string? absolute, string root, LoadReport report)
        {
            if (string.IsNullOrEmpty(absolute))
            {
                report.PathsMissing++;
                return null;
            }

            if (!Path.IsPathRooted(absolute))
            {
                report.PathsAlreadyRelative++;
                return absolute.Replace('\\', '/');
            }

            // SourceLink's DeterministicSourcePaths rewrites compile-time paths to this root, so a
            // release-built assembly carries no machine path to strip. IsPathRooted still calls it rooted.
            if (absolute.StartsWith("/_/", StringComparison.Ordinal))
            {
                report.PathsNormalized++;
                return absolute.Substring(3).Replace('\\', '/');
            }

            if (root.Length > 0 && absolute.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                string trimmed = absolute.Substring(root.Length).TrimStart('\\', '/');
                report.PathsNormalized++;
                return trimmed.Replace('\\', '/');
            }

            report.AbsolutePathsRemaining++;
            return absolute.Replace('\\', '/');
        }

        private static void MergeMembers(Dictionary<string, ManifestEntry> target, IReadOnlyList<ManifestEntry> source)
        {
            foreach (ManifestEntry entry in source)
            {
                if (entry.MethodFullName is null)
                {
                    continue;
                }

                // A member patched in any process of the run is observable in that run.
                if (!target.TryGetValue(entry.MethodFullName, out ManifestEntry? existing)
                    || (existing.Status != PatchStatus.Patched && entry.Status == PatchStatus.Patched))
                {
                    target[entry.MethodFullName] = entry;
                }
            }
        }

        private static void MergeAssemblies(Dictionary<string, AssemblyManifestEntry> target, IReadOnlyList<AssemblyManifestEntry> source)
        {
            foreach (AssemblyManifestEntry entry in source)
            {
                if (!target.TryGetValue(entry.Assembly, out AssemblyManifestEntry? existing)
                    || (!existing.Instrumented && entry.Instrumented))
                {
                    target[entry.Assembly] = entry;
                }
            }
        }
    }

    internal sealed class DiffInputException : Exception
    {
        internal DiffInputException(string message)
            : base(message)
        {
        }
    }
}
