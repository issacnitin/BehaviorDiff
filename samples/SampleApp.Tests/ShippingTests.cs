using RealDiff.Tracer;
using SampleApp;
using Xunit;

namespace SampleApp.Tests
{
    /// <summary>
    /// The config-parser shape: settings are applied through a parser in one file and consumed by a
    /// calculator in another. Only the parser file is edited by the proof.
    /// </summary>
    [TraceTest]
    public sealed class ShippingTests
    {
        private static ShippingCalculator Configured(string raw)
        {
            SettingsParser.Apply(raw);
            return new ShippingCalculator();
        }

        [Fact]
        public void Order_below_threshold_pays_shipping()
        {
            Assert.Equal(44.99m, Configured("region=eu").TotalWithShipping(40m));
        }

        [Fact]
        public void Order_above_threshold_ships_free()
        {
            Assert.Equal(120m, Configured("region=eu").TotalWithShipping(120m));
        }

        [Fact]
        public void Small_order_always_pays_shipping()
        {
            Assert.Equal(24.99m, Configured("region=eu").TotalWithShipping(20m));
        }

        [Fact]
        public void Explicit_threshold_overrides_the_default()
        {
            Assert.Equal(35m, Configured("freeShipping=30").TotalWithShipping(35m));
        }

        /// <summary>
        /// Deliberately asserts nothing about shipping, so a threshold change alters its behavior without
        /// any assertion reacting. This is the untested-subset fixture.
        /// </summary>
        [Fact]
        public void Totals_are_never_negative()
        {
            Assert.True(Configured("region=eu").TotalWithShipping(45m) > 0m);
        }
    }
}
