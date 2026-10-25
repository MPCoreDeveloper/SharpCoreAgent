using SharpCoreAgent.Infrastructure.Chat;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The retry arithmetic: bounded attempts, exponential backoff, capped delay and a provider's own request.
/// </summary>
/// <remarks>
/// The jitter is an argument rather than a random draw, which is what lets these tests assert exact delays:
/// a test that waited for a real second would be a slow test that still could not check the doubling.
/// </remarks>
public sealed class RetryPolicyTests
{
    [Fact]
    public void TheDefaultPolicyIsBoundedToThreePatienceSeconds()
    {
        var policy = RetryPolicy.Default;

        Assert.Equal(3, policy.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(1), policy.BaseDelay);
        Assert.Equal(TimeSpan.FromSeconds(8), policy.MaxDelay);
        Assert.Equal(250, policy.MaxJitterMilliseconds);
        Assert.Same(policy, policy.Validate());
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 8)]
    [InlineData(20, 8)]
    public void TheDelayDoublesPerAttemptAndStopsAtTheCeiling(int attempt, int expectedSeconds)
    {
        var delay = RetryPolicy.Default.DelayFor(attempt, retryAfter: null, jitterMilliseconds: 0);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void JitterIsAddedAndClampedToItsCeiling()
    {
        var policy = RetryPolicy.Default;

        Assert.Equal(TimeSpan.FromSeconds(2.25), policy.DelayFor(1, null, 250));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.DelayFor(1, null, -100));
        Assert.Equal(TimeSpan.FromSeconds(2.25), policy.DelayFor(1, null, 5_000));
    }

    [Fact]
    public void AProviderThatAsksForLongerGetsLonger()
    {
        var delay = RetryPolicy.Default.DelayFor(1, TimeSpan.FromSeconds(30), jitterMilliseconds: 0);

        Assert.Equal(TimeSpan.FromSeconds(30), delay);
    }

    [Fact]
    public void AProviderThatAsksForLessIsIgnored()
    {
        var delay = RetryPolicy.Default.DelayFor(2, TimeSpan.FromSeconds(1), jitterMilliseconds: 0);

        Assert.Equal(TimeSpan.FromSeconds(4), delay);
    }

    [Fact]
    public void AnAttemptBelowOneIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryPolicy.Default.DelayFor(0, null, 0));

    [Fact]
    public void TooManyAttemptsIsRejected()
    {
        var failure = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RetryPolicy { MaxAttempts = 11 }.Validate());

        Assert.Equal("MaxAttempts", failure.ParamName);
    }

    [Fact]
    public void ABackoffLargerThanTheCeilingIsRejected()
    {
        var failure = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RetryPolicy { BaseDelay = TimeSpan.FromSeconds(30) }.Validate());

        Assert.Equal("BaseDelay", failure.ParamName);
    }

    [Fact]
    public void ANegativeBackoffIsRejected()
    {
        var failure = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RetryPolicy { BaseDelay = TimeSpan.FromSeconds(-1) }.Validate());

        Assert.Equal("BaseDelay", failure.ParamName);
    }

    [Fact]
    public void ACeilingBeyondAMinuteIsRejected()
    {
        var failure = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RetryPolicy { MaxDelay = TimeSpan.FromMinutes(2) }.Validate());

        Assert.Equal("MaxDelay", failure.ParamName);
    }

    [Fact]
    public void JitterOutsideItsRangeIsRejected()
    {
        var tooMuch = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RetryPolicy { MaxJitterMilliseconds = 5_001 }.Validate());
        var negative = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RetryPolicy { MaxJitterMilliseconds = -1 }.Validate());

        Assert.Equal("MaxJitterMilliseconds", tooMuch.ParamName);
        Assert.Equal("MaxJitterMilliseconds", negative.ParamName);
    }

    [Fact]
    public void ThePolicyDescribesItselfForTheLog()
    {
        Assert.Equal("3 attempts, 1s..8s backoff", RetryPolicy.Default.ToString());
        Assert.Equal(
            "retrying (2/3) in 4s: rate limited",
            new RetryAttempt(1, 3, TimeSpan.FromSeconds(4), "rate limited").ToString());
    }
}
