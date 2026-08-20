using System.Runtime.CompilerServices;
using BehaviorDiff.Tracer;
using SampleApp.NoPdb;
using Xunit;

namespace SampleApp.NoPdb.Tests
{
    internal static class TraceBootstrap
    {
        [ModuleInitializer]
        internal static void Initialize()
        {
            TraceSession.InitializeFromEnvironment();
        }
    }

    [TraceTest]
    public sealed class UnattributableTests
    {
        [Fact]
        public void Calls_are_traced_but_cannot_be_attributed_to_a_file()
        {
            var subject = new Unattributable(3);

            Assert.Equal(12, subject.Scale(4));
            Assert.Equal(7, subject.Offset(4));
        }
    }
}
