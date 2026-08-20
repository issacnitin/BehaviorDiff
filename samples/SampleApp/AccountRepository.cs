namespace SampleApp.Persistence
{
    public sealed class AccountRepository
    {
        private int _storedStatus;

        public AccountRepository(int storedStatus)
        {
            _storedStatus = storedStatus;
        }

        public void Save(AccountStatus status)
        {
            _storedStatus = (int)status;
        }

        public AccountStatus ReadStatus()
        {
            AccountStatusStorage.ObserveRead(_storedStatus);
            return (AccountStatus)_storedStatus;
        }
    }
}