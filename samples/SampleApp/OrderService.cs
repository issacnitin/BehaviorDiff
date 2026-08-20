using System;
using System.Threading.Tasks;

namespace SampleApp
{
    /// <summary>Async class returning Task&lt;T&gt;, with both a value path and a fault path after the await.</summary>
    public sealed class OrderService
    {
        private readonly Catalog _catalog;
        private readonly InventoryClient _inventory;

        public OrderService(Catalog catalog, InventoryClient inventory)
        {
            _catalog = catalog;
            _inventory = inventory;
        }

        public async Task<decimal> QuoteAsync(string sku, int quantity)
        {
            if (quantity <= 0)
            {
                // Thrown from an async method, so it surfaces as a faulted Task rather than synchronously.
                throw new ArgumentOutOfRangeException(nameof(quantity));
            }

            bool inStock = await _inventory.IsInStockAsync(sku).ConfigureAwait(false);
            if (!inStock)
            {
                throw new InvalidOperationException("Out of stock: " + sku);
            }

            decimal unit = _catalog.UnitPrice(sku);
            return ApplyDiscount(unit * quantity, quantity);
        }

        public decimal ApplyDiscount(decimal amount, int quantity)
        {
            return quantity >= 10 ? decimal.Round(amount * 0.9m, 2) : amount;
        }
    }
}
