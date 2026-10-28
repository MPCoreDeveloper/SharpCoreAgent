using SharpCoreAgent.Application;
using Xunit;

namespace SharpCoreAgent.Tests.Application;

/// <summary>
/// <see cref="AgentOptions"/> is the budget one turn is allowed to spend, so every limit is pinned at
/// both edges rather than trusted to stay sensible.
/// </summary>
/// <remarks>
/// Validation is pure — a record in, an exception or the same record out — so these tests need no
/// workspace, no provider and no container. The reported parameter name matters as much as the type:
/// a caller that maps a bad option back to a command-line flag reads
/// <see cref="ArgumentException.ParamName"/>, not the message.
/// </remarks>
public sealed class AgentOptionsTests
{
    [Fact]
    public void TheDefaultsValidateAndReturnTheSameInstance()
    {
        var options = new AgentOptions();

        Assert.Same(options, options.Validate());
    }

    [Fact]
    public void TheDefaultsAreSizedForAWorkingSessionRatherThanADemoOne()
    {
        var options = new AgentOptions();

        // A turn that reads, edits, builds and retests is tens of model calls, so the old bound of 24 ended
        // ordinary work with "the iteration budget ran out".
        Assert.Equal(400, options.MaxIterations);

        // Millions of tokens is what a session a developer keeps open all afternoon spends: a ceiling real work
        // reaches in ten minutes does not protect anyone, it just ends the conversation.
        Assert.Equal(10_000_000, options.MaxTotalTokens);

        // The window bounds one request, not the session: it is high enough that a conversation has to be very
        // long before the oldest messages have to go.
        Assert.Equal(120, options.MaxContextMessages);
    }

    [Fact]
    public void TheContextWindowMayBeSwitchedOffButNotSetToSomethingUnusable()
    {
        // Zero is the documented "the host compacts on its own schedule" switch.
        var off = new AgentOptions { MaxContextMessages = 0 };
        Assert.Same(off, off.Validate());

        var smallest = new AgentOptions { MaxContextMessages = 20 };
        Assert.Same(smallest, smallest.Validate());

        // A window of nineteen would drop the instruction that asked for the work, so it is refused rather than
        // silently rounding up.
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new AgentOptions { MaxContextMessages = 19 }.Validate());
        Assert.Equal("MaxContextMessages", exception.ParamName);
    }

    [Fact]
    public void AcceptsBothEdgesOfTheTurnTimeout()
    {
        var shortest = new AgentOptions { TurnTimeout = TimeSpan.FromSeconds(10) };
        var longest = new AgentOptions { TurnTimeout = TimeSpan.FromHours(1) };

        Assert.Same(shortest, shortest.Validate());
        Assert.Same(longest, longest.Validate());
    }

    [Fact]
    public void RejectsATurnTimeoutShorterThanTenSeconds()
    {
        var options = new AgentOptions { TurnTimeout = TimeSpan.FromSeconds(9) };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());

        Assert.Equal("TurnTimeout", exception.ParamName);
        Assert.Equal(options.TurnTimeout, exception.ActualValue);
        Assert.Contains("TurnTimeout must be between 10 seconds and 1 hour.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsATurnTimeoutLongerThanAnHour()
    {
        var options = new AgentOptions { TurnTimeout = TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1) };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());

        Assert.Equal("TurnTimeout", exception.ParamName);
    }

    [Fact]
    public void AcceptsBothEdgesOfTheTemperature()
    {
        var coldest = new AgentOptions { Temperature = 0 };
        var hottest = new AgentOptions { Temperature = 2 };

        Assert.Same(coldest, coldest.Validate());
        Assert.Same(hottest, hottest.Validate());
    }

    [Fact]
    public void RejectsATemperatureAboveTwo()
    {
        var options = new AgentOptions { Temperature = 2.5 };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());

        Assert.Equal("Temperature", exception.ParamName);
        Assert.Equal(options.Temperature, exception.ActualValue);
        Assert.Contains("Temperature must be between 0 and 2.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsANegativeTemperature()
    {
        var options = new AgentOptions { Temperature = -0.1 };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());

        Assert.Equal("Temperature", exception.ParamName);
    }

    [Fact]
    public void RejectsNotANumberAsATemperature()
    {
        // A NaN temperature reaches the sampler as "no value at all", and some servers answer it with a
        // 400 long after the turn started. Refusing it here keeps the failure local and readable.
        var options = new AgentOptions { Temperature = double.NaN };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());

        Assert.Equal("Temperature", exception.ParamName);
    }

    [Fact]
    public void RejectsAnIterationBudgetOfZero()
    {
        var options = new AgentOptions { MaxIterations = 0 };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());

        Assert.Equal("MaxIterations", exception.ParamName);
    }

    [Fact]
    public void RejectsATokenBudgetBelowOneThousand()
    {
        var options = new AgentOptions { MaxTotalTokens = 999 };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }
}
