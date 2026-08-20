using System.Globalization;

namespace SampleApp.Plugin
{
    /// <summary>
    /// Reached only through reflection, so this assembly is not loaded until a test runs. It therefore
    /// arrives after startup enumeration and is recorded as LatePatched.
    /// </summary>
    public sealed class LateBoundFormatter
    {
        public string Format(decimal amount)
        {
            return amount.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }
}
