using System;
using RealDiff.Contracts;

namespace RealDiff.Tracer
{
    /// <summary>
    /// Entry points called from woven IL. Public because instrumented assemblies reference them directly,
    /// unlike the Harmony path where reflection reaches internal patch bodies.
    /// </summary>
    /// <remarks>
    /// Exit ordering mirrors <see cref="TracePatches"/> exactly: on the success path the return value is
    /// recorded and then the event is emitted; on the throw path only the emit runs. The event is produced
    /// at method exit under both backends, which is what makes per-key ordinals comparable between them.
    /// </remarks>
    public static class WeaveHooks
    {
        private static readonly object s_gate = new object();
        private static readonly string[] s_noParameters = new string[0];

        // Read without a lock on the hot path; replaced wholesale under the lock when it grows.
        private static MethodTraceInfo?[] s_descriptors = new MethodTraceInfo?[0];

        private static AssemblyCoverage?[] s_assemblies = new AssemblyCoverage?[0];

        private static readonly System.Collections.Generic.Dictionary<int, AssemblyCoverage> s_coverageByBase =
            new System.Collections.Generic.Dictionary<int, AssemblyCoverage>();

        // Every member the weaver considered, woven or skipped, so discovered == Woven + Skipped reconciles
        // the same way the Harmony manifest does. A weave failure is a build error, never a manifest row.
        private static readonly System.Collections.Generic.List<ManifestEntry> s_members =
            new System.Collections.Generic.List<ManifestEntry>();

        // A woven exit that cannot recover its frame emits nothing, and a missing event is indistinguishable
        // from a behaviour change downstream. Counted rather than thrown: faulting inside instrumentation
        // would corrupt the process under test. The harness asserts this is zero.
        private static int s_lostFrames;

        internal static int LostFrames => System.Threading.Volatile.Read(ref s_lostFrames);

        /// <summary>
        /// Declares a woven assembly and reserves its descriptor range. The returned base is added to each
        /// call site's local index, so indices stay unique when several assemblies are woven into one process.
        /// </summary>
        public static int RegisterAssembly(string assemblyName, bool isTestAssembly, int memberCount)
        {
            if (assemblyName == null)
            {
                throw new ArgumentNullException(nameof(assemblyName));
            }

            if (memberCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(memberCount));
            }

            lock (s_gate)
            {
                var coverage = new AssemblyCoverage(assemblyName, AssemblyDiscovery.BuildTimeWeave, queuedAtMs: 0)
                {
                    IsTestAssembly = isTestAssembly,
                };

                int baseOffset = s_descriptors.Length;
                Array.Resize(ref s_descriptors, baseOffset + memberCount);
                s_coverageByBase[baseOffset] = coverage;

                int index = s_assemblies.Length;
                var grown = new AssemblyCoverage?[index + 1];
                Array.Copy(s_assemblies, grown, index);
                grown[index] = coverage;
                s_assemblies = grown;
                return baseOffset;
            }
        }

        /// <summary>Declares one woven method. The index is a dense slot the call site passes back to <see cref="Enter"/>.</summary>
        public static void Register(
            int baseOffset,
            int localIndex,
            string fullName,
            string? filePath,
            int line,
            string sourceResolution,
            int returnKind,
            bool isTestRoot,
            string parameterNames)
        {
            if (localIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(localIndex));
            }

            if (!Enum.IsDefined(typeof(ReturnKind), returnKind))
            {
                throw new ArgumentOutOfRangeException(nameof(returnKind), returnKind, "not a ReturnKind value");
            }

