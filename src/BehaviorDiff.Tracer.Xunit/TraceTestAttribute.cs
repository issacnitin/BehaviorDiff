using System;
using System.Reflection;
using System.Text;
using Xunit.Sdk;

namespace BehaviorDiff.Tracer
{
    /// <summary>
    /// Stamps every trace event produced during a test with that test's identity.
    /// Apply to a test class or an individual test method, or register it assembly-wide.
    /// </summary>
    /// <remarks>
    /// xunit invokes <see cref="Before"/> synchronously on the same logical flow that then runs the test
    /// body, so writing to the <see cref="TraceSession.CurrentTestId"/> <c>AsyncLocal</c> here makes the id
    /// visible to the test and to anything it awaits or spawns, including work that resumes on another
    /// thread. That is what allows tests to run in parallel without their events being mixed up.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class TraceTestAttribute : BeforeAfterTestAttribute
    {
        public override void Before(MethodInfo methodUnderTest)
        {
            if (methodUnderTest is null)
            {
                throw new ArgumentNullException(nameof(methodUnderTest));
            }

            // Fallback for assemblies without a module initializer. Patching earlier is better; see the
            // sample's TraceBootstrap.
            //
            // This deliberately does not live in a static constructor. Installation patches assemblies,
            // which JITs and loads more assemblies, and a cctor runs under the CLR's type-initialization
            // lock: with xunit constructing this attribute on several threads at once, one thread ends up
            // inside the cctor doing that work while the others block on the type-init lock, and the JIT
            // work the first thread needs can require a lock one of the blocked threads holds. The result
            // is a hang at startup with no output, which is what happened when test parallelism was raised.
            TraceSession.InitializeFromEnvironment();

            if (SuppressNaming)
            {
                return;
            }

            TraceSession.CurrentTestId = BuildTestId(methodUnderTest);
        }

        public override void After(MethodInfo methodUnderTest)
        {
            if (SuppressNaming)
            {
                return;
            }

            TraceSession.CurrentTestId = null;
        }

        /// <summary>Lets the same binary be run with framework naming and with woven naming, to compare them.</summary>
        private static bool SuppressNaming =>
            string.Equals(
                Environment.GetEnvironmentVariable("BEHAVIORDIFF_CORRELATION"),
                "woven",
                StringComparison.OrdinalIgnoreCase);

        private static string BuildTestId(MethodInfo methodUnderTest)
        {
            var builder = new StringBuilder(96);
            Type? declaring = methodUnderTest.DeclaringType;
            if (declaring != null)
            {
                builder.Append(declaring.FullName).Append('.');
            }

            return builder.Append(methodUnderTest.Name).ToString();
        }
    }
}
