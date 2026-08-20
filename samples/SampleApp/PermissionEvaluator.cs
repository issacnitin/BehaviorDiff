namespace SampleApp
{
    /// <summary>Consumes permission defaults from another file and is never edited by the demo.</summary>
    public sealed class PermissionEvaluator
    {
        public bool CanRead()
        {
            return PermissionSettings.DefaultRole != "None";
        }

        public string AccessLabel()
        {
            return CanRead() ? "read allowed" : "read denied";
        }

        public bool HasRecognizedDecision()
        {
            return AccessLabel().StartsWith("read ", System.StringComparison.Ordinal);
        }
    }
}
