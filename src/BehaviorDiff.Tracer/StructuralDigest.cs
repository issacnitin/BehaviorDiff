using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace BehaviorDiff.Tracer
{
    /// <summary>Canonical text for a value, plus the hash the engine actually compares.</summary>
    internal readonly struct DigestResult
    {
        internal DigestResult(string hash, string rendered)
        {
            Hash = hash;
            Rendered = rendered;
        }

        /// <summary>Hash of the <em>full</em> canonical text, never of the truncated rendering.</summary>
        internal string Hash { get; }

        /// <summary>Canonical text, capped for readability with an explicit truncation marker.</summary>
        internal string Rendered { get; }
    }

    /// <summary>
    /// Renders an object graph to canonical text by reading fields only, then hashes that text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No member that the target can override is ever invoked.</b> No property getters, no
    /// <c>ToString</c>, no <c>GetHashCode</c>, no <c>Equals</c>, no <c>GetEnumerator</c>. A tracer runs
    /// inside the process under test, and any of those can allocate without bound, block, mutate state,
    /// throw, or re-enter patched code, which would make the act of observing change what is observed.
    /// Field reads execute no target code. Reference identity is compared with
    /// <see cref="object.ReferenceEquals"/> and hashed with <see cref="RuntimeHelpers.GetHashCode"/> so
    /// the cycle detector does not call the graph's own equality members either.
    /// </para>
    /// <para>
    /// Primitives, enums, and the BCL date/time and Guid structs are formatted through BCL conversions.
    /// Those types are sealed or non-overridable, so no target code runs, but it is a deliberate exception
    /// to the "no ToString" rule rather than an oversight.
    /// </para>
    /// <para>
    /// <b>Shape rules are curated and incomplete by construction.</b> Collections are digested through
    /// per-type rules that read only live elements, because raw field digestion of a BCL collection
    /// exposes incidental state - spare capacity slots, mutation counters - that differs between two
    /// collections holding identical content. Any IEnumerable without a rule falls through to raw fields
    /// and is counted as an unruled enumerable in the manifest. That counter is the list of places where
    /// this class is still wrong.
    /// </para>
    /// </remarks>
    internal static class StructuralDigest
    {
        /// <summary>Rendered text is capped here; the hash is computed before this cap is applied.</summary>
        internal const int RenderedCap = 2000;

        private const string TruncationMarker = "<truncated>";

        private static readonly ConcurrentDictionary<Type, FieldInfo[]> FieldCache =
            new ConcurrentDictionary<Type, FieldInfo[]>();

        private static readonly ConcurrentDictionary<Type, string> BlockedCache =
            new ConcurrentDictionary<Type, string>();

        // Checked before any recursion. Matching is by name so the tracer needs no reference to EF Core,
        // the DI abstractions, or anything else it is refusing to walk into.
        private static readonly string[] BlockedNamespacePrefixes =
        {
            "Microsoft.Extensions.",
            "Microsoft.EntityFrameworkCore.",
        };

        private static readonly string[] BlockedTypeNames =
        {
            "System.IServiceProvider",
            "System.IO.Stream",
            "System.Threading.CancellationToken",
            "System.Threading.CancellationTokenSource",
            "System.Threading.Tasks.Task",
            "System.Threading.Tasks.ValueTask",
            "System.Type",
            "System.Reflection.Assembly",
            "System.Reflection.MemberInfo",
            "System.Reflection.Module",
            "System.Delegate",
            "System.MulticastDelegate",
            "System.IntPtr",
            "System.UIntPtr",
        };

        internal static DigestResult ComputeValue(object? value, DigestOptions options)
        {
            var builder = new StringBuilder(128);
            var context = new Context(options);
            WriteTagged(builder, value, 0, context);
            return Finish(builder, context);
        }

        internal static DigestResult ComputeArguments(string[] parameterNames, object[] args, DigestOptions options)
        {
            var builder = new StringBuilder(160);
            var context = new Context(options);

            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                if (i < parameterNames.Length)
                {
                    builder.Append(parameterNames[i]).Append('=');
                }

                WriteTagged(builder, args[i], 0, context);
            }

            return Finish(builder, context);
        }

        private static DigestResult Finish(StringBuilder builder, Context context)
        {
            string canonical = builder.ToString();
            string hash = Hash(canonical);

            if (canonical.Length <= RenderedCap)
            {
                return new DigestResult(hash, canonical);
            }

            DigestStatistics.NoteRenderedTruncated();
            return new DigestResult(hash, canonical.Substring(0, RenderedCap) + TruncationMarker);
        }

        private static string Hash(string canonical)
        {
            using var sha = SHA256.Create();
            byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));

            var builder = new StringBuilder(7 + (bytes.Length * 2));
            builder.Append("sha256:");
            for (int i = 0; i < bytes.Length; i++)
            {
                builder.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        /// <summary>Writes a value prefixed with the rule that produced it.</summary>
        private static void WriteTagged(StringBuilder builder, object? value, int depth, Context context)
        {
            int start = builder.Length;
            builder.Append("        ");
            string rule = Write(builder, value, depth, context);
            builder.Remove(start, 8).Insert(start, rule + ":");
        }

        private static string Write(StringBuilder builder, object? value, int depth, Context context)
        {
            DigestStatistics.NoteValue();

            if (builder.Length > context.CanonicalCap)
            {
                return "Primitive";
            }

            // null is its own marker and is never conflated with an empty string, an empty collection,
            // or a default-valued struct.
            if (value is null)
            {
                builder.Append("null");
                return "Primitive";
            }

            if (TryWriteScalar(builder, value))
            {
                return "Primitive";
            }

            Type type = value.GetType();

            // Blocklist is consulted before the cycle register and before any field walk, so a blocked
            // graph is never entered even once.
            string? blocked = BlockedReason(type);
            if (blocked != null)
            {
                DigestStatistics.NoteBlocklisted();
                builder.Append("<skipped:").Append(blocked).Append('>');
                return "Blocklisted";
            }

            if (!type.IsValueType)
            {
                if (context.Visited.TryGetValue(value, out int existing))
                {
                    builder.Append("<ref ").Append(existing.ToString(CultureInfo.InvariantCulture)).Append('>');
                    return "StructuralFields";
                }

                context.Visited.Add(value, context.Visited.Count);
            }

            if (depth >= context.MaxDepth)
            {
                DigestStatistics.NoteDepthLimited();
                builder.Append("<depth:").Append(type.Name).Append('>');
                return "DepthLimit";
            }

            if (type.IsArray)
            {
                WriteArray(builder, (Array)value, depth, context);
                return "ShapeRule:Array";
            }

            string? shapeRule = TryWriteShape(builder, value, type, depth, context);
            if (shapeRule != null)
            {
                return "ShapeRule:" + shapeRule;
            }

            if (IsEnumerable(type))
            {
                DigestStatistics.NoteUnruledEnumerable(type.FullName ?? type.Name);
            }

            WriteFields(builder, value, type, depth, context);
            return "StructuralFields";
        }

        private static bool TryWriteScalar(StringBuilder builder, object value)
        {
            switch (value)
            {
                case string s:
                    WriteString(builder, s);
                    return true;
                case bool b:
                    builder.Append(b ? "true" : "false");
                    return true;
                case char c:
                    builder.Append('\'').Append(c).Append('\'');
                    return true;
                case sbyte or byte or short or ushort or int or uint or long or ulong:
                    builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    return true;
                case float f:
                    builder.Append(f.ToString("R", CultureInfo.InvariantCulture));
                    return true;
                case double d:
                    builder.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    return true;
                case decimal m:
                    builder.Append(m.ToString(CultureInfo.InvariantCulture));
                    return true;

                // Normalized, not formatted. A timestamp or a fresh Guid differs on every run of the same
                // build, so carrying the value through would make two identical runs diverge.
                case DateTime:
                    builder.Append("<datetime>");
                    return true;
                case DateTimeOffset:
                    builder.Append("<datetimeoffset>");
                    return true;
                case Guid:
                    builder.Append("<guid>");
                    return true;

                case TimeSpan ts:
                    builder.Append(ts.ToString("c", CultureInfo.InvariantCulture));
                    return true;
                case Enum e:
                    builder.Append(e.GetType().Name).Append('.').Append(e.ToString());
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Absolute paths and temp files are normalized: they embed the machine's directory layout and a
        /// random file name, neither of which is behavior.
        /// </summary>
        private static void WriteString(StringBuilder builder, string value)
        {
            if (value.Length > 2 && LooksLikePath(value))
            {
                string temp = TempPrefix;
                if (temp.Length > 0 && value.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
                {
                    builder.Append("<tempfile>");
                    return;
                }

                builder.Append("<path>");
                return;
            }

            builder.Append('"').Append(value).Append('"');
        }

        private static readonly string TempPrefix = SafeTempPath();

        private static string SafeTempPath()
        {
            try
            {
                return Path.GetTempPath();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static bool LooksLikePath(string value)
        {
            if (value.IndexOf('\0') >= 0)
            {
                return false;
            }

            bool windowsRooted = value.Length > 2 && char.IsLetter(value[0]) && value[1] == ':'
                && (value[2] == '\\' || value[2] == '/');
            bool unixRooted = value[0] == '/' && value.IndexOf('/', 1) > 0;
            bool uncRooted = value.StartsWith("\\\\", StringComparison.Ordinal);

            return windowsRooted || unixRooted || uncRooted;
        }

        private static void WriteArray(StringBuilder builder, Array array, int depth, Context context)
        {
            builder.Append(array.GetType().GetElementType()?.Name ?? "?")
                .Append('[').Append(array.Length.ToString(CultureInfo.InvariantCulture)).Append("]{");

            if (array.Rank != 1)
            {
                builder.Append("rank=").Append(array.Rank.ToString(CultureInfo.InvariantCulture)).Append('}');
                return;
            }

            int lower = array.GetLowerBound(0);
            int count = Math.Min(array.Length, context.MaxElements);
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                Write(builder, array.GetValue(lower + i), depth + 1, context);
                if (builder.Length > context.CanonicalCap)
                {
                    break;
                }
            }

            if (array.Length > count)
            {
                builder.Append(", \u2026");
            }

            builder.Append('}');
        }

        /// <summary>Returns the applied rule name, or null when no rule matched.</summary>
        private static string? TryWriteShape(StringBuilder builder, object value, Type type, int depth, Context context)
        {
            switch (NormalizedName(type))
            {
                case "System.Collections.Generic.List":
                    return WriteIndexed(builder, value, type, depth, context, "List", "_items", "_size", 0);
                case "System.Collections.Generic.Stack":
                    return WriteIndexed(builder, value, type, depth, context, "Stack", "_array", "_size", 0);
                case "System.Collections.Generic.Queue":
                    return WriteQueue(builder, value, type, depth, context);
                case "System.Collections.ObjectModel.ReadOnlyCollection":
                    return WriteWrapped(builder, value, type, depth, context, "ReadOnlyCollection", "list");
                case "System.Collections.Immutable.ImmutableArray":
                    return WriteWrapped(builder, value, type, depth, context, "ImmutableArray", "array");
                case "System.Collections.Generic.Dictionary":
                    return WriteHashed(builder, value, type, depth, context, "Dictionary", "key", "value");
                case "System.Collections.Generic.HashSet":
                    return WriteHashed(builder, value, type, depth, context, "HashSet", "Value", null);
                default:
                    return null;
            }
        }

        private static string? WriteIndexed(
            StringBuilder builder,
            object value,
            Type type,
            int depth,
            Context context,
            string ruleName,
            string arrayField,
            string countField,
            int offset)
        {
            if (ReadField(type, value, arrayField) is not Array items || ReadField(type, value, countField) is not int size)
            {
                return null;
            }

            builder.Append(ruleName).Append('[').Append(size.ToString(CultureInfo.InvariantCulture)).Append("]{");

            int count = Math.Min(size, context.MaxElements);
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                Write(builder, items.GetValue(offset + i), depth + 1, context);
                if (builder.Length > context.CanonicalCap)
                {
                    break;
                }
            }

            if (size > count)
            {
                builder.Append(", \u2026");
            }

            builder.Append('}');
            return ruleName;
        }

        private static string? WriteQueue(StringBuilder builder, object value, Type type, int depth, Context context)
        {
            if (ReadField(type, value, "_array") is not Array items
                || ReadField(type, value, "_head") is not int head
                || ReadField(type, value, "_size") is not int size
                || items.Length == 0)
            {
                return null;
            }

            builder.Append("Queue[").Append(size.ToString(CultureInfo.InvariantCulture)).Append("]{");

            int count = Math.Min(size, context.MaxElements);
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                Write(builder, items.GetValue((head + i) % items.Length), depth + 1, context);
                if (builder.Length > context.CanonicalCap)
                {
                    break;
                }
            }

            if (size > count)
            {
                builder.Append(", \u2026");
            }

            builder.Append('}');
            return "Queue";
        }

        private static string? WriteWrapped(
            StringBuilder builder,
            object value,
            Type type,
            int depth,
            Context context,
            string ruleName,
            string innerField)
        {
            object? inner = ReadField(type, value, innerField);
            if (inner is null)
            {
                return null;
            }

            builder.Append(ruleName).Append('{');
            Write(builder, inner, depth + 1, context);
            builder.Append('}');
            return ruleName;
        }

        /// <summary>
        /// Hash-based collections: entry order depends on key hash codes, and reference-type keys hash by
        /// identity, which varies per process. Child digests are sorted so the same content produces the
        /// same text in every run.
        /// </summary>
        private static string? WriteHashed(
            StringBuilder builder,
            object value,
            Type type,
            int depth,
            Context context,
            string ruleName,
            string keyField,
            string? valueField)
        {
            if (ReadField(type, value, "_entries") is not Array entries || ReadField(type, value, "_count") is not int count)
            {
                return null;
            }

            Type? entryType = entries.GetType().GetElementType();
            if (entryType is null)
            {
                return null;
            }

            var rendered = new List<string>();
            int limit = Math.Min(count, entries.Length);
            for (int i = 0; i < limit; i++)
            {
                object? entry = entries.GetValue(i);
                if (entry is null)
                {
                    continue;
                }

                // .NET Core marks free-list slots with next < -1. If the field is absent the layout is
                // unknown, so every slot is included rather than silently dropping live entries.
                object? next = ReadField(entryType, entry, "next") ?? ReadField(entryType, entry, "Next");
                if (next is int nextIndex && nextIndex < -1)
                {
                    continue;
                }

                var itemBuilder = new StringBuilder(48);
                Write(itemBuilder, ReadField(entryType, entry, keyField), depth + 1, context);
                if (valueField != null)
                {
                    itemBuilder.Append(" => ");
                    Write(itemBuilder, ReadField(entryType, entry, valueField), depth + 1, context);
                }

                rendered.Add(itemBuilder.ToString());

                if (rendered.Count >= context.MaxElements)
                {
                    break;
                }
            }

            rendered.Sort(StringComparer.Ordinal);

            builder.Append(ruleName).Append('[').Append(rendered.Count.ToString(CultureInfo.InvariantCulture)).Append("]{");
            for (int i = 0; i < rendered.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(rendered[i]);
            }

            builder.Append('}');
            return ruleName;
        }

        private static void WriteFields(StringBuilder builder, object value, Type type, int depth, Context context)
        {
            builder.Append(type.Name).Append('{');

            FieldInfo[] fields = FieldCache.GetOrAdd(type, CollectFields);
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append("; ");
                }

                builder.Append(fields[i].Name).Append('=');

                object? fieldValue;
                try
                {
                    fieldValue = fields[i].GetValue(value);
                }
                catch (Exception ex)
                {
                    // Never omitted. A digest that silently drops a field makes two different objects
                    // render identically, which reads downstream as "no behavior change".
                    DigestStatistics.NoteErrored();
                    builder.Append("<error:").Append(fields[i].Name).Append(':').Append(ex.GetType().Name).Append('>');
                    continue;
                }

                Write(builder, fieldValue, depth + 1, context);

                if (builder.Length > context.CanonicalCap)
                {
                    break;
                }
            }

            builder.Append('}');
        }

        private static object? ReadField(Type type, object instance, string name)
        {
            try
            {
                FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return field?.GetValue(instance);
            }
            catch (Exception)
            {
                DigestStatistics.NoteErrored();
                return null;
            }
        }

        private static string? BlockedReason(Type type)
        {
            return BlockedCache.GetOrAdd(type, static candidate =>
            {
                for (Type? current = candidate; current != null; current = current.BaseType)
                {
                    string name = NormalizedName(current);
                    foreach (string prefix in BlockedNamespacePrefixes)
                    {
                        if (name.StartsWith(prefix, StringComparison.Ordinal))
                        {
                            return name;
                        }
                    }

                    foreach (string blocked in BlockedTypeNames)
                    {
                        if (string.Equals(name, blocked, StringComparison.Ordinal))
                        {
                            return name;
                        }
                    }
                }

                foreach (Type contract in candidate.GetInterfaces())
                {
                    string name = NormalizedName(contract);
                    foreach (string prefix in BlockedNamespacePrefixes)
                    {
                        if (name.StartsWith(prefix, StringComparison.Ordinal))
                        {
                            return name;
                        }
                    }

                    foreach (string blocked in BlockedTypeNames)
                    {
                        if (string.Equals(name, blocked, StringComparison.Ordinal))
                        {
                            return name;
                        }
                    }
                }

                return string.Empty;
            }) is { Length: > 0 } reason
                ? reason
                : null;
        }

        private static string NormalizedName(Type type)
        {
            string name = type.Name;
            int tick = name.IndexOf('`');
            if (tick >= 0)
            {
                name = name.Substring(0, tick);
            }

            return string.IsNullOrEmpty(type.Namespace) ? name : type.Namespace + "." + name;
        }

        private static bool IsEnumerable(Type type)
        {
            foreach (Type contract in type.GetInterfaces())
            {
                if (contract == typeof(IEnumerable))
                {
                    return true;
                }
            }

            return false;
        }

        private static FieldInfo[] CollectFields(Type type)
        {
            var fields = new List<FieldInfo>();
            for (Type? current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                fields.AddRange(current.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
            }

            fields.Sort(static (left, right) =>
            {
                int byType = string.CompareOrdinal(left.DeclaringType?.FullName, right.DeclaringType?.FullName);
                return byType != 0 ? byType : string.CompareOrdinal(left.Name, right.Name);
            });

            return fields.ToArray();
        }

        private sealed class Context
        {
            internal Context(DigestOptions options)
            {
                MaxDepth = options.MaxDepth;
                CanonicalCap = options.CanonicalCap;
                MaxElements = options.MaxElements;
                Visited = new Dictionary<object, int>(ReferenceComparer.Instance);
            }

            internal int MaxDepth { get; }

            internal int CanonicalCap { get; }

            internal int MaxElements { get; }

            internal Dictionary<object, int> Visited { get; }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();

            public new bool Equals(object? x, object? y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return RuntimeHelpers.GetHashCode(obj);
            }
        }
    }

    internal sealed class DigestOptions
    {
        internal DigestOptions(int maxDepth, int canonicalCap, int maxElements)
        {
            MaxDepth = maxDepth;
            CanonicalCap = canonicalCap;
            MaxElements = maxElements;
        }

        /// <summary>Kept at 6: real graphs reach four or five levels, and truncated-away state is a silent gap.</summary>
        internal int MaxDepth { get; }

        /// <summary>Safety bound on canonical text so a pathological graph cannot exhaust memory.</summary>
        internal int CanonicalCap { get; }

        internal int MaxElements { get; }
    }
}
