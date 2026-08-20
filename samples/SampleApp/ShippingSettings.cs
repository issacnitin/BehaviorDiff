namespace SampleApp
{
    /// <summary>
    /// Applied configuration. A plain field, not a property, so reading it emits no trace event of its
    /// own - the value crosses the file boundary without leaving a footprint in the edited file.
    /// </summary>
    public static class ShippingSettings
    {
        public static decimal FreeShippingThreshold = 50m;
    }
}
