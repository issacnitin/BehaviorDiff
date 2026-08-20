using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BehaviorDiff.Tracer;
using SampleApp;
using Xunit;

namespace SampleApp.Tests
{
    /// <summary>
    /// Breadth, not depth. Each test method is a distinct TestId, so every subject method it touches
    /// becomes a distinct match key - the engine's volume precondition needs enough keys for a clean
    /// result to carry information.
    /// </summary>
    [TraceTest]
    public sealed class CoverageBreadthTests
    {
        private static OrderService NewService()
        {
            var catalog = new Catalog();
            return new OrderService(catalog, new InventoryClient(catalog));
        }

        [Fact]
        public async Task Quote_widget_single_unit()
        {
            Assert.Equal(9.99m, await NewService().QuoteAsync("WIDGET", 1));
        }

        [Fact]
        public async Task Quote_widget_at_discount_boundary()
        {
            Assert.Equal(89.91m, await NewService().QuoteAsync("WIDGET", 10));
        }

        [Fact]
        public async Task Quote_gadget_bulk()
        {
            Assert.Equal(551.25m, await NewService().QuoteAsync("GADGET", 25));
        }

        [Fact]
        public void Discount_applies_only_above_threshold()
        {
            var service = NewService();

            Assert.Equal(100m, service.ApplyDiscount(100m, 9));
            Assert.Equal(90m, service.ApplyDiscount(100m, 10));
        }

        [Fact]
        public void Catalog_knows_its_own_skus()
        {
            var catalog = new Catalog();

            Assert.True(catalog.IsKnown("WIDGET"));
            Assert.False(catalog.IsKnown("NOPE"));
        }

        [Fact]
        public void Catalog_price_cap_excludes_expensive_skus()
        {
            Assert.NotEmpty(new Catalog().SkusUnder(50m));
        }

        [Fact]
        public void Inventory_reserves_by_sku_length()
        {
            var catalog = new Catalog();

            Assert.True(new InventoryClient(catalog).ReserveCount("WIDGET") > 0);
        }

        [Fact]
        public void Pricing_rules_apply_coupon_tiers()
        {
            Assert.Equal(9.99m, PricingRules.Price(new Quote("WIDGET", new Coupon("SAVE10", tier: 1, percent: 10m)), 9.99m));
            Assert.Equal(49.00m, PricingRules.Price(new Quote("GADGET", new Coupon("SAVE25", tier: 2, percent: 25m)), 49m));
        }

        [Fact]
        public void Price_cache_round_trips_a_value()
        {
            var cache = new PriceCache<string>();
            cache.Put("WIDGET", 9.99m);

            Assert.True(cache.TryGet("WIDGET", out decimal value));
            Assert.Equal(9.99m, value);
        }

        [Fact]
        public void Price_cache_misses_report_default()
        {
            Assert.False(new PriceCache<string>().TryGet("ABSENT", out decimal value));
            Assert.Equal(0m, value);
        }

        [Fact]
        public void Collections_of_every_shape_are_digested()
        {
            Assert.Equal(6, Probes.BuildDictionaryWithRemovals().Count);
            Assert.Equal(8, Probes.BuildSetWithRemovals().Count);
        }

        [Fact]
        public void Nested_graphs_reach_the_depth_limiter()
        {
            Assert.Equal(1, Probes.Descend(DeepNode.Build(7)));
        }

        [Fact]
        public void Blocklisted_shapes_are_not_walked()
        {
            Assert.Equal(1, Probes.UseServices(new ServiceHolder("breadth")));
        }

        [Fact]
        public void Cyclic_graphs_terminate()
        {
            Assert.Equal(1, Probes.Traverse(Cyclic.Loop("breadth")));
        }

        [Fact]
        public void Unknown_sku_still_throws()
        {
            Assert.Throws<KeyNotFoundException>(() => new Catalog().UnitPrice("MISSING"));
        }

        [Fact]
        public async Task Out_of_stock_still_faults()
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => NewService().QuoteAsync("SPROCKET", 2));
        }
    }
}
