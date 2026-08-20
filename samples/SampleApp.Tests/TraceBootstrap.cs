using System.Runtime.CompilerServices;
using BehaviorDiff.Tracer;

namespace SampleApp.Tests
{
    internal static class TraceBootstrap
    {
        /// <summary>
        /// Patches before any test runs. Test collections execute in parallel, so relying on a static
        /// constructor that fires on first attribute use would leave a window where some calls are already
        /// executing unpatched. The tracer also hooks AssemblyLoad, so the target assembly does not have to
        /// be loaded yet at this point.
        /// </summary>
        [ModuleInitializer]
        internal static void Initialize()
        {
            TraceSession.InitializeFromEnvironment();
        }
    }
}
