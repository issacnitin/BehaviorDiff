namespace SampleApp
{
    /// <summary>Applies permission defaults. Its trace stays identical when the default changes.</summary>
    public static class PermissionDefaultsParser
    {
        private const string DefaultRole = "Reader";

        public static void Apply(string raw)
        {
            string role = DefaultRole;
            if (raw.StartsWith("role=", System.StringComparison.Ordinal))
            {
                role = raw.Substring("role=".Length);
            }

            PermissionSettings.DefaultRole = role;
        }
    }
}
