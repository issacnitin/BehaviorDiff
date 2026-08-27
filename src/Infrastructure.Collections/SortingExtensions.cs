using System.Collections.Generic;
using System.Linq;

namespace Infrastructure.Collections
{
    public static class SortingExtensions
    {
        public static List<(int Priority, T Value)> ByPriority<T>(
            this IEnumerable<(int Priority, T Value)> src)
        {
            var list = src.ToList();
            list.Sort((a, b) => a.Priority.CompareTo(b.Priority));
            return list;
        }
    }
}
