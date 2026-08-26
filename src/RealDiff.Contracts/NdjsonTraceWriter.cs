using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RealDiff.Contracts
{
    /// <summary>
    /// Appends <see cref="TraceEvent"/>s to an NDJSON file. Thread-safe.
    /// </summary>
    /// <remarks>
    /// The file is opened with <see cref="FileShare.ReadWrite"/> so the Engine can read a trace while the
    /// traced process is still writing it. One writer per process is assumed: two writers appending to the
    /// same path concurrently is not guaranteed to keep lines intact.
    /// </remarks>
    public sealed class NdjsonTraceWriter : IDisposable
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private readonly object _gate = new object();
        private readonly StringBuilder _buffer = new StringBuilder(256);
        private readonly StreamWriter _writer;
        private bool _disposed;

        /// <param name="path">File to append to. Created, with its directory, if missing.</param>
        /// <param name="autoFlush">Flush after every event. Costs throughput, but survives a hard process kill.</param>
        public NdjsonTraceWriter(string path, bool autoFlush = false)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("Path must be non-empty.", nameof(path));
            }

            FilePath = Path.GetFullPath(path);

            string? directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var stream = new FileStream(
                FilePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 64 * 1024,
                FileOptions.None);

            _writer = new StreamWriter(stream, Utf8NoBom)
            {
                AutoFlush = autoFlush,
                NewLine = TraceEventNdjson.LineTerminator,
            };
        }

        /// <summary>Absolute path being appended to.</summary>
        public string FilePath { get; }

        /// <summary>Appends one event as one line.</summary>
        public void Write(TraceEvent traceEvent)
        {
            if (traceEvent is null)
            {
                throw new ArgumentNullException(nameof(traceEvent));
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(NdjsonTraceWriter));
                }

                _buffer.Length = 0;
                TraceEventNdjson.WriteTo(_buffer, traceEvent);
                _writer.WriteLine(_buffer.ToString());
            }
        }

        /// <summary>Appends every event in <paramref name="traceEvents"/>, in order.</summary>
        public void WriteAll(IEnumerable<TraceEvent> traceEvents)
        {
            if (traceEvents is null)
            {
                throw new ArgumentNullException(nameof(traceEvents));
            }

            foreach (TraceEvent traceEvent in traceEvents)
            {
                Write(traceEvent);
            }
        }

        /// <summary>Pushes buffered lines to the operating system.</summary>
        public void Flush()
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _writer.Flush();
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _writer.Dispose();
            }
        }
    }
}
