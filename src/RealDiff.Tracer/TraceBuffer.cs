using System;
using System.Collections.Concurrent;
using System.Threading;
using RealDiff.Contracts;

namespace RealDiff.Tracer
{
    /// <summary>
    /// Producer/consumer buffer between traced threads and the NDJSON file.
    /// </summary>
    /// <remarks>
    /// Traced threads only enqueue; a single background thread owns the writer, so
    /// <see cref="NdjsonTraceWriter"/> is touched from exactly one thread and lines can never interleave.
    /// The queue is bounded and enqueue <em>blocks</em> when full rather than dropping: a dropped event is
    /// indistinguishable from a behavior change, which would produce a false positive in the diff.
    /// </remarks>
    internal sealed class TraceBuffer : IDisposable
    {
        private const int FlushEveryEvents = 512;
        private const int IdleFlushMilliseconds = 200;

        private readonly BlockingCollection<TraceEvent> _queue;
        private readonly NdjsonTraceWriter _writer;
        private readonly Thread _pump;
        private readonly ManualResetEventSlim _drained = new ManualResetEventSlim(false);
        private long _enqueued;
        private long _written;
        private long _dropped;
        private int _disposed;

        internal TraceBuffer(string path, int capacity)
        {
            _writer = new NdjsonTraceWriter(path);
            _queue = new BlockingCollection<TraceEvent>(new ConcurrentQueue<TraceEvent>(), capacity);
            _pump = new Thread(Pump)
            {
                IsBackground = true,
                Name = "RealDiff trace writer",
            };
            _pump.Start();
        }

        internal string FilePath => _writer.FilePath;

        internal long Enqueued => Interlocked.Read(ref _enqueued);

        internal long Written => Interlocked.Read(ref _written);

        /// <summary>
        /// Events that arrived after the buffer closed. Counted rather than ignored: a dropped event is
        /// indistinguishable from a call that never happened, so the number has to appear somewhere.
        /// </summary>
        internal long Dropped => Interlocked.Read(ref _dropped);

        internal void Enqueue(TraceEvent traceEvent)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }

            try
            {
                _queue.Add(traceEvent);
                Interlocked.Increment(ref _enqueued);
            }
            catch (InvalidOperationException)
            {
                // Adding completed, or the collection was disposed, during shutdown.
                // ObjectDisposedException derives from InvalidOperationException, so this covers both.
                Interlocked.Increment(ref _dropped);
            }
        }

        private void Pump()
        {
            int pending = 0;
            while (true)
            {
                TraceEvent? traceEvent = null;
                bool took;
                try
                {
                    took = _queue.TryTake(out traceEvent, IdleFlushMilliseconds);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (took && traceEvent != null)
                {
                    _writer.Write(traceEvent);
                    Interlocked.Increment(ref _written);
                    if (++pending >= FlushEveryEvents)
                    {
                        _writer.Flush();
                        pending = 0;
                    }

                    continue;
                }

                if (pending > 0)
                {
                    _writer.Flush();
                    pending = 0;
                }

                if (_queue.IsCompleted)
                {
                    break;
                }
            }

            _writer.Flush();
            _drained.Set();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _queue.CompleteAdding();

            // Bounded: ProcessExit handlers get roughly two seconds before the runtime stops waiting.
            _drained.Wait(TimeSpan.FromSeconds(5));

            _writer.Dispose();
            _queue.Dispose();
            _drained.Dispose();
        }
    }
}
