using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BehaviorDiff.Contracts;

namespace BehaviorDiff.Tracer
{
    /// <summary>
    /// Process-wide tracing state: test correlation, call-frame tracking, and event emission.
    /// </summary>
    public static class TraceSession
    {
        /// <summary>Used when a traced call happens outside any test, so events stay parseable.</summary>
        public const string NoTestId = "(no-test)";

        private static readonly AsyncLocal<string?> s_testId = new AsyncLocal<string?>();

        // AsyncLocal, not [ThreadStatic]: an async continuation may resume on a different thread, and the
        // parent/depth relationship has to follow the logical flow rather than the physical thread.
        private static readonly AsyncLocal<CallFrame?> s_currentFrame = new AsyncLocal<CallFrame?>();

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> s_testRootInvocations =
            new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        private static readonly ConcurrentDictionary<string, int> s_callOrdinals =
            new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        // Guards against a traced method being re-entered by the tracer itself, e.g. through argument rendering.
        [ThreadStatic]
        private static bool t_inTracer;

        private static readonly ConcurrentDictionary<MethodBase, MethodTraceInfo> s_methods =
            new ConcurrentDictionary<MethodBase, MethodTraceInfo>();

        private static readonly object s_gate = new object();

        private static TracerOptions s_options = new TracerOptions();
        private static TraceBuffer? s_buffer;
        private static SourceLocationResolver? s_locations;
        private static long s_nextCallId;
        private static long s_internalErrors;
        private static bool s_initialized;

        /// <summary>
        /// The test currently executing on this logical flow. Set by the xunit integration; readable by
        /// anything that wants to correlate. Flows into tasks started by the test.
        /// </summary>
        public static string? CurrentTestId
        {
            get => s_testId.Value;
            set => s_testId.Value = value;
        }

        /// <summary>True once patches are installed and events are being written.</summary>
        public static bool IsActive => s_buffer != null;

        /// <summary>Absolute path of the trace file, or null when tracing is off.</summary>
        public static string? TracePath => s_buffer?.FilePath;

        /// <summary>Number of events handed to the buffer so far.</summary>
        public static long EventCount => s_buffer?.Enqueued ?? 0;

        /// <summary>Failures inside the tracer itself. Non-zero means the trace is incomplete.</summary>
        public static long InternalErrors => Interlocked.Read(ref s_internalErrors);

        /// <summary>Configures from BEHAVIORDIFF_* environment variables. Safe to call repeatedly.</summary>
        public static void InitializeFromEnvironment()
        {
            Initialize(TracerOptions.FromEnvironment());
        }

        /// <summary>Installs patches. The first call wins; later calls are ignored.</summary>
        public static void Initialize(TracerOptions options)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            lock (s_gate)
            {
                if (s_initialized)
                {
                    return;
                }

                s_initialized = true;
                s_options = options;

                if (!options.IsEnabled)
                {
                    return;
                }

                string tracePath = options.ResolveTracePath();
                TracerDiagnostics.Configure(tracePath);
                s_locations = new SourceLocationResolver();
                s_buffer = new TraceBuffer(tracePath, options.QueueCapacity);

                AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
            }
        }

        /// <summary>Drains the buffer, writes the coverage manifest, and closes the trace file.</summary>
        public static void Shutdown()
        {
            TraceBuffer? buffer;
            SourceLocationResolver? locations;
            TracerOptions options;

            lock (s_gate)
            {
                buffer = s_buffer;
                locations = s_locations;
                options = s_options;
                s_buffer = null;
                s_locations = null;
            }

            // Manifest first: it has to describe every member the weaver registered.
            WeaveHooks.WriteManifest(options.ResolveManifestPath());

            buffer?.Dispose();
            locations?.Dispose();

            // Appended after Dispose, because the written count is only final once the pump has stopped.
            if (buffer != null)
            {
                AppendWriterStats(buffer, options);
            }
        }

        private static void AppendWriterStats(TraceBuffer buffer, TracerOptions options)
        {
            var stats = new WriterStatsEntry
            {
                Enqueued = buffer.Enqueued,
                Written = buffer.Written,
                Dropped = buffer.Dropped,
                Capacity = options.QueueCapacity,
            };

            try
            {
                File.AppendAllText(options.ResolveManifestPath(), ManifestNdjson.ToLine(stats) + "\n");
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        internal static void RegisterMethod(MethodBase method, MethodTraceInfo info)
        {
            s_methods[method] = info;
        }

        internal static object? BeginCall(MethodBase method, object[] args)
        {
            if (s_buffer is null || t_inTracer)
            {
                return null;
            }

            t_inTracer = true;
            try
            {
                // The only MethodBase-dependent step: the patcher populated this registry at install time.
                if (!s_methods.TryGetValue(method, out MethodTraceInfo? info))
                {
                    return null;
                }

                return BeginCallCore(info, args);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref s_internalErrors);
                return null;
            }
            finally
            {
                t_inTracer = false;
            }
        }

        /// <summary>
        /// Entry point for build-time weaving, where the descriptor is emitted alongside the call site and
        /// there is no patcher to populate the MethodBase registry.
        /// </summary>
        internal static object? BeginCall(MethodTraceInfo info, object[] args)
        {
            if (s_buffer is null || t_inTracer)
            {
                return null;
            }

            t_inTracer = true;
            try
            {
                return BeginCallCore(info, args);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref s_internalErrors);
                return null;
            }
            finally
            {
                t_inTracer = false;
            }
        }

