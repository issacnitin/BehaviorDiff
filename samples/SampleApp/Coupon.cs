using System;

namespace SampleApp
{
    /// <summary>
    /// Two coupons with the same public surface can differ in <c>_tier</c>. Nothing public exposes it, so
    /// a renderer that reads properties or ToString cannot tell them apart.
    /// </summary>
    public sealed class Coupon
    {
        private readonly string _code;
        private readonly int _tier;

        public Coupon(string code, int tier, decimal percent)
        {
            _code = code;
            _tier = tier;
            Percent = percent;
        }

        /// <summary>Auto-property: its backing field is what the digest actually reads.</summary>
        public decimal Percent { get; }

        public override string ToString()
        {
            // Deliberately identical for two coupons differing only in _tier.
            return "Coupon(" + _code + ")";
        }
    }

    /// <summary>A two-level graph, so the digest has to descend to see the difference.</summary>
    public sealed class Quote
    {
        private readonly string _sku;
        private readonly Coupon _coupon;

        public Quote(string sku, Coupon coupon)
        {
            _sku = sku;
            _coupon = coupon;
        }
    }

    public static class PricingRules
    {
        /// <summary>Ignores the coupon tier, so two quotes differing only in it price identically.</summary>
        public static decimal Price(Quote quote, decimal list)
        {
            if (quote is null)
            {
                throw new ArgumentNullException(nameof(quote));
            }

            return decimal.Round(list, 2);
        }
    }
}
