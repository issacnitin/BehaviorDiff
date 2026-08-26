using System;
using System.Reflection;
using System.Threading;
using RealDiff.Tracer;
using SampleApp;
using Xunit;

namespace SampleApp.Tests
{
    /// <summary>
    /// Drives the frontier downgrade reasons. Without these every frontier verdict in the suite is reached
    /// with an empty descendant set, which makes the rule vacuously true.
    /// </summary>
    [TraceTest]
    public sealed class DowngradeTests
    {
        [Fact]
        public void Deep_origin_with_shallow_consequences()
        {
            Assert.Equal(24, DeepChain.LevelOne(7));
        }

        [Fact]
        public void Diverging_parent_over_a_partial_child()
        {
            Assert.Equal(10, PartialSubtree.Parent(7));
        }

        [Fact]
        public void Diverging_node_whose_own_value_is_truncated()
        {
            Assert.Equal(2421, PartialSelf.Build(7).Length);
        }

        [Fact]
        public void Diverging_parent_over_an_uninstrumentable_child()
        {
            Assert.Equal(10, SkippedSubtree.Parent(7));
        }
    }
}
