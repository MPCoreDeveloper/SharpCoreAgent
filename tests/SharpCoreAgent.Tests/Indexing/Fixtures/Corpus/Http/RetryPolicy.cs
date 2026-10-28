namespace Demo.Http;

/// Decides whether a failed request is worth another attempt, and how long to wait before it.
public sealed class RetryPolicy
{
    public int MaxAttempts { get; init; } = 4;

    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    public bool ShouldRetry(int attempt, int statusCode) =>
        attempt < MaxAttempts && (statusCode == 429 || statusCode >= 500);

    /// Exponential backoff: the wait doubles with every attempt that already failed.
    public TimeSpan BackoffFor(int attempt) =>
        TimeSpan.FromMilliseconds(BaseDelay.TotalMilliseconds * Math.Pow(2, attempt));
}
