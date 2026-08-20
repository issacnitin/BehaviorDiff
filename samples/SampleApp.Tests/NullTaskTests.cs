using SampleApp;
using Xunit;

namespace SampleApp.Tests
{
    /// <summary>
    /// Regression fixture for the null-Task path. The assertion that matters is not here but in
    /// tools/verify-null-task.ps1, which counts the emitted events: exactly one per call, not two.
    /// </summary>
    public sealed class NullTaskTests
    {
        [Fact]
        public void Null_task_returns_null()
        {
            var probe = new NullTaskProbe();
            Assert.Null(probe.NeverStarted());
        }

        [Fact]
        public void Null_task_of_t_returns_null()
        {
            var probe = new NullTaskProbe();
            Assert.Null(probe.NeverStartedOfT());
        }
    }
}
