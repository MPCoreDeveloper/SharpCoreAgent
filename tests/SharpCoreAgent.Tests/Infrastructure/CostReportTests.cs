using SharpCoreAgent.Domain.Sessions;
using SharpCoreAgent.Infrastructure.Providers;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The three states a cost line can be in, and the rule that keeps a missing number from rendering as
/// a small one.
/// </summary>
/// <remarks>
/// Every assertion here is about the difference between "costs nothing" and "nobody knows": a report
/// that prints <c>$0.00</c> for an unpriced model is worse than one that refuses to answer, because a
/// developer reads the first as a measurement and plans the next hour on it.
/// </remarks>
public sealed class CostReportTests
{
    private static ModelUsage Flash(int? input, int? output, int turns = 1) =>
        new("deepseek:deepseek-flash", input, output, turns);

    private static ModelUsage Chat(int? input, int? output, int turns = 1) =>
        new("deepseek:deepseek-chat", input, output, turns);

    [Fact]
    public void ASessionThatReportedNothingHasNoLines()
    {
        var report = CostReport.Create([]);

        Assert.Empty(report.Lines);
        Assert.Equal(0m, report.Total);
        Assert.True(report.IsComplete);
    }

    [Fact]
    public void AMeasuredLineIsTheProductOfItsTokensAndTheRate()
    {
        var report = CostReport.Create([Flash(1_000_000, 1_000_000)]);

        var line = Assert.Single(report.Lines);
        Assert.True(line.IsPriced);
        Assert.Equal(1.50m, line.Cost);
        Assert.Equal(1.50m, report.Total);
        Assert.Equal(1, report.Measured);
        Assert.True(report.IsComplete);
    }

    [Fact]
    public void AMeasuredLineIsWhatMakesATotalPrintable()
    {
        // The command prints `total unknown` when this is zero: a sum of nothing is not a measurement.
        Assert.Equal(0, CostReport.Create([Chat(1_000, 2_000)]).Measured);
        Assert.Equal(0, CostReport.Create([Flash(null, null)]).Measured);
        Assert.Equal(1, CostReport.Create([Flash(1_000, 2_000), Chat(5, 5)]).Measured);
    }

    [Fact]
    public void AnUnpricedModelIsCountedNotGuessed()
    {
        var report = CostReport.Create([Chat(1_000, 2_000)]);

        var line = Assert.Single(report.Lines);
        Assert.False(line.IsPriced);
        Assert.Null(line.Cost);
        Assert.Equal(0m, report.Total);
        Assert.Equal(1, report.Unpriced);
        Assert.Equal(0, report.UnknownUsage);
        Assert.False(report.IsComplete);
    }

    [Fact]
    public void AProviderThatReportedNothingIsCountedSeparatelyFromAMissingPrice()
    {
        var report = CostReport.Create([Flash(null, null)]);

        Assert.Equal(0, report.Unpriced);
        Assert.Equal(1, report.UnknownUsage);
        Assert.Equal(0m, report.Total);
        Assert.False(report.IsComplete);
    }

    [Fact]
    public void HalfKnownUsageIsNotPricedAtHalfCost()
    {
        var report = CostReport.Create([Flash(1_000, null)]);

        Assert.Equal(1, report.UnknownUsage);
        Assert.Null(Assert.Single(report.Lines).Cost);
        Assert.Equal(0m, report.Total);
    }

    [Fact]
    public void TheTotalIsTheSumOfTheLinesThatCouldBeMeasured()
    {
        var report = CostReport.Create([Flash(1_000_000, 0), Chat(5_000, 5_000)]);

        Assert.Equal(2, report.Lines.Count);
        Assert.Equal(0.30m, report.Total);
        Assert.Equal(1, report.Unpriced);
        Assert.False(report.IsComplete);
    }

    [Fact]
    public void LinesAreOrderedByNameTheWayUsagePrintsThem()
    {
        var report = CostReport.Create([Chat(1, 1), Flash(1, 1)]);

        Assert.Equal(
            ["deepseek:deepseek-chat", "deepseek:deepseek-flash"],
            report.Lines.Select(line => line.ModelId));
    }

    [Fact]
    public void TurnCountsAreReportedUnchanged()
    {
        // The report prices tokens, not turns; a caller reading turns must see what the session holds.
        var report = CostReport.Create([Flash(10, 10, turns: 7)]);

        Assert.Equal(7, Assert.Single(report.Lines).Usage.Turns);
    }

    [Fact]
    public void ANullUsageListIsARefusedCall()
    {
        Assert.Throws<ArgumentNullException>(() => CostReport.Create(null!));
    }
}
