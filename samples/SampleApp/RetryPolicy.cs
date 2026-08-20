namespace SampleApp
{
    public sealed class RetryPolicy
    {
        public bool[] BuildRetryPlan(int statusCode, int attempts)
        {
            var plan = new bool[attempts];
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                plan[attempt - 1] = ShouldRetry(statusCode, attempt);
            }

            return plan;
        }

        public bool ShouldRetry(int statusCode, int attempt)
        {
            return IsTransient(statusCode) && attempt < RetrySettings.MaxAttempts;
        }

        public bool IsTransient(int statusCode)
        {
            return statusCode == 408 || statusCode == 429 || statusCode >= 500;
        }
    }
}