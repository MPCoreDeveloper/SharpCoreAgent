using System.Globalization;
using SharpCoreAgent.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// The two clocks a build report prints, and the line that appears when they disagree.
/// </summary>
/// <remarks>
/// The numbers here are the ones that cost this repository half a day: a run whose wall clock said thirty thousand
/// seconds and whose processor time said fourteen minutes. Nothing in a test can suspend the machine, so the rule is
/// exercised as arithmetic and the command is exercised end to end in <see cref="IndexCommandTests"/>.
/// </remarks>
public sealed class BuildTimingTests
{
    [Fact]
    public void AnOrdinaryBuildRendersBothClocksAndSaysNothingElse()
    {
        var timing = new BuildTiming(TimeSpan.FromSeconds(122), TimeSpan.FromSeconds(118));

        Assert.False(timing.Diverges);
        Assert.Null(timing.Divergence);
        Assert.Equal("2 m 2 s wall · 1 m 58 s cpu", timing.Render());
    }

    [Fact]
    public void AnEightHourNapIsNamedAsSuch()
    {
        var timing = new BuildTiming(TimeSpan.FromSeconds(30_345), TimeSpan.FromSeconds(865));

        Assert.True(timing.Diverges);
        Assert.Equal("8 h 25 m wall · 14 m 25 s cpu", timing.Render());

        var line = timing.Divergence;

        Assert.NotNull(line);
        Assert.Contains("35×", line, StringComparison.Ordinal);
        Assert.Contains("asleep", line, StringComparison.Ordinal);
        Assert.Contains("Kernel-Power", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AShortRunIsNotWorthALine()
    {
        // Ninety seconds of wall clock and twelve of processor time is a factor of seven, and still ordinary:
        // below the floor the difference is a cold cache, not a diagnosis.
        var timing = new BuildTiming(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(3));

        Assert.False(timing.Diverges);
        Assert.Equal("20.0 s wall · 3.0 s cpu", timing.Render());
    }

    [Fact]
    public void WorkOnSeveralCoresIsNotADivergence()
    {
        var timing = new BuildTiming(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(35));

        Assert.False(timing.Diverges);
        Assert.Null(timing.Divergence);
    }

    [Fact]
    public void NoProcessorTimeAtAllRoundsToOverAHundred()
    {
        var timing = new BuildTiming(TimeSpan.FromHours(2), TimeSpan.Zero);

        var line = timing.Divergence;

        Assert.NotNull(line);
        Assert.Contains("over 100×", line, StringComparison.Ordinal);
    }

    [Fact]
    public void SpanRendersAtTheWidthOfTheDuration()
    {
        Assert.Equal("0.4 s", BuildTiming.Span(TimeSpan.FromMilliseconds(400)));
        Assert.Equal("0.0 s", BuildTiming.Span(TimeSpan.FromMilliseconds(-5)));
        Assert.Equal("10 m 32 s", BuildTiming.Span(TimeSpan.FromSeconds(632)));
        Assert.Equal("8 h 25 m", BuildTiming.Span(TimeSpan.FromSeconds(30_345)));
    }
}
