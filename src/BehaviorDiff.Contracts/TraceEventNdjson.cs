using System;
using System.Text;
using static BehaviorDiff.Contracts.Json;

namespace BehaviorDiff.Contracts
{
    /// <summary>
    /// The trace wire format: exactly one <see cref="TraceEvent"/> per line, encoded as a flat JSON object.
    /// </summary>
    /// <remarks>
    /// Optional fields are omitted when null; unknown fields are ignored on read.
    /// Escaping and scanning live in <see cref="Json"/>, shared with the manifest format so the two
    /// cannot drift.
    /// </remarks>
    public static class TraceEventNdjson
    {
        /// <summary>Line terminator. Always LF, never <see cref="Environment.NewLine"/>, so traces from Windows and Linux builds are byte-comparable.</summary>
        public const string LineTerminator = "\n";

        public const string TestIdField = "testId";
        public const string MethodFullNameField = "methodFullName";
        public const string FilePathField = "filePath";
        public const string FilePathResolutionField = "filePathResolution";
        public const string LineField = "line";
        public const string CallDepthField = "callDepth";
        public const string ParentCallIdField = "parentCallId";
        public const string CallIdField = "callId";
        public const string OrdinalField = "ordinal";
        public const string ArgsDigestField = "argsDigest";
        public const string ArgsRenderedField = "argsRendered";
        public const string ReturnDigestField = "returnDigest";
        public const string ReturnRenderedField = "returnRendered";
        public const string ExceptionTypeField = "exceptionType";
        public const string ThreadIdField = "threadId";
        public const string IsHarnessField = "isHarness";

        /// <summary>Renders <paramref name="traceEvent"/> as a single NDJSON line, without the terminator.</summary>
        public static string ToLine(TraceEvent traceEvent)
        {
            var builder = new StringBuilder(256);
            WriteTo(builder, traceEvent);
            return builder.ToString();
        }

