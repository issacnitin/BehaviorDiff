using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BehaviorDiff.Contracts
{
    /// <summary>Outcome of parsing one physical line of a trace file.</summary>
    public readonly struct TraceLineResult
    {
        private TraceLineResult(long lineNumber, TraceEvent? traceEvent, string? error, string? rawLine)
        {
            LineNumber = lineNumber;
            Event = traceEvent;
            Error = error;
            RawLine = rawLine;
        }

        /// <summary>1-based physical line number within the file.</summary>
        public long LineNumber { get; }

        /// <summary>The parsed event, or <see langword="null"/> when the line was malformed.</summary>
        public TraceEvent? Event { get; }

        /// <summary>Failure description, or <see langword="null"/> when the line parsed.</summary>
        public string? Error { get; }

        /// <summary>The offending text, retained only for malformed lines.</summary>
        public string? RawLine { get; }

        /// <summary>True when <see cref="Event"/> is populated.</summary>
        public bool IsValid => Event != null;

        internal static TraceLineResult Parsed(long lineNumber, TraceEvent traceEvent)
        {
            return new TraceLineResult(lineNumber, traceEvent, null, null);
        }

        internal static TraceLineResult Failed(long lineNumber, string error, string rawLine)
        {
            return new TraceLineResult(lineNumber, null, error, rawLine);
        }
    }

    /// <summary>Reads NDJSON trace files produced by <see cref="NdjsonTraceWriter"/>.</summary>
    public static class NdjsonTraceReader
    {
        /// <summary>
        /// Streams every line, reporting malformed ones instead of throwing. Blank lines are skipped.
        /// </summary>
        public static IEnumerable<TraceLineResult> ReadWithDiagnostics(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("Path must be non-empty.", nameof(path));
            }

            var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);

            return ReadWithDiagnostics(new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true), disposeReader: true);
        }

        /// <summary>
        /// Streams every line from <paramref name="reader"/>, reporting malformed ones instead of throwing.
        /// </summary>
        /// <param name="disposeReader">Dispose <paramref name="reader"/> when enumeration finishes.</param>
        public static IEnumerable<TraceLineResult> ReadWithDiagnostics(TextReader reader, bool disposeReader = false)
        {
            if (reader is null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            return Iterate(reader, disposeReader);
        }

        /// <summary>
        /// Streams parsed events, throwing <see cref="FormatException"/> on the first malformed line.
        /// </summary>
        public static IEnumerable<TraceEvent> Read(string path)
        {
            foreach (TraceLineResult result in ReadWithDiagnostics(path))
            {
                if (!result.IsValid)
                {
                    throw new FormatException(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}({1}): {2}",
                        path,
                        result.LineNumber,
                        result.Error));
                }

                yield return result.Event!;
            }
        }

        private static IEnumerable<TraceLineResult> Iterate(TextReader reader, bool disposeReader)
        {
            try
            {
                long lineNumber = 0;
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    lineNumber++;

                    if (line.Length == 0 || IsWhitespaceOnly(line))
                    {
                        continue;
                    }

                    if (TraceEventNdjson.TryParseLine(line, out TraceEvent? traceEvent, out string? error))
                    {
                        yield return TraceLineResult.Parsed(lineNumber, traceEvent!);
                    }
                    else
                    {
                        yield return TraceLineResult.Failed(lineNumber, error ?? "unknown parse failure", line);
                    }
                }
            }
            finally
            {
                if (disposeReader)
                {
                    reader.Dispose();
                }
            }
        }

        private static bool IsWhitespaceOnly(string line)
        {
            for (int i = 0; i < line.Length; i++)
            {
                if (!char.IsWhiteSpace(line[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
