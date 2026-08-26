using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using static RealDiff.Contracts.Json;

namespace RealDiff.Contracts
{
    /// <summary>Required metadata shared by every process manifest in one run.</summary>
    public sealed class RunMetadataEntry
    {
        public string Schema { get; init; } = TraceFormat.Schema;

        public string Language { get; init; } = string.Empty;
    }

    /// <summary>Process-wide canonicalizer counters.</summary>
    public sealed class DigestStatsEntry
    {
        public long ValuesDigested { get; init; }

        public long DepthLimited { get; init; }

        public long Blocklisted { get; init; }

        public long Errored { get; init; }

        /// <summary>Values whose rendered text was capped. The hash still covers the full text.</summary>
        public long RenderedTruncated { get; init; }

        /// <summary>Struct fields identified but unreadable without violating language access rules.</summary>
        public long UnreadableFields { get; init; }

        /// <summary>Map entries replaced by a deterministic ambiguity marker because their sort probes tied.</summary>
        public long AmbiguousMapEntries { get; init; }
    }

    /// <summary>
    /// An IEnumerable type with no shape rule, digested by raw fields. Each of these is a place where the
    /// digest is still exposing incidental state such as spare capacity or a mutation counter.
    /// </summary>
    public sealed class UnruledEnumerableEntry
    {
        public string TypeName { get; init; } = string.Empty;

        public long Count { get; init; }
    }

    /// <summary>
    /// Event accounting for the writer, so a trace file can be reconciled against what the traced threads
    /// actually produced.
    /// </summary>
    /// <remarks>
    /// Written after the buffer is drained and closed, which is why it is appended to the manifest rather
    /// than emitted with the rest of it. Line count in the file, <see cref="Written"/>, and
    /// <see cref="Enqueued"/> must agree; <see cref="Dropped"/> must be zero. Any mismatch means the trace
    /// is missing events, and missing events look exactly like removed behavior.
    /// </remarks>
    public sealed class WriterStatsEntry
    {
        /// <summary>Events handed to the buffer by traced threads.</summary>
        public long Enqueued { get; init; }

        /// <summary>Events the pump thread wrote as lines.</summary>
        public long Written { get; init; }

        /// <summary>Events discarded because the buffer was already closed.</summary>
        public long Dropped { get; init; }

        /// <summary>Bounded queue capacity. Enqueue blocks at this depth rather than dropping.</summary>
        public int Capacity { get; init; }
    }

    /// <summary>Everything one process recorded about what it could and could not observe.</summary>
    public sealed class CoverageManifest
    {
        public RunMetadataEntry? Metadata { get; init; }

        public IReadOnlyList<AssemblyManifestEntry> Assemblies { get; init; } = new AssemblyManifestEntry[0];

        public IReadOnlyList<ManifestEntry> Members { get; init; } = new ManifestEntry[0];

        public IReadOnlyList<UnruledEnumerableEntry> UnruledEnumerables { get; init; } = new UnruledEnumerableEntry[0];

        public DigestStatsEntry? DigestStats { get; init; }

        public WriterStatsEntry? WriterStats { get; init; }
    }

    /// <summary>
    /// NDJSON encoding of a coverage manifest. Two record shapes share the file, discriminated by a
    /// leading <c>kind</c> field so a reader can dispatch without buffering.
    /// </summary>
    public static class ManifestNdjson
    {
        public const string KindField = "kind";
        public const string RunKind = "run";
        public const string AssemblyKind = "assembly";
        public const string MemberKind = "member";
        public const string DigestKind = "digest";
        public const string UnruledKind = "unruled";
        public const string WriterKind = "writer";

        public const string SchemaField = "schema";
        public const string LanguageField = "language";

        public const string EnqueuedField = "enqueued";
        public const string WrittenField = "written";
        public const string DroppedField = "dropped";
        public const string CapacityField = "capacity";

        public const string ValuesDigestedField = "valuesDigested";
        public const string DepthLimitedField = "depthLimited";
        public const string BlocklistedField = "blocklisted";
        public const string ErroredField = "errored";
        public const string RenderedTruncatedField = "renderedTruncated";
        public const string UnreadableFieldsField = "unreadableFields";
        public const string AmbiguousMapEntriesField = "ambiguousMapEntries";
        public const string TypeNameField = "typeName";
        public const string CountField = "count";

