using System;
using System.Globalization;
using System.Text;

namespace RealDiff.Contracts
{
    /// <summary>
    /// Minimal JSON writing and scanning for flat objects, shared by every NDJSON line format here.
    /// </summary>
    /// <remarks>
    /// Hand-rolled rather than System.Text.Json so the Tracer assembly, which loads into an arbitrary
    /// target process, carries no package dependency that could conflict with one the target already has.
    /// </remarks>
    internal static class Json
    {
        internal static void AppendString(StringBuilder builder, string name, string value, bool first)
        {
            AppendName(builder, name, first);
            AppendEscaped(builder, value);
        }

        internal static void AppendNumber(StringBuilder builder, string name, long value, bool first)
        {
            AppendName(builder, name, first);
            builder.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        internal static void AppendBoolean(StringBuilder builder, string name, bool value, bool first)
        {
            AppendName(builder, name, first);
            builder.Append(value ? "true" : "false");
        }

        private static void AppendName(StringBuilder builder, string name, bool first)
        {
            if (!first)
            {
                builder.Append(',');
            }

            builder.Append('"').Append(name).Append("\":");
        }

        internal static void AppendEscaped(StringBuilder builder, string value)
        {
            builder.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        // U+2028/U+2029 are legal inside a JSON string but are line breaks to some readers,
                        // which would split one record across two NDJSON lines.
                        if (c < ' ' || c == '\u2028' || c == '\u2029')
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            builder.Append('"');
        }

        internal static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n'))
            {
                i++;
            }
        }

        internal static bool TryReadString(string s, ref int i, out string value, out string? error)
        {
            value = string.Empty;
            error = null;

            if (i >= s.Length || s[i] != '"')
            {
                error = Describe(i, "expected '\"'");
                return false;
            }

            i++;
            var builder = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"')
                {
                    value = builder.ToString();
                    return true;
                }

                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }

                if (i >= s.Length)
                {
                    break;
                }

                char escape = s[i++];
                switch (escape)
                {
                    case '"':
                        builder.Append('"');
                        break;
                    case '\\':
                        builder.Append('\\');
                        break;
                    case '/':
                        builder.Append('/');
                        break;
                    case 'b':
                        builder.Append('\b');
                        break;
                    case 'f':
                        builder.Append('\f');
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case 'u':
                        if (i + 4 > s.Length)
                        {
                            error = Describe(i, "truncated \\u escape");
                            return false;
                        }

                        if (!int.TryParse(
                                s.Substring(i, 4),
                                NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture,
                                out int codeUnit))
                        {
                            error = Describe(i, "invalid \\u escape");
                            return false;
                        }

                        builder.Append((char)codeUnit);
                        i += 4;
                        break;
                    default:
                        error = Describe(i - 1, "invalid escape '\\" + escape + "'");
                        return false;
                }
            }

            error = Describe(i, "unterminated string");
            return false;
        }

        internal static bool TryReadNullableString(string s, ref int i, out string? value, out string? error)
        {
            value = null;

            if (TryConsumeLiteral(s, ref i, "null"))
            {
                error = null;
                return true;
            }

            if (!TryReadString(s, ref i, out string read, out error))
            {
                return false;
            }

            value = read;
            return true;
        }

        internal static bool TryReadInt32(string s, ref int i, string name, out int value, out string? error)
        {
            value = 0;
            if (!TryReadNumberToken(s, ref i, name, out string token, out error))
            {
                return false;
            }

            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                error = "'" + name + "' is not a 32-bit integer: '" + token + "'";
                return false;
            }

            return true;
        }

        internal static bool TryReadNullableInt64(string s, ref int i, string name, out long? value, out string? error)
        {
            value = null;

            if (TryConsumeLiteral(s, ref i, "null"))
            {
                error = null;
                return true;
            }

            if (!TryReadNumberToken(s, ref i, name, out string token, out error))
            {
                return false;
            }

            if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {
                error = "'" + name + "' is not a 64-bit integer: '" + token + "'";
                return false;
            }

            value = parsed;
            return true;
        }

        internal static bool TryReadBoolean(string s, ref int i, string name, out bool value, out string? error)
        {
            error = null;

            if (TryConsumeLiteral(s, ref i, "true"))
            {
                value = true;
                return true;
            }

            if (TryConsumeLiteral(s, ref i, "false"))
            {
                value = false;
                return true;
            }

            value = false;
            error = Describe(i, "expected a boolean for '" + name + "'");
            return false;
        }

        internal static bool TryReadNumberToken(string s, ref int i, string name, out string token, out string? error)
        {
            error = null;
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+'))
            {
                i++;
            }

            while (i < s.Length && (s[i] >= '0' && s[i] <= '9'))
            {
                i++;
            }

            if (i == start)
            {
                token = string.Empty;
                error = Describe(start, "expected a number for '" + name + "'");
                return false;
            }

            token = s.Substring(start, i - start);
            return true;
        }

        internal static bool TryConsumeLiteral(string s, ref int i, string literal)
        {
            if (i + literal.Length <= s.Length && string.CompareOrdinal(s, i, literal, 0, literal.Length) == 0)
            {
                i += literal.Length;
                return true;
            }

            return false;
        }

        internal static bool TrySkipValue(string s, ref int i, out string? error)
        {
            error = null;
            SkipWhitespace(s, ref i);
            if (i >= s.Length)
            {
                error = Describe(i, "unexpected end of line");
                return false;
            }

            char c = s[i];
            if (c == '"')
            {
                return TryReadString(s, ref i, out _, out error);
            }

            if (c == '{' || c == '[')
            {
                int depth = 0;
                while (i < s.Length)
                {
                    char current = s[i];
                    if (current == '"')
                    {
                        if (!TryReadString(s, ref i, out _, out error))
                        {
                            return false;
                        }

                        continue;
                    }

                    if (current == '{' || current == '[')
                    {
                        depth++;
                        i++;
                        continue;
                    }

                    if (current == '}' || current == ']')
                    {
                        depth--;
                        i++;
                        if (depth == 0)
                        {
                            return true;
                        }

                        continue;
                    }

                    i++;
                }

                error = "unterminated object or array";
                return false;
            }

            if (c == 't' || c == 'f' || c == 'n')
            {
                while (i < s.Length && s[i] >= 'a' && s[i] <= 'z')
                {
                    i++;
                }

                return true;
            }

            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+'))
            {
                i++;
            }

            while (i < s.Length && ((s[i] >= '0' && s[i] <= '9') || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '-' || s[i] == '+'))
            {
                i++;
            }

            if (i == start)
            {
                error = Describe(start, "unrecognised value");
                return false;
            }

            return true;
        }

        internal static string Describe(int index, string message)
        {
            return message + " at index " + index.ToString(CultureInfo.InvariantCulture);
        }
    }
}
