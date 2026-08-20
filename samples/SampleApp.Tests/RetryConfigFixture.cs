using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PaymentFixture
{
    internal sealed class RetryConfigFixture : IReadOnlyDictionary<string, string>
    {
        private const string InheritedMaxAttempts = "10";
        private readonly IReadOnlyDictionary<string, string> _values;

        private RetryConfigFixture(IReadOnlyDictionary<string, string> values)
        {
            _values = values;
        }

        public string this[string key] => _values.TryGetValue(key, out string? value)
            ? value
            : string.Equals(key, "max_attempts", StringComparison.Ordinal)
                ? InheritedMaxAttempts
                : throw new KeyNotFoundException(key);

        public IEnumerable<string> Keys => _values.Keys;

        public IEnumerable<string> Values => _values.Values;

        public int Count => _values.Count;

        public static RetryConfigFixture Load(string path)
        {
            Dictionary<string, string>? values = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(path));
            return new RetryConfigFixture(values ?? throw new InvalidDataException("Retry config is empty."));
        }

        public bool ContainsKey(string key) => _values.ContainsKey(key);

        public bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _values.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}