        public const string AssemblyField = "assembly";
        public const string MethodFullNameField = "method";
        public const string StatusField = "status";
        public const string SkipReasonField = "skipReason";
        public const string ReturnKindField = "returnKind";
        public const string IsTestRootField = "isTestRoot";
        public const string SourceResolutionField = "sourceResolution";
        public const string DetailField = "detail";
        public const string DiscoveryField = "discovery";
        public const string ScannedField = "scanned";
        public const string InstrumentedField = "instrumented";
        public const string PatchedMembersField = "patchedMembers";
        public const string DiscoveredMembersField = "discoveredMembers";
        public const string SkippedMembersField = "skippedMembers";
        public const string PatchFailedMembersField = "patchFailedMembers";
        public const string QueuedAtMsField = "queuedAtMs";
        public const string PatchedAtMsField = "patchedAtMs";
        public const string TracedCallsField = "tracedCalls";
        public const string MembersWithExactSourceField = "membersWithExactSource";
        public const string SourceUnavailableField = "sourceUnavailable";
        public const string SourcePartialField = "sourcePartial";
        public const string ExactSourcePercentField = "exactSourcePercent";
        public const string SourceRuleAppliedField = "sourceRule";
        public const string IsTestAssemblyField = "isTestAssembly";
        public const string TestFrameworkReferenceField = "testFrameworkReference";

