using RealDiff.Tracer;
using SampleApp;
using SampleApp.Persistence;
using Xunit;

namespace SampleApp.Tests
{
    [TraceTest]
    public sealed class AccountStatusTests
    {
        [Fact]
        public void Active_account_can_withdraw()
        {
            var service = new WithdrawalService();
            var repository = new AccountRepository(storedStatus: 1);

            Assert.True(service.CanWithdraw(repository));
        }

        [Fact]
        public void Closed_account_cannot_withdraw()
        {
            var service = new WithdrawalService();
            var repository = new AccountRepository(storedStatus: 3);

            Assert.False(service.CanWithdraw(repository));
        }

        [Fact]
        public void Suspended_account_cannot_withdraw()
        {
            var service = new WithdrawalService();
            var repository = new AccountRepository(storedStatus: 2);

            Assert.False(service.CanWithdraw(repository));
        }
    }
}