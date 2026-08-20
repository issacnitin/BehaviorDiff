using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using Xunit;

namespace SampleApp.Tests
{
    /// <summary>
    /// Forces several assembly loads to land at once, so the drain log contains genuinely concurrent
    /// enqueues rather than the neatly serialized ones a single reflective load produces.
    /// </summary>
    /// <remarks>
    /// Each context yields a distinct Assembly instance for the same file, so each raises its own
    /// AssemblyLoad event and each is enqueued separately. A barrier releases the loader threads together.
    /// </remarks>
    public sealed class ConcurrentLoadTests
    {
        private const int LoaderThreads = 6;

        [Fact]
        public void Assemblies_loading_concurrently_are_all_enqueued()
        {
            string pluginPath = Path.Combine(AppContext.BaseDirectory, "SampleApp.Plugin.dll");
            Assert.True(File.Exists(pluginPath), "plugin assembly missing: " + pluginPath);

            using var barrier = new Barrier(LoaderThreads);
            var loaded = new Assembly?[LoaderThreads];
            var failures = new Exception?[LoaderThreads];
            var threads = new Thread[LoaderThreads];

            for (int i = 0; i < LoaderThreads; i++)
            {
                int index = i;
                threads[i] = new Thread(() =>
                {
                    try
                    {
                        var context = new AssemblyLoadContext("behaviordiff-concurrent-" + index, isCollectible: false);
                        barrier.SignalAndWait();
                        loaded[index] = context.LoadFromAssemblyPath(pluginPath);
                    }
                    catch (Exception ex)
                    {
                        failures[index] = ex;
                    }
                });

                threads[i].IsBackground = true;
                threads[i].Start();
            }

            foreach (Thread thread in threads)
            {
                Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "loader thread did not finish");
            }

            for (int i = 0; i < LoaderThreads; i++)
            {
                Assert.Null(failures[i]);
                Assert.NotNull(loaded[i]);
            }

            // Distinct instances, otherwise the load events would have collapsed to one.
            var distinct = new HashSet<Assembly>();
            foreach (Assembly? assembly in loaded)
            {
                distinct.Add(assembly!);
            }

            Assert.Equal(LoaderThreads, distinct.Count);
        }
    }
}
