using SampleApp.Persistence;

namespace SampleApp
{
    public sealed class WithdrawalService
    {
        private readonly AccessControl _accessControl = new AccessControl();

        public bool CanWithdraw(AccountRepository repository)
        {
            AccountStatus status = repository.ReadStatus();
            return _accessControl.CanWithdraw(status);
        }
    }
}