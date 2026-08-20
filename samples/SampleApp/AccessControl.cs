namespace SampleApp
{
    /// <summary>Applies withdrawal policy to the status decoded from durable storage.</summary>
    public sealed class AccessControl
    {
        public bool CanWithdraw(AccountStatus status)
        {
            return status != AccountStatus.Suspended
                && status != AccountStatus.Closed;
        }
    }
}