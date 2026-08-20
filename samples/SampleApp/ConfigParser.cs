using System.Collections.Generic;
using System.Globalization;

namespace SampleApp
{
    public static class ConfigParser
    {
        public static void Apply(IReadOnlyDictionary<string, string> raw)
        {
            // payment-config.json omits max_attempts; the indexer resolves the inherited value of 10.
            RetrySettings.MaxAttempts = int.Parse(raw["max_attempts"], CultureInfo.InvariantCulture);
        }
    }
}