        public static string ToLine(RunMetadataEntry entry)
        {
            if (entry is null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            var builder = new StringBuilder(96);
            builder.Append('{');
            AppendString(builder, KindField, RunKind, first: true);
            AppendString(builder, SchemaField, entry.Schema, first: false);
            AppendString(builder, LanguageField, entry.Language, first: false);
            return builder.Append('}').ToString();
        }

        public static string ToLine(ManifestEntry entry)
        {
            if (entry is null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            var builder = new StringBuilder(192);
            builder.Append('{');
            AppendString(builder, KindField, MemberKind, first: true);
            AppendString(builder, AssemblyField, entry.Assembly, first: false);

            if (entry.MethodFullName != null)
            {
                AppendString(builder, MethodFullNameField, entry.MethodFullName, first: false);
            }

            AppendString(builder, StatusField, entry.Status.ToString(), first: false);

            if (entry.SkipReason != null)
            {
                AppendString(builder, SkipReasonField, entry.SkipReason, first: false);
            }

            if (entry.ReturnKind != null)
            {
                AppendString(builder, ReturnKindField, entry.ReturnKind, first: false);
            }

            if (entry.IsTestRoot)
            {
                AppendBoolean(builder, IsTestRootField, true, first: false);
            }

            if (entry.SourceResolution != null)
            {
                AppendString(builder, SourceResolutionField, entry.SourceResolution, first: false);
            }

            if (entry.Detail != null)
            {
                AppendString(builder, DetailField, entry.Detail, first: false);
            }

            return builder.Append('}').ToString();
        }

        public static string ToLine(DigestStatsEntry entry)
        {
            var builder = new StringBuilder(160);
            builder.Append('{');
            AppendString(builder, KindField, DigestKind, first: true);
            AppendNumber(builder, ValuesDigestedField, entry.ValuesDigested, first: false);
            AppendNumber(builder, DepthLimitedField, entry.DepthLimited, first: false);
            AppendNumber(builder, BlocklistedField, entry.Blocklisted, first: false);
            AppendNumber(builder, ErroredField, entry.Errored, first: false);
            AppendNumber(builder, RenderedTruncatedField, entry.RenderedTruncated, first: false);
            if (entry.UnreadableFields != 0)
            {
                AppendNumber(builder, UnreadableFieldsField, entry.UnreadableFields, first: false);
            }

            if (entry.AmbiguousMapEntries != 0)
            {
                AppendNumber(builder, AmbiguousMapEntriesField, entry.AmbiguousMapEntries, first: false);
            }

            return builder.Append('}').ToString();
        }

        public static string ToLine(UnruledEnumerableEntry entry)
        {
            var builder = new StringBuilder(128);
            builder.Append('{');
            AppendString(builder, KindField, UnruledKind, first: true);
            AppendString(builder, TypeNameField, entry.TypeName, first: false);
            AppendNumber(builder, CountField, entry.Count, first: false);
            return builder.Append('}').ToString();
        }

        public static string ToLine(WriterStatsEntry entry)
        {
            var builder = new StringBuilder(128);
            builder.Append('{');
            AppendString(builder, KindField, WriterKind, first: true);
            AppendNumber(builder, EnqueuedField, entry.Enqueued, first: false);
            AppendNumber(builder, WrittenField, entry.Written, first: false);
            AppendNumber(builder, DroppedField, entry.Dropped, first: false);
            AppendNumber(builder, CapacityField, entry.Capacity, first: false);
            return builder.Append('}').ToString();
        }

        public static string ToLine(AssemblyManifestEntry entry)
        {
            if (entry is null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            var builder = new StringBuilder(192);
            builder.Append('{');
            AppendString(builder, KindField, AssemblyKind, first: true);
            AppendString(builder, AssemblyField, entry.Assembly, first: false);
            AppendString(builder, DiscoveryField, entry.Discovery.ToString(), first: false);
            AppendBoolean(builder, ScannedField, entry.Scanned, first: false);
            AppendBoolean(builder, InstrumentedField, entry.Instrumented, first: false);
            AppendNumber(builder, PatchedMembersField, entry.PatchedMembers, first: false);
            AppendNumber(builder, DiscoveredMembersField, entry.DiscoveredMembers, first: false);
            AppendNumber(builder, SkippedMembersField, entry.SkippedMembers, first: false);
            AppendNumber(builder, PatchFailedMembersField, entry.PatchFailedMembers, first: false);
            AppendNumber(builder, QueuedAtMsField, entry.QueuedAtMs, first: false);

            if (entry.PatchedAtMs.HasValue)
            {
                AppendNumber(builder, PatchedAtMsField, entry.PatchedAtMs.Value, first: false);
            }

            AppendNumber(builder, TracedCallsField, entry.TracedCalls, first: false);
            AppendNumber(builder, MembersWithExactSourceField, entry.MembersWithExactSource, first: false);
            AppendNumber(builder, ExactSourcePercentField, entry.ExactSourcePercent, first: false);
            AppendString(builder, SourceRuleAppliedField, entry.SourceRuleApplied, first: false);

            if (entry.SourcePartial)
            {
                AppendBoolean(builder, SourcePartialField, true, first: false);
            }

            if (entry.SourceUnavailable)
            {
                AppendBoolean(builder, SourceUnavailableField, true, first: false);
            }

            {
            }

            if (entry.IsTestAssembly)
            {
                AppendBoolean(builder, IsTestAssemblyField, true, first: false);
            }

            if (entry.TestFrameworkReference != null)
            {
                AppendString(builder, TestFrameworkReferenceField, entry.TestFrameworkReference, first: false);
            }

            if (entry.Detail != null)
            {
                AppendString(builder, DetailField, entry.Detail, first: false);
            }

            return builder.Append('}').ToString();
        }

        /// <summary>Parses one line into whichever record shape its <c>kind</c> names.</summary>
        public static bool TryParseLine(
            string line,
            out ManifestEntry? member,
            out AssemblyManifestEntry? assemblyEntry,
            out DigestStatsEntry? digestStats,
            out UnruledEnumerableEntry? unruled,
            out WriterStatsEntry? writerStats,
            out string? error)
        {
            return TryParseLine(
                line,
                out member,
                out assemblyEntry,
                out digestStats,
                out unruled,
                out writerStats,
                out RunMetadataEntry? _,
                out error);
        }

        public static bool TryParseLine(
            string line,
            out ManifestEntry? member,
            out AssemblyManifestEntry? assemblyEntry,
            out DigestStatsEntry? digestStats,
            out UnruledEnumerableEntry? unruled,
            out WriterStatsEntry? writerStats,
            out RunMetadataEntry? metadata,
            out string? error)
        {
            member = null;
            assemblyEntry = null;
            digestStats = null;
            unruled = null;
            writerStats = null;
            metadata = null;
            error = null;

            if (line is null)
            {
                error = "line is null";
                return false;
            }

            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (!TryParseFlatObject(line, fields, out error))
            {
                return false;
            }

            if (!fields.TryGetValue(KindField, out object? kindValue) || kindValue is not string kind)
            {
                error = "'" + KindField + "' is required";
                return false;
            }

            switch (kind)
            {
                case RunKind:
                    string? schema = GetString(fields, SchemaField);
                    string? language = GetString(fields, LanguageField);
                    if (string.IsNullOrEmpty(schema) || string.IsNullOrEmpty(language))
                    {
                        error = "'" + SchemaField + "' and '" + LanguageField + "' are required and must be non-empty";
                        return false;
                    }

                    metadata = new RunMetadataEntry { Schema = schema!, Language = language! };
                    return true;
                case MemberKind:
                    return TryBuildMember(fields, out member, out error);
                case AssemblyKind:
                    return TryBuildAssembly(fields, out assemblyEntry, out error);
                case DigestKind:
                    digestStats = new DigestStatsEntry
                    {
                        ValuesDigested = GetInt64(fields, ValuesDigestedField) ?? 0,
                        DepthLimited = GetInt64(fields, DepthLimitedField) ?? 0,
                        Blocklisted = GetInt64(fields, BlocklistedField) ?? 0,
                        Errored = GetInt64(fields, ErroredField) ?? 0,
                        RenderedTruncated = GetInt64(fields, RenderedTruncatedField) ?? 0,
                        UnreadableFields = GetInt64(fields, UnreadableFieldsField) ?? 0,
                        AmbiguousMapEntries = GetInt64(fields, AmbiguousMapEntriesField) ?? 0,
                    };
                    return true;
                case UnruledKind:
                    unruled = new UnruledEnumerableEntry
                    {
                        TypeName = GetString(fields, TypeNameField) ?? string.Empty,
                        Count = GetInt64(fields, CountField) ?? 0,
                    };
                    return true;
                case WriterKind:
                    writerStats = new WriterStatsEntry
                    {
                        Enqueued = GetInt64(fields, EnqueuedField) ?? 0,
                        Written = GetInt64(fields, WrittenField) ?? 0,
                        Dropped = GetInt64(fields, DroppedField) ?? 0,
                        Capacity = (int)(GetInt64(fields, CapacityField) ?? 0),
                    };
                    return true;
                default:
                    error = "unrecognised " + KindField + ": '" + kind + "'";
                    return false;
            }
        }

        private static bool TryBuildMember(Dictionary<string, object?> fields, out ManifestEntry? member, out string? error)
        {
            member = null;

            string? assembly = GetString(fields, AssemblyField);
            if (string.IsNullOrEmpty(assembly))
            {
                error = "'" + AssemblyField + "' is required and must be non-empty";
                return false;
            }

            string? statusText = GetString(fields, StatusField);
            if (statusText is null)
            {
                error = "'" + StatusField + "' is required";
                return false;
            }

            PatchStatus status;
            switch (statusText)
            {
                case nameof(PatchStatus.Patched):
                    status = PatchStatus.Patched;
                    break;
                case nameof(PatchStatus.Skipped):
                    status = PatchStatus.Skipped;
                    break;
                case nameof(PatchStatus.PatchFailed):
                    status = PatchStatus.PatchFailed;
                    break;
                case nameof(PatchStatus.EnumerationFailed):
                    status = PatchStatus.EnumerationFailed;
                    break;
                default:
                    error = "unrecognised " + StatusField + ": '" + statusText + "'";
                    return false;
            }

            error = null;
            member = new ManifestEntry
            {
                Assembly = assembly!,
                MethodFullName = GetString(fields, MethodFullNameField),
                Status = status,
                SkipReason = GetString(fields, SkipReasonField),
                ReturnKind = GetString(fields, ReturnKindField),
                IsTestRoot = GetBoolean(fields, IsTestRootField),
                SourceResolution = GetString(fields, SourceResolutionField),
                Detail = GetString(fields, DetailField),
            };

            return true;
        }

        private static bool TryBuildAssembly(Dictionary<string, object?> fields, out AssemblyManifestEntry? entry, out string? error)
        {
            entry = null;

            string? assembly = GetString(fields, AssemblyField);
            if (string.IsNullOrEmpty(assembly))
            {
                error = "'" + AssemblyField + "' is required and must be non-empty";
                return false;
            }

            string? discoveryText = GetString(fields, DiscoveryField);
            AssemblyDiscovery discovery;
            switch (discoveryText)
            {
                case nameof(AssemblyDiscovery.BuildTimeWeave):
                    discovery = AssemblyDiscovery.BuildTimeWeave;
                    break;
                case nameof(AssemblyDiscovery.JavaAgentTransform):
                    discovery = AssemblyDiscovery.JavaAgentTransform;
                    break;
                case nameof(AssemblyDiscovery.NodeAstTransform):
                    discovery = AssemblyDiscovery.NodeAstTransform;
                    break;
                case nameof(AssemblyDiscovery.GoAstRewrite):
                    discovery = AssemblyDiscovery.GoAstRewrite;
                    break;
                case nameof(AssemblyDiscovery.RustAstRewrite):
                    discovery = AssemblyDiscovery.RustAstRewrite;
                    break;
                default:
                    // StartupEnumeration and AssemblyLoadEvent were the runtime patcher's; a manifest still
                    // carrying them predates build-time weaving and its coverage claims do not transfer.
                    error = "unrecognised " + DiscoveryField + ": '" + discoveryText + "'";
                    return false;
            }

            // Retired with the runtime patcher. Its presence means a manifest written before build-time
            // weaving, whose coverage claims are about a different instrumentation model.
            if (fields.ContainsKey("latePatched") || fields.ContainsKey("prePatchCoverage"))
            {
                error = "manifest carries retired field latePatched/prePatchCoverage; it predates build-time weaving";
                return false;
            }

            error = null;
            entry = new AssemblyManifestEntry
            {
                Assembly = assembly!,
                Discovery = discovery,
                Scanned = GetBoolean(fields, ScannedField),
                Instrumented = GetBoolean(fields, InstrumentedField),
                PatchedMembers = (int)(GetInt64(fields, PatchedMembersField) ?? 0),
                DiscoveredMembers = (int)(GetInt64(fields, DiscoveredMembersField) ?? 0),
                SkippedMembers = (int)(GetInt64(fields, SkippedMembersField) ?? 0),
                PatchFailedMembers = (int)(GetInt64(fields, PatchFailedMembersField) ?? 0),
                QueuedAtMs = GetInt64(fields, QueuedAtMsField) ?? 0,
                PatchedAtMs = GetInt64(fields, PatchedAtMsField),
                TracedCalls = GetInt64(fields, TracedCallsField) ?? 0,
                MembersWithExactSource = (int)(GetInt64(fields, MembersWithExactSourceField) ?? 0),
                ExactSourcePercent = (int)(GetInt64(fields, ExactSourcePercentField) ?? 0),
                SourceRuleApplied = GetString(fields, SourceRuleAppliedField) ?? SourceRule.NotApplicable,
                SourcePartial = GetBoolean(fields, SourcePartialField),
                SourceUnavailable = GetBoolean(fields, SourceUnavailableField),
                IsTestAssembly = GetBoolean(fields, IsTestAssemblyField),
                TestFrameworkReference = GetString(fields, TestFrameworkReferenceField),
                Detail = GetString(fields, DetailField),
            };

            return true;
        }

        private static string? GetString(Dictionary<string, object?> fields, string name)
        {
            return fields.TryGetValue(name, out object? value) ? value as string : null;
        }

        private static bool GetBoolean(Dictionary<string, object?> fields, string name)
        {
            return fields.TryGetValue(name, out object? value) && value is bool flag && flag;
        }

        private static long? GetInt64(Dictionary<string, object?> fields, string name)
        {
            return fields.TryGetValue(name, out object? value) && value is long number ? number : null;
        }

        private static bool TryParseFlatObject(string line, Dictionary<string, object?> fields, out string? error)
        {
            error = null;
            int i = 0;
            SkipWhitespace(line, ref i);
            if (i >= line.Length || line[i] != '{')
            {
                error = Describe(i, "expected '{'");
                return false;
            }

            i++;
            SkipWhitespace(line, ref i);
            if (i < line.Length && line[i] == '}')
            {
                i++;
            }
            else
            {
                while (true)
                {
                    SkipWhitespace(line, ref i);
                    if (!TryReadString(line, ref i, out string key, out error))
                    {
                        return false;
                    }

                    SkipWhitespace(line, ref i);
                    if (i >= line.Length || line[i] != ':')
                    {
                        error = Describe(i, "expected ':'");
                        return false;
                    }

                    i++;
                    SkipWhitespace(line, ref i);

                    if (i >= line.Length)
                    {
                        error = Describe(i, "unexpected end of line");
                        return false;
                    }

                    char c = line[i];
                    if (c == '"')
                    {
                        if (!TryReadString(line, ref i, out string text, out error))
                        {
                            return false;
                        }

                        fields[key] = text;
                    }
                    else if (c == 't' || c == 'f')
                    {
                        if (!TryReadBoolean(line, ref i, key, out bool flag, out error))
                        {
                            return false;
                        }

                        fields[key] = flag;
                    }
                    else if (c == 'n')
                    {
                        if (!TryConsumeLiteral(line, ref i, "null"))
                        {
                            error = Describe(i, "expected null");
                            return false;
                        }

                        fields[key] = null;
                    }
                    else if (c == '-' || (c >= '0' && c <= '9'))
                    {
                        if (!TryReadNullableInt64(line, ref i, key, out long? number, out error))
                        {
                            return false;
                        }

                        fields[key] = number;
                    }
                    else if (!TrySkipValue(line, ref i, out error))
                    {
                        return false;
                    }

                    SkipWhitespace(line, ref i);
                    if (i < line.Length && line[i] == ',')
                    {
                        i++;
                        continue;
                    }

                    if (i < line.Length && line[i] == '}')
                    {
                        i++;
                        break;
                    }

                    error = Describe(i, "expected ',' or '}'");
                    return false;
                }
            }

            SkipWhitespace(line, ref i);
            if (i != line.Length)
            {
                error = Describe(i, "unexpected trailing content");
                return false;
            }

            return true;
        }
    }

    /// <summary>Reads and writes a whole coverage manifest.</summary>
    /// <remarks>
    /// Written whole rather than appended: the manifest is a snapshot of one process's coverage and is
    /// rewritten as late-loaded assemblies are discovered.
    /// </remarks>
    public static class ManifestFile
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        public static void Write(string path, CoverageManifest manifest)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("Path must be non-empty.", nameof(path));
            }

            if (manifest is null)
            {
                throw new ArgumentNullException(nameof(manifest));
            }

            string fullPath = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, Utf8NoBom) { NewLine = TraceEventNdjson.LineTerminator };

