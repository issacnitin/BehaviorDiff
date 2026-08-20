using System.Threading.Tasks;

namespace SampleApp
{
    /// <summary>Async class returning ValueTask&lt;T&gt;, which the tracer has to convert before observing.</summary>
    public sealed class InventoryClient
    {
        private readonly Catalog _catalog;

        public InventoryClient(Catalog catalog)
        {
            _catalog = catalog;
        }

        public async ValueTask<bool> IsInStockAsync(string sku)
        {
            // A real suspension point: the Harmony postfix fires here, long before the value is known.
            await Task.Delay(5).ConfigureAwait(false);
            return _catalog.IsKnown(sku) && ReserveCount(sku) > 0;
        }

        public int ReserveCount(string sku)
        {
            return sku.Length > 6 ? 0 : 12;
        }
    }
}