            lock (s_gate)
            {
                if (!s_coverageByBase.TryGetValue(baseOffset, out AssemblyCoverage coverage))
                {
                    throw new ArgumentOutOfRangeException(nameof(baseOffset));
                }

                int index = baseOffset + localIndex;
                if (index >= s_descriptors.Length)
                {
                    var grown = new MethodTraceInfo?[index + 1];
                    Array.Copy(s_descriptors, grown, s_descriptors.Length);
                    s_descriptors = grown;
                }

                if (s_descriptors[index] != null)
                {
                    throw new InvalidOperationException("duplicate weave index " + index + " for " + fullName);
                }

                s_descriptors[index] = new MethodTraceInfo(
                    fullName,
                    filePath,
                    line,
                    sourceResolution,
                    (ReturnKind)returnKind,
                    parameterNames.Length == 0 ? s_noParameters : parameterNames.Split(','),
                coverage,
                isTestRoot);
                coverage.NotePatchedMember();
                coverage.NoteMemberSource(sourceResolution);

                s_members.Add(new ManifestEntry
                {
                    Assembly = coverage.Name,
                    MethodFullName = fullName,
                    Status = PatchStatus.Patched,
                    FilePath = filePath,
                    Line = line,
                    ReturnKind = ((ReturnKind)returnKind).ToString(),
                    IsTestRoot = isTestRoot,
                    SourceResolution = sourceResolution,
                });
            }
        }

        /// <summary>
        /// Starts the trace session from environment configuration. Emitted at the end of every woven module's
        /// initializer, so a woven process traces without a test-framework adapter to start it. Initialization
        /// is idempotent, so it does not matter which woven module happens to run first.
        /// </summary>
        public static void EnsureSession()
        {
            TraceSession.InitializeFromEnvironment();
        }

        /// <summary>
        /// Declares a member the weaver considered and did not instrument. Emitted so the manifest accounts
        /// for every discovered member, which is what makes the frontier rule sound.
        /// </summary>
        public static void RegisterSkipped(
            int baseOffset,
            string fullName,
            string skipReason,
            string detail,
            string returnKind,
            bool isTestRoot,
            string? filePath,
            int line,
            string sourceResolution)
        {
            lock (s_gate)
            {
                if (!s_coverageByBase.TryGetValue(baseOffset, out AssemblyCoverage coverage))
                {
                    throw new ArgumentOutOfRangeException(nameof(baseOffset));
                }

                coverage.NoteSkippedMember();

                s_members.Add(new ManifestEntry
                {
                    Assembly = coverage.Name,
                    MethodFullName = fullName,
                    Status = PatchStatus.Skipped,
                    SkipReason = skipReason,
                    Detail = detail,
                    FilePath = filePath,
                    Line = line,
                    ReturnKind = returnKind,
                    IsTestRoot = isTestRoot,
                    SourceResolution = sourceResolution,
                });
            }
        }

        /// <summary>Closes every woven assembly's coverage and writes the manifest. Called before writer stats.</summary>
        internal static void WriteManifest(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            CoverageManifest manifest;
            lock (s_gate)
            {
                var assemblies = new System.Collections.Generic.List<AssemblyManifestEntry>(s_assemblies.Length);
                foreach (AssemblyCoverage? coverage in s_assemblies)
                {
                    if (coverage == null)
                    {
                        continue;
                    }

                    // Instrumentation ships inside the IL, so coverage is complete from the first instruction.
                    coverage.MarkComplete(patchedAtMs: 0, afterStartup: false, scanned: true);
                    assemblies.Add(coverage.ToManifestEntry());
                }

                var unruled = new System.Collections.Generic.List<UnruledEnumerableEntry>();
                foreach (System.Collections.Generic.KeyValuePair<string, long> entry in DigestStatistics.UnruledEnumerables())
                {
                    unruled.Add(new UnruledEnumerableEntry { TypeName = entry.Key, Count = entry.Value });
                }

                manifest = new CoverageManifest
                {
                    Metadata = new RunMetadataEntry
                    {
                        Schema = TraceFormat.Schema,
                        Language = TraceFormat.DotNetLanguage,
                    },
                    Assemblies = assemblies,
                    Members = new System.Collections.Generic.List<ManifestEntry>(s_members),
                    UnruledEnumerables = unruled,
                    DigestStats = new DigestStatsEntry
                    {
                        ValuesDigested = DigestStatistics.ValuesDigested,
                        DepthLimited = DigestStatistics.DepthLimited,
                        Blocklisted = DigestStatistics.Blocklisted,
                        Errored = DigestStatistics.Errored,
                        RenderedTruncated = DigestStatistics.RenderedTruncated,
                    },
                };
            }

            ManifestFile.Write(path, manifest);
        }

        /// <summary>Method prologue. The returned frame must be stored in a local that survives into the handler.</summary>
        public static object? Enter(int index, object[] args)
        {
            MethodTraceInfo?[] descriptors = s_descriptors;
            if ((uint)index >= (uint)descriptors.Length)
            {
                return null;
            }

            MethodTraceInfo? info = descriptors[index];
            return info == null ? null : TraceSession.BeginCall(info, args);
        }

        /// <summary>Normal return from a value-returning method.</summary>
        public static void ExitValue(object? frame, object? result)
        {
            if (frame is CallFrame call)
            {
                TraceSession.CompleteSync(call, TraceSession.Render(result));
                TraceSession.EndCall(call, exception: null);
            }
            else
            {
                NoteLostFrame(frame);
            }
        }

        /// <summary>Normal return from a void method.</summary>
        public static void ExitVoid(object? frame)
        {
            if (frame is CallFrame call)
            {
                TraceSession.CompleteSync(call, result: null);
                TraceSession.EndCall(call, exception: null);
            }
            else
            {
                NoteLostFrame(frame);
            }
        }

        /// <summary>Escaping exception. No return value is recorded, matching the Harmony finalizer.</summary>
        public static void ExitException(object? frame, Exception exception)
        {
            if (frame is CallFrame call)
            {
                TraceSession.EndCall(call, exception);
            }
            else
            {
                NoteLostFrame(frame);
            }
        }

        /// <summary>
        /// Async return. The task has not completed, so emission defers to a continuation; the frame is
        /// captured by that continuation and outlives the method. Order mirrors Harmony exactly: the
        /// postfix attaches first, then the finalizer runs and sees the deferral flag already set.
        /// </summary>
        public static void ExitTask(object? frame, System.Threading.Tasks.Task? result)
        {
            if (frame is not CallFrame call)
            {
                NoteLostFrame(frame);
                return;
            }

            TraceSession.AttachContinuation(call, result, resultRenderer: null);
            TraceSession.EndCall(call, exception: null);
        }

        /// <inheritdoc cref="ExitTask" />
        public static void ExitTaskOf<T>(object? frame, System.Threading.Tasks.Task<T>? result)
        {
            if (frame is not CallFrame call)
            {
                NoteLostFrame(frame);
                return;
            }

            TraceSession.AttachContinuation(
                call,
                result,
                static completed => TraceSession.Render(((System.Threading.Tasks.Task<T>)completed).Result));
            TraceSession.EndCall(call, exception: null);
        }

        /// <summary>
        /// A ValueTask backed by an IValueTaskSource may be consumed once. AsTask performs that single
        /// consumption and the caller is handed a ValueTask over the resulting Task, which is safe to await
        /// repeatedly. Returns the replacement rather than taking a ref, which keeps the call site simple.
        /// </summary>
        public static System.Threading.Tasks.ValueTask ExitValueTask(
            object? frame,
            System.Threading.Tasks.ValueTask result)
        {
            if (frame is not CallFrame call)
            {
                NoteLostFrame(frame);
                return result;
            }

            System.Threading.Tasks.Task task = result.AsTask();
            TraceSession.AttachContinuation(call, task, resultRenderer: null);
            TraceSession.EndCall(call, exception: null);
            return new System.Threading.Tasks.ValueTask(task);
        }

        /// <inheritdoc cref="ExitValueTask" />
        public static System.Threading.Tasks.ValueTask<T> ExitValueTaskOf<T>(
            object? frame,
            System.Threading.Tasks.ValueTask<T> result)
        {
            if (frame is not CallFrame call)
            {
                NoteLostFrame(frame);
                return result;
            }

            System.Threading.Tasks.Task<T> task = result.AsTask();
            TraceSession.AttachContinuation(
                call,
                task,
                static completed => TraceSession.Render(((System.Threading.Tasks.Task<T>)completed).Result));
            TraceSession.EndCall(call, exception: null);
            return new System.Threading.Tasks.ValueTask<T>(task);
        }

        /// <summary>
        /// A null frame is legitimate when the tracer is not running; anything else means the weaver emitted
        /// a prologue whose result did not reach the epilogue.
        /// </summary>
        private static void NoteLostFrame(object? frame)
        {
            if (frame != null)
            {
                System.Threading.Interlocked.Increment(ref s_lostFrames);
            }
        }

        internal static AssemblyCoverage[] Assemblies()
        {
            lock (s_gate)
            {
                var live = new System.Collections.Generic.List<AssemblyCoverage>(s_assemblies.Length);
                foreach (AssemblyCoverage? coverage in s_assemblies)
                {
                    if (coverage != null)
                    {
                        live.Add(coverage);
                    }
                }

                return live.ToArray();
            }
        }
    }
}
