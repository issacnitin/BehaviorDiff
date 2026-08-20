namespace SampleApp
{
    /// <summary>Consumes the applied configuration. Never edited by the config-parser proof.</summary>
    public sealed class ShippingCalculator
    {
        private const decimal FlatRate = 4.99m;

        public bool IsFreeShipping(decimal orderTotal)
        {
            return orderTotal >= ShippingSettings.FreeShippingThreshold;
        }

        public decimal ShippingCost(decimal orderTotal)
        {
            return IsFreeShipping(orderTotal) ? 0m : FlatRate;
        }

        public decimal TotalWithShipping(decimal orderTotal)
        {
            return orderTotal + ShippingCost(orderTotal);
        }
    }
}
