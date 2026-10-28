using SharpCoreAgent.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// What a bare invocation means: a session when a terminal is attached, the usage block when there is
/// not one.
/// </summary>
/// <remarks>
/// The terminal probe is injected rather than read from <see cref="Console"/>, so both branches are
/// tested on the same machine. That is the whole reason <c>Terminal.Interactive</c> is settable: a test
/// host cannot allocate the terminal these cases are about.
/// </remarks>
[Collection(CliConsole.Name)]
public sealed class InteractiveStartTests
{
    [Fact]
    public void NoArgumentsOnATerminalStartsAChat()
    {
        var options = CliOptions.Parse([], NoEnvironment, Interactive);

        Assert.Equal(CliCommand.Chat, options.Command);
        Assert.False(options.CommandExplicit);
        Assert.Empty(options.Prompt);
    }

    [Fact]
    public void NoArgumentsWithoutATerminalPrintsTheUsageBlock()
    {
        var options = CliOptions.Parse([], NoEnvironment, NotInteractive);

        Assert.Equal(CliCommand.Help, options.Command);
        Assert.False(options.CommandExplicit);
    }

    [Fact]
    public void FlagsWithoutACommandFollowTheSameRule()
    {
        // `sharpcoreagent --quiet` is still "no command": the flag changes how the session renders, not
        // whether one starts.
        Assert.Equal(CliCommand.Chat, CliOptions.Parse(["--quiet"], NoEnvironment, Interactive).Command);
        Assert.Equal(CliCommand.Help, CliOptions.Parse(["--quiet"], NoEnvironment, NotInteractive).Command);
    }

    [Fact]
    public void AFlagThatNamesAnAnswerIsStillAnAnswer()
    {
        Assert.Equal(CliCommand.Help, CliOptions.Parse(["--help"], NoEnvironment, Interactive).Command);
        Assert.Equal(CliCommand.Version, CliOptions.Parse(["--version"], NoEnvironment, Interactive).Command);
    }

    [Fact]
    public void ATypedCommandSaysThatItWasTyped()
    {
        var options = CliOptions.Parse(["chat"], NoEnvironment, NotInteractive);

        Assert.Equal(CliCommand.Chat, options.Command);
        Assert.True(options.CommandExplicit);

        // A command is a command even when no terminal is attached: only the default is conditional.
        Assert.Equal(CliCommand.Run, CliOptions.Parse(["run", "hi"], NoEnvironment, NotInteractive).Command);
    }

    [Fact]
    public async Task ABareStartRunsTheSessionAndSaysWhatItIs()
    {
        using var directory = TempDirectory.Create();
        var previous = Terminal.Interactive;
        Terminal.Interactive = Interactive;

        try
        {
            using var console = new ConsoleScope(string.Empty);
            var exitCode = await ProgramEntry.RunAsync(
                ["--provider", "demo", "--workspace", directory.Path, "--no-session"]);

            var banner = console.Error.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("╭", banner, StringComparison.Ordinal);
            Assert.Contains("╰", banner, StringComparison.Ordinal);
            Assert.Contains(HelpText.VersionLine, banner, StringComparison.Ordinal);
            Assert.Contains("model      demo:demo-scripted", banner, StringComparison.Ordinal);
            Assert.Contains("workspace  ", banner, StringComparison.Ordinal);
            Assert.Contains("budget 10,000,000 tokens", banner, StringComparison.Ordinal);
            Assert.Contains("/help", banner, StringComparison.Ordinal);
        }
        finally
        {
            Terminal.Interactive = previous;
        }
    }

    [Fact]
    public async Task AChatTypedByHandPrintsNoBanner()
    {
        using var directory = TempDirectory.Create();
        var previous = Terminal.Interactive;
        Terminal.Interactive = Interactive;

        try
        {
            using var console = new ConsoleScope(string.Empty);
            var exitCode = await ProgramEntry.RunAsync(
                ["chat", "--provider", "demo", "--workspace", directory.Path, "--no-session"]);

            Assert.Equal(0, exitCode);
            Assert.DoesNotContain(HelpText.VersionLine, console.Error.ToString(), StringComparison.Ordinal);
            Assert.Contains("workspace: ", console.Error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Terminal.Interactive = previous;
        }
    }

    [Fact]
    public async Task QuietSuppressesTheBanner()
    {
        using var directory = TempDirectory.Create();
        var previous = Terminal.Interactive;
        Terminal.Interactive = Interactive;

        try
        {
            using var console = new ConsoleScope(string.Empty);
            var exitCode = await ProgramEntry.RunAsync(
                ["--provider", "demo", "--workspace", directory.Path, "--no-session", "--quiet"]);

            Assert.Equal(0, exitCode);
            Assert.DoesNotContain(HelpText.VersionLine, console.Error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("workspace: ", console.Error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Terminal.Interactive = previous;
        }
    }

    private static bool Interactive() => true;

    private static bool NotInteractive() => false;

    private static string? NoEnvironment(string name) => null;
}
