using RealDiff.Tracer;
using SampleApp;
using Xunit;

namespace SampleApp.Tests
{
    [TraceTest]
    public sealed class PermissionTests
    {
        [Fact]
        public void Default_permission_always_produces_a_decision()
        {
            PermissionDefaultsParser.Apply("region=eu");

            bool recognized = new PermissionEvaluator().HasRecognizedDecision();

            Assert.True(recognized);
        }
    }
}
