using System;
using System.Globalization;
using System.IO;

namespace RealDiff.Tracer
{
    /// <summary>
    /// Tracer's own diagnostics.
    /// </summary>
    /// <remarks>
    /// Written to a sidecar file next to the trace as well as stderr, because test hosts routinely
    /// capture, buffer, reorder, or drop a child process's stderr. Losing the record of which methods were
    /// skipped would make a coverage gap indistinguishable from a behavior change.
    /// </remarks>
    internal static class TracerDiagnostics
    {
        private const string Prefix = "RealDiff: ";

        private static readonly object s_gate = new object();
        private static string? s_logPath;
        private static string? s_failurePath;
        private static long s_sequence;

        internal static string? LogPath => s_logPath;

        internal static void Configure(string tracePath)
        {
            lock (s_gate)
            {
                s_logPath = tracePath + ".log";
                s_failurePath = tracePath + ".FAILED";
                foreach (string path in new[] { s_logPath, s_failurePath })
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
        }

        /// <summary>
        /// Records a condition that invalidates the run, in a file the harness can find. The tracer runs
        /// inside a test host whose exit code belongs to the test framework, so it cannot fail the process.
        /// </summary>
        internal static void WriteFailureMarker(string message)
        {
            lock (s_gate)
            {
                if (s_failurePath is null)
                {
                    return;
                }

                try
                {
                    File.AppendAllText(s_failurePath, message + Environment.NewLine);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        internal static void Write(string message)
        {
            // Everything under one lock. The file order is used to reason about which tracer operations
            // overlapped, so it has to be the real order; writing to stderr outside the lock would let two
            // threads land in the file in the opposite order to the events they describe.
            lock (s_gate)
            {
                Console.Error.WriteLine(Prefix + message);

                if (s_logPath is null)
                {
                    return;
                }

                try
                {
                    File.AppendAllText(s_logPath, message + Environment.NewLine);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        /// <summary>
        /// Records a drain lifecycle event with the sequence, thread and elapsed time needed to check
        /// that patching never runs re-entrantly or inside an assembly load callback.
        /// </summary>
        internal static void Drain(string phase, string assembly, long elapsedMs, string? extra = null)
        {
            lock (s_gate)
            {
                long sequence = ++s_sequence;
                string line = "DRAIN seq=" + sequence.ToString(CultureInfo.InvariantCulture)
                    + " phase=" + phase
                    + " asm=" + assembly
                    + " tid=" + Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture)
                    + " t=" + elapsedMs.ToString(CultureInfo.InvariantCulture) + "ms"
                    + (extra is null ? string.Empty : " " + extra);

                if (s_logPath is null)
                {
                    return;
                }

                try
                {
                    File.AppendAllText(s_logPath, line + Environment.NewLine);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
