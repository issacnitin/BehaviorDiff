using BehaviorDiff.Tracer;
using SampleApp;
using SampleApp.Persistence;
using Xunit;

namespace SampleApp.Tests
{
    [TraceTest]
    public sealed class AccountStatusTests
    {
        private readonly WithdrawalService _service = new WithdrawalService();

        [Fact]
        public void Active_account_can_withdraw()
        {
            var repository = new AccountRepository(storedStatus: 1);

            Assert.True(_service.CanWithdraw(repository));
        }

        [Fact]
        public void Closed_account_cannot_withdraw()
        {
            var repository = new AccountRepository(storedStatus: 3);

            Assert.False(_service.CanWithdraw(repository));
        }

        [Fact]
        public void Suspended_account_cannot_withdraw()
        {
            var repository = new AccountRepository(storedStatus: 2);

            Assert.False(_service.CanWithdraw(repository));
        }
    }
}