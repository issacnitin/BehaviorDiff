namespace SampleApp
{
    public enum AccountStatus
    {
        Pending = 0,
        Active = 1,
        Suspended = 2,
        Closed = 3,
    }

    public static class AccountStatusStorage
    {
        public static void ObserveRead(int storedStatus)
        {
        }
    }
}