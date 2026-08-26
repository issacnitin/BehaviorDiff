using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using RealDiff.Tracer;
using Xunit;

namespace SampleApp.Tests
{
    [TraceTest]
    public sealed class OrderServiceTests
    {
        private static OrderService NewService()
        {
            var catalog = new Catalog();
            return new OrderService(catalog, new InventoryClient(catalog));
        }

        [Fact]
        public async Task Quote_applies_bulk_discount_above_ten_units()
        {
            decimal quote = await NewService().QuoteAsync("WIDGET", 10);

            Assert.Equal(89.91m, quote);
        }

        [Fact]
        public async Task Quote_without_discount_below_ten_units()
        {
            decimal quote = await NewService().QuoteAsync("GADGET", 2);

            Assert.Equal(49.00m, quote);
        }

        [Fact]
        public async Task Quote_faults_when_inventory_reports_out_of_stock()
        {
            // SPROCKET is eight characters, so ReserveCount returns zero.
            await Assert.ThrowsAsync<InvalidOperationException>(() => NewService().QuoteAsync("SPROCKET", 1));
        }

        [Fact]
        public async Task Quote_faults_on_invalid_quantity_before_any_await()
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => NewService().QuoteAsync("WIDGET", 0));
        }

        [Fact]
        public void Unit_price_throws_for_unknown_sku()
        {
            Assert.Throws<KeyNotFoundException>(() => new Catalog().UnitPrice("NOPE"));
        }

        [Fact]
        public void Catalog_lists_skus_under_a_price_cap()
        {
            var catalog = new Catalog();

            Assert.Equal(3, catalog.Count);
            Assert.Equal(new[] { "SPROCKET", "WIDGET" }, catalog.SkusUnder(10m));
        }

        [Fact]
        public void Generic_members_still_run_even_though_they_cannot_be_traced()
        {
            var cache = new PriceCache<string>();
            cache.Put("WIDGET", 9.99m);

            Assert.True(cache.TryGet("WIDGET", out decimal price));
            Assert.Equal(9.99m, price);
            Assert.Equal(2, Sequences.CountMatching(new[] { 1, 2, 3, 4 }, n => n % 2 == 0));
        }

        [Fact]
        public void Excluded_namespace_still_runs()
        {
            var log = new SampleApp.Diagnostics.RunLog();
            log.Record("hello");

            Assert.Equal(1, log.LineCount());
        }

        [Fact]
        public void Graphs_differing_only_in_a_private_field_price_identically()
        {
            // Same sku, same coupon code, same Percent, same ToString. Only the private _tier differs.
            var lenient = new Quote("WIDGET", new Coupon("SAVE10", tier: 1, percent: 10m));
            var strict = new Quote("WIDGET", new Coupon("SAVE10", tier: 2, percent: 10m));

            Assert.Equal(PricingRules.Price(lenient, 99.90m), PricingRules.Price(strict, 99.90m));
        }

        [Fact]
        public void Reflectively_loaded_assembly_arrives_after_startup()
        {
            // Nothing references this assembly statically, so it is not loaded until right now: the
            // tracer sees it on the load event and patches it from the drain thread, after the call below
            // has already happened.
            Assembly assembly = Assembly.Load("SampleApp.Plugin");
            Type formatter = assembly.GetType("SampleApp.Plugin.LateBoundFormatter", throwOnError: true)
                ?? throw new InvalidOperationException("type not found");

            object instance = Activator.CreateInstance(formatter)
                ?? throw new InvalidOperationException("could not construct");
            MethodInfo format = formatter.GetMethod("Format")
                ?? throw new InvalidOperationException("method not found");

            Assert.Equal("12.50", format.Invoke(instance, new object[] { 12.5m }));
        }
    }
}
