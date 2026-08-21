using System;
using System.Collections.Generic;
using BehaviorDiff.Tracer;
using SampleApp;
using Xunit;

namespace SampleApp.Tests
{
    /// <summary>
    /// Each test drives one canonicalizer property through the real tracer. Assertions here only keep the
    /// suite honest; the evidence is the emitted NDJSON, checked by tools/trace-sample.ps1.
    /// </summary>
    [TraceTest]
    public sealed class DigestProofTests
    {
        [Fact]
        public void Proof1_digestion_invokes_no_overridable_member()
        {
            SideEffectProbe.Calls.Clear();

            Assert.Equal(1, Probes.Inspect(new SideEffectProbe(42)));

            // If the digest had touched the getter, ToString, Equals, GetHashCode or GetEnumerator,
            // this would be non-empty. The traced returnRendered of ObservedCalls carries the proof.
            Assert.Equal(string.Empty, Probes.ObservedCalls());
        }

        [Fact]
        public void Proof2_cyclic_graph_terminates()
        {
            Assert.Equal(1, Probes.Traverse(Cyclic.Loop("a")));
            Assert.Equal(1, Probes.Traverse(Cyclic.Loop("a")));
        }

        [Fact]
        public void Proof4_shared_node_differs_from_two_equal_copies()
        {
            var shared = new Coupon("SAVE10", tier: 1, percent: 10m);

            Assert.Equal(1, Probes.Relate(new Pair(shared, shared)));
            Assert.Equal(1, Probes.Relate(new Pair(
                new Coupon("SAVE10", tier: 1, percent: 10m),
                new Coupon("SAVE10", tier: 1, percent: 10m))));
        }

        [Fact]
        public void Proof5_same_input_digests_identically_within_and_across_runs()
        {
            // Reference-type keys hash by identity, which varies per process; the shape rule sorts.
            Assert.Equal(6, Probes.BuildDictionaryWithRemovals().Count);
            Assert.Equal(8, Probes.BuildSetWithRemovals().Count);
        }

        [Fact]
        public void Proof6_guid_and_datetime_are_normalized_away()
        {
            Assert.Equal(1, Probes.Stamp(new Stamped(Guid.NewGuid(), DateTime.UtcNow, "fixed")));
            Assert.Equal(1, Probes.Stamp(new Stamped(Guid.NewGuid(), DateTime.UtcNow.AddDays(-3), "fixed")));
        }

        [Fact]
        public void Proof7_blocklisted_shapes_are_skipped_not_walked()
        {
            Assert.Equal(1, Probes.UseServices(new ServiceHolder("holder")));
        }

        [Fact]
        public void Depth_limiter_fires_on_a_graph_past_the_cap()
        {
            // Height 9 binary tree: 64 nodes sit exactly at the depth-6 boundary.
            Assert.Equal(1, Probes.Descend(DeepNode.Build(9)));
        }

        [Fact]
        public void Rendered_text_is_truncated_while_the_hash_covers_the_whole_value()
        {
            string a = Probes.LongText(3000);
            string b = Probes.LongText(3000);
            string hiddenA = Probes.LongTextWithHiddenSuffix('a');
            string hiddenB = Probes.LongTextWithHiddenSuffix('b');

            Assert.Equal(3000, a.Length);
            Assert.Equal(a, b);
            Assert.NotEqual(hiddenA, hiddenB);
        }

        [Fact]
        public void A_field_that_cannot_be_read_is_marked_not_omitted()
        {
            // Same generic type and field names in all three, so the payload is the only difference.
            Assert.Equal(1, ErrorProbes.Readable(new Wrapper<int>("same", 7)));
            Assert.Equal(1, ErrorProbes.Unreadable(new Wrapper<Unreadable>("same", default)));
            Assert.Equal(1, ErrorProbes.UnreadableOther(new Wrapper<AlsoUnreadable>("same", default)));
        }
    }
}
