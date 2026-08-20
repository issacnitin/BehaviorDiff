namespace SampleApp
{
    /// <summary>
    /// The only file the downgrade proof edits. It declares no methods, so it contributes no trace events
    /// of its own - the changed value shows up at the use sites in other files.
    /// </summary>
    public static class DowngradeConfig
    {
        public const int Magnitude = 3;
    }
}
