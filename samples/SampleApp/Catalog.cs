using System;
using System.Collections.Generic;

namespace SampleApp
{
    /// <summary>Sync class: one plain query, one that throws for unknown input.</summary>
    public sealed class Catalog
    {
        private readonly Dictionary<string, decimal> _prices = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["WIDGET"] = 9.99m,
            ["GADGET"] = 24.50m,
            ["SPROCKET"] = 3.25m,
        };

        /// <summary>Property getter: must be skipped.</summary>
        public int Count => _prices.Count;

        /// <summary>Expression-bodied and tiny, so the JIT would inline it without DOTNET_JitNoInline=1.</summary>
        public bool IsKnown(string sku)
        {
            return _prices.ContainsKey(sku);
        }

        /// <summary>Contains a capturing local function, which the compiler emits as a [CompilerGenerated] method on this type.</summary>
        public IReadOnlyList<string> SkusUnder(decimal limit)
        {
            var matches = new List<string>();
            foreach (KeyValuePair<string, decimal> pair in _prices)
            {
                if (IsUnder(pair.Value))
                {
                    matches.Add(pair.Key);
                }
            }

            matches.Sort(StringComparer.Ordinal);
            return matches;

            bool IsUnder(decimal price) => price < limit;
        }

        public decimal UnitPrice(string sku)
        {
            if (!_prices.TryGetValue(sku, out decimal price))
            {
                throw new KeyNotFoundException("Unknown sku: " + sku);
            }

            return price;
        }
    }
}
