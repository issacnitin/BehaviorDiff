using System.Threading.Tasks;

namespace SampleApp
{
    public interface IPaymentGateway
    {
        Task<int> ChargeAsync(decimal amount, int attempt);
    }

    public sealed class PaymentResult
    {
        public bool Succeeded { get; init; }

        public int AttemptCount { get; init; }

        public int StatusCode { get; init; }
    }

    public sealed class PaymentClient
    {
        private readonly IPaymentGateway _gateway;
        private readonly RetryPolicy _retryPolicy;

        public PaymentClient(IPaymentGateway gateway, RetryPolicy retryPolicy)
        {
            _gateway = gateway;
            _retryPolicy = retryPolicy;
        }

        public async Task<PaymentResult> ChargeAsync(decimal amount)
        {
            bool[]? retryPlan = null;
            for (int attempt = 1; ; attempt++)
            {
                int statusCode = await _gateway.ChargeAsync(amount, attempt).ConfigureAwait(false);
                if (statusCode == 200)
                {
                    return new PaymentResult
                    {
                        Succeeded = true,
                        AttemptCount = attempt,
                        StatusCode = statusCode,
                    };
                }

                retryPlan ??= _retryPolicy.BuildRetryPlan(statusCode, attempts: 10);
                if (!retryPlan[attempt - 1])
                {
                    return new PaymentResult
                    {
                        Succeeded = false,
                        AttemptCount = attempt,
                        StatusCode = statusCode,
                    };
                }
            }
        }
    }
}