            if (manifest.Metadata != null)
            {
                writer.WriteLine(ManifestNdjson.ToLine(manifest.Metadata));
            }

            foreach (AssemblyManifestEntry entry in manifest.Assemblies)
            {
                writer.WriteLine(ManifestNdjson.ToLine(entry));
            }

            foreach (ManifestEntry entry in manifest.Members)
            {
                writer.WriteLine(ManifestNdjson.ToLine(entry));
            }

            foreach (UnruledEnumerableEntry entry in manifest.UnruledEnumerables)
            {
                writer.WriteLine(ManifestNdjson.ToLine(entry));
            }

            if (manifest.DigestStats != null)
            {
                writer.WriteLine(ManifestNdjson.ToLine(manifest.DigestStats));
            }
        }

        /// <summary>Reads a manifest, throwing <see cref="FormatException"/> on the first malformed line.</summary>
        public static CoverageManifest Read(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("Path must be non-empty.", nameof(path));
            }

            var assemblies = new List<AssemblyManifestEntry>();
            var members = new List<ManifestEntry>();
            var unruledEntries = new List<UnruledEnumerableEntry>();
            DigestStatsEntry? stats = null;
            WriterStatsEntry? writer = null;
            RunMetadataEntry? metadata = null;
            long lineNumber = 0;