        /// <summary>Appends <paramref name="traceEvent"/> to <paramref name="builder"/> as a single NDJSON line, without the terminator.</summary>
        public static void WriteTo(StringBuilder builder, TraceEvent traceEvent)
        {
            if (builder is null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            if (traceEvent is null)
            {
                throw new ArgumentNullException(nameof(traceEvent));
            }

            builder.Append('{');
            AppendString(builder, TestIdField, traceEvent.TestId, first: true);
            AppendString(builder, MethodFullNameField, traceEvent.MethodFullName, first: false);

            if (traceEvent.FilePath != null)
            {
                AppendString(builder, FilePathField, traceEvent.FilePath, first: false);
            }

            AppendString(builder, FilePathResolutionField, traceEvent.FilePathResolution, first: false);

            AppendNumber(builder, LineField, traceEvent.Line, first: false);
            AppendNumber(builder, CallDepthField, traceEvent.CallDepth, first: false);
            AppendNumber(builder, CallIdField, traceEvent.CallId, first: false);
            AppendNumber(builder, OrdinalField, traceEvent.Ordinal, first: false);

            if (traceEvent.ParentCallId.HasValue)
            {
                AppendNumber(builder, ParentCallIdField, traceEvent.ParentCallId.Value, first: false);
            }

            if (traceEvent.ArgsDigest != null)
            {
                AppendString(builder, ArgsDigestField, traceEvent.ArgsDigest, first: false);
            }

            if (traceEvent.ArgsRendered != null)
            {
                AppendString(builder, ArgsRenderedField, traceEvent.ArgsRendered, first: false);
            }

            if (traceEvent.ReturnDigest != null)
            {
                AppendString(builder, ReturnDigestField, traceEvent.ReturnDigest, first: false);
            }

            if (traceEvent.ReturnRendered != null)
            {
                AppendString(builder, ReturnRenderedField, traceEvent.ReturnRendered, first: false);
            }

            if (traceEvent.ExceptionType != null)
            {
                AppendString(builder, ExceptionTypeField, traceEvent.ExceptionType, first: false);
            }

            AppendNumber(builder, ThreadIdField, traceEvent.ThreadId, first: false);

            if (traceEvent.IsHarness)
            {
                AppendBoolean(builder, IsHarnessField, true, first: false);
            }

            builder.Append('}');
        }

        /// <summary>
        /// Parses a single NDJSON line. Returns <see langword="false"/> with a positioned message in
        /// <paramref name="error"/> rather than throwing, so a torn or corrupt line can be reported and skipped.
        /// </summary>
        public static bool TryParseLine(string line, out TraceEvent? traceEvent, out string? error)
        {
            traceEvent = null;
            error = null;

            if (line is null)
            {
                error = "line is null";
                return false;
            }

            string? testId = null;
            string? methodFullName = null;
            string? filePath = null;
            string? filePathResolution = null;
            string? argsDigest = null;
            string? argsRendered = null;
            string? returnDigest = null;
            string? returnRendered = null;
            string? exceptionType = null;
            long? callId = null;
            long? parentCallId = null;
            int? ordinal = null;
            int sourceLine = 0;
            int callDepth = 0;
            int threadId = 0;
            bool isHarness = false;

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

                    switch (key)
                    {
                        case TestIdField:
                            if (!TryReadNullableString(line, ref i, out testId, out error))
                            {
                                return false;
                            }

                            break;

                        case MethodFullNameField:
                            if (!TryReadNullableString(line, ref i, out methodFullName, out error))
                            {
                                return false;
                            }

                            break;

                        case FilePathField:
                            if (!TryReadNullableString(line, ref i, out filePath, out error))
                            {
                                return false;
                            }

                            break;

                        case FilePathResolutionField:
                            if (!TryReadNullableString(line, ref i, out filePathResolution, out error))
                            {
                                return false;
                            }

                            break;

                        case ArgsDigestField:
                            if (!TryReadNullableString(line, ref i, out argsDigest, out error))
                            {
                                return false;
                            }

                            break;

                        case ReturnDigestField:
                            if (!TryReadNullableString(line, ref i, out returnDigest, out error))
                            {
                                return false;
                            }

                            break;

                        case ArgsRenderedField:
                            if (!TryReadNullableString(line, ref i, out argsRendered, out error))
                            {
                                return false;
                            }

                            break;

                        case ReturnRenderedField:
                            if (!TryReadNullableString(line, ref i, out returnRendered, out error))
                            {
                                return false;
                            }

                            break;

                        case ExceptionTypeField:
                            if (!TryReadNullableString(line, ref i, out exceptionType, out error))
                            {
                                return false;
                            }

                            break;

                        case LineField:
                            if (!TryReadInt32(line, ref i, LineField, out sourceLine, out error))
                            {
                                return false;
                            }

                            break;

                        case CallDepthField:
                            if (!TryReadInt32(line, ref i, CallDepthField, out callDepth, out error))
                            {
                                return false;
                            }

                            break;

                        case ThreadIdField:
                            if (!TryReadInt32(line, ref i, ThreadIdField, out threadId, out error))
                            {
                                return false;
                            }

                            break;

                        case IsHarnessField:
                            if (!TryReadBoolean(line, ref i, IsHarnessField, out isHarness, out error))
                            {
                                return false;
                            }

                            break;

                        case CallIdField:
                            if (!TryReadNullableInt64(line, ref i, CallIdField, out callId, out error))
                            {
                                return false;
                            }

                            break;

                        case OrdinalField:
                            int parsedOrdinal;
                            if (!TryReadInt32(line, ref i, OrdinalField, out parsedOrdinal, out error))
                            {
                                return false;
                            }

                            ordinal = parsedOrdinal;
                            break;

                        case ParentCallIdField:
                            if (!TryReadNullableInt64(line, ref i, ParentCallIdField, out parentCallId, out error))
                            {
                                return false;
                            }

                            break;

                        default:
                            if (!TrySkipValue(line, ref i, out error))
                            {
                                return false;
                            }

                            break;
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

            if (string.IsNullOrEmpty(testId))
            {
                error = "'" + TestIdField + "' is required and must be non-empty";
                return false;
            }

            if (string.IsNullOrEmpty(methodFullName))
            {
                error = "'" + MethodFullNameField + "' is required and must be non-empty";
                return false;
            }

            if (!callId.HasValue)
            {
                error = "'" + CallIdField + "' is required";
                return false;
            }

            if (!ordinal.HasValue || ordinal.Value < 0)
            {
                error = "'" + OrdinalField + "' is required and must be non-negative";
                return false;
            }

            traceEvent = new TraceEvent
            {
                TestId = testId!,
                MethodFullName = methodFullName!,
                FilePath = filePath,
                FilePathResolution = filePathResolution ?? SourceResolution.Unresolved,
                Line = sourceLine,
                CallDepth = callDepth,
                ParentCallId = parentCallId,
                CallId = callId.Value,
                Ordinal = ordinal.Value,
                ArgsDigest = argsDigest,
                ArgsRendered = argsRendered,
                ReturnDigest = returnDigest,
                ReturnRendered = returnRendered,
                ExceptionType = exceptionType,
                ThreadId = threadId,
                IsHarness = isHarness,
            };

            return true;
        }
    }
}
