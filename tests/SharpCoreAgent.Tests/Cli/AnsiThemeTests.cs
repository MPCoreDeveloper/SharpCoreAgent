using SharpCoreAgent.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// The colour rules are a contract with scripts and with assistive terminals: a pipe must never receive
/// escapes it did not ask for, and the opt-outs must work without a flag.
/// </summary>
public sealed class AnsiThemeTests
{
    [Fact]
    public void AutoOnARedirectStaysPlain()
    {
        var theme = AnsiTheme.Create(ColorChoice.Auto, redirected: true, _ => null);

        Assert.False(theme.Enabled);
        Assert.Equal("→", theme.Accent("→"));
        Assert.Equal("ok", theme.Success("ok"));
    }

    [Fact]
    public void AutoOnATerminalPaints()
    {
        var theme = AnsiTheme.Create(ColorChoice.Auto, redirected: false, _ => null);

        Assert.True(theme.Enabled);
        Assert.Equal("\u001b[36m→\u001b[0m", theme.Accent("→"));
    }

    [Fact]
    public void NoColorDisablesAutoEvenOnATerminal()
    {
        var theme = AnsiTheme.Create(ColorChoice.Auto, redirected: false, name => name == "NO_COLOR" ? string.Empty : null);

        Assert.False(theme.Enabled);
    }

    [Fact]
    public void DumbTerminalDisablesAuto()
    {
        var theme = AnsiTheme.Create(ColorChoice.Auto, redirected: false, name => name == "TERM" ? "dumb" : null);

        Assert.False(theme.Enabled);
    }

    [Fact]
    public void AlwaysPaintsEvenIntoAPipe()
    {
        var theme = AnsiTheme.Create(ColorChoice.Always, redirected: true, _ => null);

        Assert.True(theme.Enabled);
    }

    [Fact]
    public void NeverStaysPlainOnATerminal()
    {
        var theme = AnsiTheme.Create(ColorChoice.Never, redirected: false, _ => null);

        Assert.False(theme.Enabled);
        Assert.Equal("text", theme.Dim("text"));
    }

    [Fact]
    public void EmptyTextIsNeverWrapped()
    {
        var theme = AnsiTheme.Create(ColorChoice.Always, redirected: true, _ => null);

        Assert.Equal(string.Empty, theme.Strong(string.Empty));
    }

    [Fact]
    public void DiffPaintsEachLineByItsMarker()
    {
        var theme = AnsiTheme.Create(ColorChoice.Always, redirected: true, _ => null);

        var painted = theme.Diff("--- a/x.cs\n+++ b/x.cs\n@@ -1,1 +1,1 @@\n-old\n+new\n same\n");

        Assert.Contains("\u001b[32m+new\u001b[0m", painted, StringComparison.Ordinal);
        Assert.Contains("\u001b[31m-old\u001b[0m", painted, StringComparison.Ordinal);
        Assert.Contains("\u001b[36m@@", painted, StringComparison.Ordinal);
        Assert.Contains("\u001b[2m same\u001b[0m", painted, StringComparison.Ordinal);
        Assert.Contains("\u001b[1m+++ b/x.cs\u001b[0m", painted, StringComparison.Ordinal);
    }
}
