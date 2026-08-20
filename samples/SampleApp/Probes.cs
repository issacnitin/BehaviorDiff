using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;

namespace SampleApp
{
    /// <summary>
    /// Every overridable member records that it ran. After digestion this list must still be empty:
    /// the canonicalizer reads fields and must never invoke a member the target can define.
    /// </summary>
    public sealed class SideEffectProbe : IEnumerable<int>
    {
        public static readonly List<string> Calls = new List<string>();

        private readonly int _value;

        public SideEffectProbe(int value)
        {
            _value = value;
        }

        public int Value
        {
            get
            {
                Calls.Add("get_Value");
                return _value;
            }
        }

        public override string ToString()
        {
            Calls.Add("ToString");
            return "probe";
        }

        public override bool Equals(object? obj)
        {
            Calls.Add("Equals");
            return ReferenceEquals(this, obj);
        }

        public override int GetHashCode()
        {
            Calls.Add("GetHashCode");
            return 17;
        }

        public IEnumerator<int> GetEnumerator()
        {
            Calls.Add("GetEnumerator");
            yield return _value;
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            Calls.Add("GetEnumerator");
            return GetEnumerator();
        }
    }

    /// <summary>Binary tree. Built past the depth cap so the limiter fires on every node at the boundary.</summary>
    public sealed class DeepNode
    {
        private readonly int _value;
        private readonly DeepNode? _left;
        private readonly DeepNode? _right;

        private DeepNode(int value, DeepNode? left, DeepNode? right)
        {
            _value = value;
            _left = left;
            _right = right;
        }

        public static DeepNode Build(int height)
        {
            return height <= 0 ? new DeepNode(0, null, null) : new DeepNode(height, Build(height - 1), Build(height - 1));
        }
    }

    /// <summary>Holds exactly the shapes the blocklist must refuse to walk into.</summary>
    public sealed class ServiceHolder
    {
        private readonly NullLogger _logger = NullLogger.Instance;
        private readonly Stream _stream = new MemoryStream();
        private readonly CancellationToken _token = CancellationToken.None;
        private readonly Task _work = Task.CompletedTask;
        private readonly Func<int, int> _callback = static x => x;
        private readonly Type _type = typeof(ServiceHolder);
        private readonly string _name;

        public ServiceHolder(string name)
        {
            _name = name;
        }
    }

    /// <summary>A Guid and a DateTime differ on every run; normalization must erase both.</summary>
    public sealed class Stamped
    {
        private readonly Guid _id;
        private readonly DateTime _at;
        private readonly string _name;

        public Stamped(Guid id, DateTime at, string name)
        {
            _id = id;
            _at = at;
            _name = name;
        }
    }

    /// <summary>Two slots, so a shared node and two equal copies can be told apart.</summary>
    public sealed class Pair
    {
        private readonly object _a;
        private readonly object _b;

        public Pair(object a, object b)
        {
            _a = a;
            _b = b;
        }
    }

    /// <summary>Self-referential; digestion must terminate.</summary>
    public sealed class Cyclic
    {
        private readonly string _name;
        private Cyclic? _next;

        public Cyclic(string name)
        {
            _name = name;
        }

        public static Cyclic Loop(string name)
        {
            var first = new Cyclic(name);
            var second = new Cyclic(name + "-2");
            first._next = second;
            second._next = first;
            return first;
        }
    }

    /// <summary>Traced entry points. Each exists so a fixture reaches the canonicalizer through the real pipeline.</summary>
    public static class Probes
    {
        public static int Inspect(SideEffectProbe probe)
        {
            return probe is null ? 0 : 1;
        }

        public static string ObservedCalls()
        {
            return string.Join(",", SideEffectProbe.Calls);
        }

        public static int Descend(DeepNode root)
        {
            return root is null ? 0 : 1;
        }

        public static int UseServices(ServiceHolder holder)
        {
            return holder is null ? 0 : 1;
        }

        public static int Stamp(Stamped stamped)
        {
            return stamped is null ? 0 : 1;
        }

        public static int Relate(Pair pair)
        {
            return pair is null ? 0 : 1;
        }

        public static int Traverse(Cyclic node)
        {
            return node is null ? 0 : 1;
        }

        public static string LongText(int length)
        {
            var builder = new StringBuilder(length);
            for (int i = 0; i < length; i++)
            {
                builder.Append((char)('a' + (i % 26)));
            }

            return builder.ToString();
        }

        /// <summary>Entries are removed so the free list is populated, which is what the shape rule must skip.</summary>
        public static Dictionary<string, int> BuildDictionaryWithRemovals()
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < 12; i++)
            {
                map["k" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = i;
            }

            for (int i = 0; i < 12; i += 2)
            {
                map.Remove("k" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            return map;
        }

        public static HashSet<string> BuildSetWithRemovals()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < 10; i++)
            {
                set.Add("s" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            set.Remove("s1");
            set.Remove("s3");
            return set;
        }
    }
}
