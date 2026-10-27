using SharpCoreAgent.Application;
using SharpCoreAgent.Domain.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Application;

/// <summary>
/// The trimmer is the last thing between a large tool result and the model's context window, so the
/// tests pin what a model has to be able to notice: the head survives, the cut is announced, the result
/// still fits the budget, and nothing else about it changes.
/// </summary>
public sealed class ToolResultTrimmerTests
{
    [Fact]
    public void AResultUnderTheBudgetIsReturnedUnchanged()
    {
        var result = ToolResult.Success("call-1", "read_file", "short");

        Assert.Same(result, ToolResultTrimmer.Trim(result, 100));
    }

    [Fact]
    public void AResultExactlyOnTheBudgetIsNotTrimmed()
    {
        var result = ToolResult.Success("call-1", "read_file", new string('a', 100));

        Assert.Same(result, ToolResultTrimmer.Trim(result, 100));
    }

    [Fact]
    public void ANonPositiveBudgetDisablesTrimming()
    {
        var result = ToolResult.Success("call-1", "read_file", new string('a', 500));

        Assert.Same(result, ToolResultTrimmer.Trim(result, 0));
        Assert.Same(result, ToolResultTrimmer.Trim(result, -1));
    }

    [Fact]
    public void ALongResultKeepsItsHeadAndSaysHowMuchItDropped()
    {
        var content = new string('a', 1000);

        var trimmed = ToolResultTrimmer.Trim(ToolResult.Success("call-1", "read_file", content), 200);

        Assert.True(trimmed.IsTruncated);
        Assert.StartsWith("aaa", trimmed.Content, StringComparison.Ordinal);
        Assert.Contains("[sharpcoreagent: truncated]", trimmed.Content, StringComparison.Ordinal);
        Assert.Contains("of 1000 characters omitted.", trimmed.Content, StringComparison.Ordinal);
        Assert.True(trimmed.Content.Length <= 200, $"the message is {trimmed.Content.Length} characters, over the 200 budget");
    }

    [Fact]
    public void TheCountNamesExactlyWhatWasDropped()
    {
        var trimmed = ToolResultTrimmer.Trim(ToolResult.Success("call-1", "read_file", new string('a', 1000)), 200);

        // The head that survives is whatever the marker and the count line left room for, so the count is
        // checked against the text that actually came back rather than against an assumed cut point.
        var marker = trimmed.Content.IndexOf("[sharpcoreagent:", StringComparison.Ordinal);
        var kept = marker - 1;

        Assert.True(kept > 0);
        Assert.Contains($"{1000 - kept} of 1000 characters omitted.", trimmed.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void EverythingExceptTheContentSurvivesTheCut()
    {
        var original = ToolResult.Success(
            "call-1",
            "read_file",
            new string('a', 1000),
            new ToolEffect(ToolEffectKind.Read, "src/app.cs"));

        var trimmed = ToolResultTrimmer.Trim(original, 200);

        Assert.Equal("call-1", trimmed.CallId);
        Assert.Equal("read_file", trimmed.ToolName);
        Assert.False(trimmed.IsError);
        Assert.False(trimmed.IsRefused);
        Assert.Equal(new ToolEffect(ToolEffectKind.Read, "src/app.cs"), Assert.Single(trimmed.Effects));
    }

    [Fact]
    public void AFailureIsTrimmedTooAndStaysAFailure()
    {
        var failure = ToolResult.Failure("call-1", "read_file", new string('e', 1000));

        var trimmed = ToolResultTrimmer.Trim(failure, 200);

        Assert.True(trimmed.IsError);
        Assert.True(trimmed.IsTruncated);
        Assert.Equal("read_file", trimmed.ToolName);
    }

    [Fact]
    public void ABudgetSmallerThanTheMarkerStillAnnouncesTheCut()
    {
        var trimmed = ToolResultTrimmer.Trim(ToolResult.Success("call-1", "read_file", new string('a', 1000)), 10);

        Assert.True(trimmed.IsTruncated);
        Assert.Equal("[sharpcoreagent: truncated] 1000 of 1000 characters omitted.", trimmed.Content.TrimStart('\n'));
    }

    [Fact]
    public void ANullResultIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => ToolResultTrimmer.Trim(null!, 100));
}
