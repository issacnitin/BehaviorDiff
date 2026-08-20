namespace Commerce.Pricing
{
    public sealed class CheckoutTotals
    {
        private readonly DiscountEngine _discounts = new DiscountEngine();

        public decimal Compute(decimal listPrice)
        {
            string discount = _discounts.SelectDiscount(listPrice);
            return discount == "SEASONAL_15"
                ? listPrice * 0.85m
                : listPrice * 0.60m;
        }
    }
}