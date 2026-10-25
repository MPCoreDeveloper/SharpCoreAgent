using System.Globalization;

namespace SharpCoreAgent.Infrastructure.Chat;

/// <summary>
/// How often and how patiently a transient provider failure is retried.
/// </summary>
/// <remarks>
/// The defaults encode the one rule that matters: retrying is cheap only while nothing has been
/// streamed yet. The attempts are bounded at three so a provider having a bad minute costs seconds of
/// the developer's time rather than minutes, and the ceiling is eight seconds so a <c>Retry-After</c>
/// of an hour does not silently park the terminal.
/// </remarks>
public sealed record RetryPolicy
{
    /// <summary>Gets the policy the product ships with.</summary>
    public static RetryPolicy Default { get; } = new();

    /// <summary>Gets the total number of attempts, including the first one. Default 3.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Gets the backoff applied to the first retry. Default one second, doubled per attempt.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets the largest computed backoff, before jitter. Default eight seconds.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>Gets the upper bound of the random jitter added to every computed delay. Default 250 ms.</summary>
    public int MaxJitterMilliseconds { get; init; } = 250;

    /// <summary>
    /// Computes the delay before one retry.
    /// </summary>
    /// <param name="attemptNumber">
    /// The attempt that just failed, counting from one: the first failure is followed by
    /// <see cref="BaseDelay"/>.
    /// </param>
    /// <param name="retryAfter">The delay the provider asked for, if it sent one.</param>
    /// <param name="jitterMilliseconds">The jitter to add; the caller supplies it so the result is reproducible.</param>
    /// <returns>The delay to wait before the next attempt.</returns>
    public TimeSpan DelayFor(int attemptNumber, TimeSpan? retryAfter, int jitterMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptNumber, 1);

        // 2^1, 2^2, 2^3 … seconds, capped. Exponential rather than linear because the interesting
        // failures are rate limits, which need room rather than a second opinion.
        var exponent = Math.Min(attemptNumber, 16);
        var seconds = Math.Min(Math.Pow(2, exponent), MaxDelay.TotalSeconds);
        var delay = TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(Math.Clamp(jitterMilliseconds, 0, MaxJitterMilliseconds));

        return retryAfter is { } asked && asked > delay ? asked : delay;
    }

    /// <summary>
    /// Checks the policy and returns this instance so it can be used inline.
    /// </summary>
    /// <returns>This instance.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its documented range.</exception>
    public RetryPolicy Validate()
    {
        if (MaxAttempts < 1 || MaxAttempts > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), MaxAttempts, "must be between 1 and 10");
        }

        if (BaseDelay < TimeSpan.Zero || BaseDelay > MaxDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(BaseDelay), BaseDelay, "must be non-negative and no larger than MaxDelay");
        }

        if (MaxDelay > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDelay), MaxDelay, "must be at most one minute");
        }

        if (MaxJitterMilliseconds < 0 || MaxJitterMilliseconds > 5_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxJitterMilliseconds), MaxJitterMilliseconds, "must be between 0 and 5000");
        }

        return this;
    }

    /// <inheritdoc />
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{MaxAttempts} attempts, {BaseDelay.TotalSeconds:0.#}s..{MaxDelay.TotalSeconds:0.#}s backoff");
}

/// <summary>
/// One retry that is about to happen, so a caller can tell the developer why the turn is pausing.
/// </summary>
/// <param name="AttemptNumber">The attempt that failed, counting from one.</param>
/// <param name="MaxAttempts">The total number of attempts the policy allows.</param>
/// <param name="Delay">How long the decorator will wait before the next attempt.</param>
/// <param name="Reason">Why the attempt failed, with no credential in it.</param>
public sealed record RetryAttempt(int AttemptNumber, int MaxAttempts, TimeSpan Delay, string Reason)
{
    /// <inheritdoc />
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"retrying ({AttemptNumber + 1}/{MaxAttempts}) in {Delay.TotalSeconds:0.#}s: {Reason}");
}
