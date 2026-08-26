using RealDiff.Tracer;
using Commerce.Pricing;
using Xunit;

namespace SampleApp.Tests
{
    [TraceTest]
    public sealed class DiscountOrderingTests
    {
        [Fact]
        public void Discount_is_applied()
        {
            var checkout = new CheckoutTotals();

            decimal total = checkout.Compute(100m);

            Assert.True(total < 100m);
        }

        [Fact]
        public void Total_is_never_above_list_price()
        {
            var checkout = new CheckoutTotals();

            decimal total = checkout.Compute(100m);

            Assert.True(total <= 100m);
        }

        [Fact]
        public void Clearance_discount_wins_current_ties()
        {
            var discounts = new DiscountEngine();

            string selected = discounts.SelectDiscount(100m);

            Assert.Equal("CLEARANCE_40", selected);
        }
    }
}