            using var reader = new StreamReader(
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;
                if (line.Length == 0)
                {
                    continue;
                }

                if (!ManifestNdjson.TryParseLine(
                        line,
                        out ManifestEntry? member,
                        out AssemblyManifestEntry? assembly,
                        out DigestStatsEntry? digestStats,
                        out UnruledEnumerableEntry? unruled,
                        out WriterStatsEntry? writerStats,
                        out RunMetadataEntry? runMetadata,
                        out string? error))
                {
                    throw new FormatException(path + "(" + lineNumber + "): " + error);
                }

                if (member != null)
                {
                    members.Add(member);
                }
                else if (assembly != null)
                {
                    assemblies.Add(assembly);
                }
                else if (unruled != null)
                {
                    unruledEntries.Add(unruled);
                }
                else if (digestStats != null)
                {
                    stats = digestStats;
                }
                else if (writerStats != null)
                {
                    writer = writerStats;
                }
                else if (runMetadata != null)
                {
                    if (metadata != null)
                    {
                        throw new FormatException(path + "(" + lineNumber + "): duplicate run metadata record");
                    }

                    metadata = runMetadata;
                }
            }

            return new CoverageManifest
            {
                Metadata = metadata,
                Assemblies = assemblies,
                Members = members,
                UnruledEnumerables = unruledEntries,
                DigestStats = stats,
                WriterStats = writer,
            };
        }
    }
}
