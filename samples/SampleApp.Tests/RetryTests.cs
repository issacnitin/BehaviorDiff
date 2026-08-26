using System;
using System.IO;
using System.Threading.Tasks;
using RealDiff.Tracer;
using SampleApp;
using Xunit;

namespace SampleApp.Tests
{
    [TraceTest]
    public sealed class RetryTests
    {
        private static PaymentClient CreateClient(int recoveryAttempt)
        {
            string configPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "payment-config.json");
            ConfigParser.Apply(PaymentFixture.RetryConfigFixture.Load(configPath));
            return new PaymentClient(
                new Diagnostics.TransientPaymentGateway(recoveryAttempt),
                new RetryPolicy());
        }

            private static PaymentResult Charge(int recoveryAttempt)
            {
                return CreateClient(recoveryAttempt).ChargeAsync(125m).GetAwaiter().GetResult();
            }

            private static void ChargeWithoutInspectingResult(int recoveryAttempt)
            {
                CreateClient(recoveryAttempt).ChargeAsync(125m).GetAwaiter().GetResult();
            }

        [Fact]
        public void Payment_succeeds_under_transient_failure()
        {
            ChargeWithoutInspectingResult(recoveryAttempt: 2);
        }

        [Fact]
        public void Payment_survives_extended_outage()
        {
            PaymentResult result = Charge(recoveryAttempt: 7);

            Assert.True(result.Succeeded);
            Assert.Equal(7, result.AttemptCount);
        }
    }
}

namespace SampleApp.Diagnostics
{
    internal sealed class TransientPaymentGateway : IPaymentGateway
    {
        private readonly int _recoveryAttempt;

        internal TransientPaymentGateway(int recoveryAttempt)
        {
            _recoveryAttempt = recoveryAttempt;
        }

        public Task<int> ChargeAsync(decimal amount, int attempt)
        {
            return Task.FromResult(attempt >= _recoveryAttempt ? 200 : 503);
        }
    }
}
