using System;
using System.Collections.Generic;
using System.Linq;

namespace Infrastructure.Collections
{
    public static class SortingExtensions
    {
        public static List<T> ByPriority<T>(
            this IEnumerable<T> src,
            Func<T, int> key)
        {
            var list = src.ToList();
            list.Sort((a, b) => key(a).CompareTo(key(b)));
            return list;
        }
    }
}
