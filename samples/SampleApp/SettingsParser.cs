using System;
using System.Globalization;

namespace SampleApp
{
    /// <summary>
    /// Reads a settings string and applies it. Returns void and writes through a static field, so its own
    /// trace event is identical whether or not the default below changed - which is what makes it a
    /// realistic config-parser shape rather than a value the diff can see directly.
    /// </summary>
    public static class SettingsParser
    {
        private const decimal DefaultFreeShippingThreshold = 50m;

        /// <summary>
        /// Parsing is inline rather than in a helper on purpose: a private helper is patched like any
        /// other member, and its changed return would put a diverging node in this file. Inline, the only
        /// traced member here takes an unchanged argument and returns void, so the edited file leaves no
        /// footprint in the trace at all.
        /// </summary>
        public static void Apply(string raw)
        {
            decimal threshold = DefaultFreeShippingThreshold;

            if (raw != null)
            {
                foreach (string part in raw.Split(';'))
                {
                    int equals = part.IndexOf('=');
                    if (equals <= 0)
                    {
                        continue;
                    }

                    if (!string.Equals(part.Substring(0, equals).Trim(), "freeShipping", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (decimal.TryParse(part.Substring(equals + 1).Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed))
                    {
                        threshold = parsed;
                        break;
                    }
                }
            }

            ShippingSettings.FreeShippingThreshold = threshold;
        }
    }
}