        /// <summary>Shared by both instrumentation backends so a frame is built identically either way.</summary>
        private static CallFrame BeginCallCore(MethodTraceInfo info, object[] args)
        {
            // Counts what was observed. Calls that happened before this member was patched ran no
            // tracer code at all and are unobservable; see AssemblyManifestEntry.
            info.Coverage.NoteTracedCall();

            // A test's extent is the subtree under its root call. When no framework adapter has named the
            // test, derive the name here instead. s_testId is AsyncLocal, so a continuation that resumes on
            // another thread still reports the root it belongs to.
            string? previousTestId = s_testId.Value;
            bool ownsTestId = info.IsTestRoot && previousTestId is null;
            if (ownsTestId)
            {
                s_testId.Value = NextSyntheticTestId(info.FullName);
            }

            CallFrame? parent = s_currentFrame.Value;
            string testId = s_testId.Value ?? NoTestId;
            string ordinalKey = testId + "\0" + info.FullName;
            int ordinal = s_callOrdinals.AddOrUpdate(ordinalKey, 0, (_, current) => checked(current + 1));
            var frame = new CallFrame(
                Interlocked.Increment(ref s_nextCallId),
                ordinal,
                parent,
                info,
                testId,
                ValueRenderer.RenderArguments(info.ParameterNames, args, s_options.MaxDigestLength),
                Environment.CurrentManagedThreadId);

            frame.OwnsTestId = ownsTestId;
            frame.PreviousTestId = previousTestId;
            s_currentFrame.Value = frame;
            return frame;
        }

        /// <summary>
        /// Names the nth invocation of a test root. Theory cases share a method, so the ordinal is what
        /// separates them; it is exactly as stable across builds as the invocation order itself.
        /// </summary>
        private static string NextSyntheticTestId(string fullName)
        {
            int ordinal = s_testRootInvocations.AddOrUpdate(fullName, 1, (_, count) => count + 1);
            return fullName + "#" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static void CompleteSync(CallFrame frame, DigestResult? result)
        {
            Emit(frame, result, exceptionType: null);
        }

        internal static void EndCall(CallFrame frame, Exception? exception)
        {
            // Restore the logical call stack first: this must happen whether or not the call threw.
            s_currentFrame.Value = frame.Parent;

            if (frame.OwnsTestId)
            {
                s_testId.Value = frame.PreviousTestId;
            }

            if (exception != null)
            {
                Emit(frame, result: null, exceptionType: exception.GetType().FullName);
                return;
            }

            if (!frame.DeferredToContinuation)
            {
                // No-ops when the postfix already claimed emission; covers the case where it did not run.
                Emit(frame, result: null, exceptionType: null);
            }
        }

        internal static void AttachContinuation(CallFrame frame, Task? task, Func<Task, DigestResult?>? resultRenderer)
        {
            if (task is null)
            {
                Emit(frame, result: null, exceptionType: null);
                return;
            }

            frame.DeferredToContinuation = true;

            // TaskScheduler.Default explicitly: continuing on a captured SynchronizationContext would change
            // where the application's own work runs, which is exactly the behavior we are trying to observe.
            task.ContinueWith(
                completed => CompleteFromTask(frame, completed, resultRenderer),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }

        internal static DigestResult? Render(object? value)
        {
            if (t_inTracer)
            {
                return null;
            }

            t_inTracer = true;
            try
            {
                return ValueRenderer.RenderValue(value, s_options.MaxDigestLength);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref s_internalErrors);
                return null;
            }
            finally
            {
                t_inTracer = false;
            }
        }

        private static void CompleteFromTask(CallFrame frame, Task task, Func<Task, DigestResult?>? resultRenderer)
        {
            DigestResult? result = null;
            string? exceptionType = null;

            if (task.IsFaulted)
            {
                AggregateException? aggregate = task.Exception;
                Exception? actual = aggregate != null && aggregate.InnerExceptions.Count > 0
                    ? aggregate.InnerExceptions[0]
                    : aggregate;
                exceptionType = actual?.GetType().FullName;
            }
            else if (task.IsCanceled)
            {
                exceptionType = typeof(TaskCanceledException).FullName;
            }
            else if (resultRenderer != null)
            {
                try
                {
                    result = resultRenderer(task);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref s_internalErrors);
                }
            }

            Emit(frame, result, exceptionType);
        }

        private static void Emit(CallFrame frame, DigestResult? result, string? exceptionType)
        {
            TraceBuffer? buffer = s_buffer;
            if (buffer is null || !frame.TryClaimEmit())
            {
                return;
            }

            buffer.Enqueue(new TraceEvent
            {
                TestId = frame.TestId,
                MethodFullName = frame.Info.FullName,
                FilePath = frame.Info.FilePath,
                FilePathResolution = frame.Info.SourceResolution,
                Line = frame.Info.Line,
                CallDepth = frame.Depth,
                ParentCallId = frame.Parent?.CallId,
                CallId = frame.CallId,
                Ordinal = frame.Ordinal,
                ArgsDigest = frame.Args?.Hash,
                ArgsRendered = frame.Args?.Rendered,
                ReturnDigest = result?.Hash,
                ReturnRendered = result?.Rendered,
                ExceptionType = exceptionType,

                // The thread the call started on. An async continuation may complete on a different thread;
                // the entry thread is what pairs meaningfully with CallId and ParentCallId.
                ThreadId = frame.ThreadId,

                IsHarness = frame.Info.Coverage.IsTestAssembly,
            });
        }
    }
}
