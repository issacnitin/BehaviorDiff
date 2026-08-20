using System;
using System.Collections.Generic;

namespace SampleApp
{
    /// <summary>Generic method definition: cannot be patched, because the postfix must be closed over the return type.</summary>
    public static class Sequences
    {
        public static int CountMatching<T>(IReadOnlyList<T> items, Func<T, bool> predicate)
        {
            int count = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (predicate(items[i]))
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Generic type definition: every member is unpatchable for the same reason.</summary>
    public sealed class PriceCache<TKey>
        where TKey : notnull
    {
        private readonly Dictionary<TKey, decimal> _entries = new Dictionary<TKey, decimal>();

        public void Put(TKey key, decimal value)
        {
            _entries[key] = value;
        }

        public bool TryGet(TKey key, out decimal value)
        {
            return _entries.TryGetValue(key, out value);
        }
    }
}
