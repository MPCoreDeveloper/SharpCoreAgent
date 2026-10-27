using SharpCoreAgent.Domain.Model;
using Xunit;

namespace SharpCoreAgent.Tests.Domain;

/// <summary>
/// Usage is reported to the developer and capped by the token budget, so the tests pin the one thing an
/// unreported count must never do: turn into a zero that under-reports what a turn actually cost.
/// </summary>
public sealed class TokenUsageTests
{
    [Fact]
    public void UnknownIsNotZero()
    {
        Assert.True(TokenUsage.Unknown.IsUnknown);
        Assert.Null(TokenUsage.Unknown.Total);
        Assert.Equal("unknown in / unknown out (total unknown)", TokenUsage.Unknown.ToString());
    }

    [Fact]
    public void ATotalNeedsBothHalves()
    {
        Assert.Null(new TokenUsage(10, null).Total);
        Assert.Null(new TokenUsage(null, 5).Total);
        Assert.Equal(15, new TokenUsage(10, 5).Total);
    }

    [Fact]
    public void AddingKeepsAnUnknownHalfUnknown()
    {
        var sum = new TokenUsage(10, 5) + new TokenUsage(null, 5);

        Assert.Null(sum.InputTokens);
        Assert.Equal(10, sum.OutputTokens);
        Assert.Null(sum.Total);
    }

    [Fact]
    public void AddingKnownUsagesSumsBothHalves()
    {
        var sum = new TokenUsage(10, 5) + new TokenUsage(2, 3);

        Assert.Equal(12, sum.InputTokens);
        Assert.Equal(8, sum.OutputTokens);
        Assert.Equal(20, sum.Total);
    }

    [Fact]
    public void AddedToUnknownStaysUnknown() =>
        Assert.True((TokenUsage.Unknown + new TokenUsage(10, 5)).IsUnknown);

    [Fact]
    public void ANullOperandIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => TokenUsage.Unknown + null!);
        Assert.Throws<ArgumentNullException>(() => null! + TokenUsage.Unknown);
    }

    [Fact]
    public void AKnownTotalIsPrintedAsTwoCounts() =>
        Assert.Equal("10 in / 5 out", new TokenUsage(10, 5).ToString());

    [Fact]
    public void AHalfKnownUsageSaysWhichHalfIsMissing() =>
        Assert.Equal("10 in / unknown out (total unknown)", new TokenUsage(10, null).ToString());
}
