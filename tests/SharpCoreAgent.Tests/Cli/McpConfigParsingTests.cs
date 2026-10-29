using SharpCoreAgent.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// <c>mcp config</c> is asked one question — whose configuration — and answers it without touching a workspace, an
/// index or a network. Everything it will not do is refused at parse time, so nothing is half rendered.
/// </summary>
/// <remarks>
/// <c>McpConfigCommandTests</c> pins the three blocks themselves; these tests are about the words and the flag list,
/// and about <c>--client</c> being refused on every other command for the same reason <c>--no-refresh</c> is. The
/// collection is the one that owns the console: <c>ConsoleScope</c> redirects the process's streams, so a test that
/// used it beside another one would read the other's output.
/// </remarks>
[Collection(CliConsole.Name)]
public sealed class McpConfigParsingTests
{
    [Theory]
    [InlineData("claude", "Claude")]
    [InlineData("vscode", "VsCode")]
    [InlineData("codex", "Codex")]
    [InlineData("Claude", "Claude")]
    [InlineData("VSCODE", "VsCode")]
    public void TheClientIsParsedAndTheActionIsConfig(string client, string expected)
    {
        var options = CliOptions.Parse(["mcp", "config", "--client", client], NoEnvironment);

        Assert.Equal(CliCommand.Mcp, options.Command);
        Assert.Equal(McpAction.Config, options.McpAction);
        Assert.Equal(expected, options.McpClient?.ToString());
        Assert.True(options.IndexEnabled);
    }

    [Fact]
    public void ConfigWithoutAClientIsRefusedAndNamesTheThree()
    {
        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["mcp", "config"], NoEnvironment));

        Assert.Equal("mcp config needs --client.", error.Message);
        Assert.Contains("claude", error.Hint, StringComparison.Ordinal);
        Assert.Contains("vscode", error.Hint, StringComparison.Ordinal);
        Assert.Contains("codex", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownClientIsRefusedAndNamesTheThree()
    {
        var error = Assert.Throws<CliUsageException>(
            () => CliOptions.Parse(["mcp", "config", "--client", "emacs"], NoEnvironment));

        Assert.Equal("unknown client 'emacs'.", error.Message);
        Assert.Equal("Use claude, vscode or codex.", error.Hint);
    }

    [Fact]
    public void AnOperandAfterConfigIsRefusedBeforeTheClientIsRead()
    {
        // The shape has a flag for this, so `mcp config claude` is a typo rather than a shorthand.
        var error = Assert.Throws<CliUsageException>(
            () => CliOptions.Parse(["mcp", "config", "claude"], NoEnvironment));

        Assert.Equal("mcp config does not take 'claude'.", error.Message);
        Assert.Contains("--client", error.Hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("run", "hello")]
    [InlineData("chat", null)]
    [InlineData("index", null)]
    [InlineData("models", null)]
    [InlineData("config", null)]
    [InlineData("setup", null)]
    public void TheClientFlagAppliesToConfigAndNothingElse(string command, string? operand)
    {
        string[] arguments = operand is null
            ? [command, "--client", "claude"]
            : [command, operand, "--client", "claude"];

        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(arguments, NoEnvironment));

        Assert.Equal("--client applies to mcp config.", error.Message);
        Assert.Contains("mcp config --client claude", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void ServeRefusesTheSnippetFlagRatherThanIgnoringIt()
    {
        var error = Assert.Throws<CliUsageException>(
            () => CliOptions.Parse(["mcp", "serve", "--client", "claude"], NoEnvironment));

        Assert.Equal("--client applies to mcp config.", error.Message);
    }

    [Fact]
    public void HelpAndVersionAnswerWithAClientInTheirHistory()
    {
        // Neither writes anything: they answer a question about the build, so a flag left in the shell history is
        // not the thing to send the developer back for.
        Assert.Equal(CliCommand.Help, CliOptions.Parse(["--help", "--client", "codex"], NoEnvironment).Command);
        Assert.Equal(CliCommand.Version, CliOptions.Parse(["--version", "--client", "codex"], NoEnvironment).Command);
    }

    [Fact]
    public void RefreshAndTheIndexFlagsAreRefusedBecauseConfigDoesNeither()
    {
        var refresh = Assert.Throws<CliUsageException>(
            () => CliOptions.Parse(["mcp", "config", "--no-refresh", "--client", "claude"], NoEnvironment));

        Assert.Equal("--no-refresh applies to mcp serve.", refresh.Message);

        var index = Assert.Throws<CliUsageException>(
            () => CliOptions.Parse(["mcp", "config", "--no-index", "--client", "claude"], NoEnvironment));

        Assert.Equal("--no-index does not apply to mcp config.", index.Message);
    }

    [Theory]
    [InlineData("--web", null)]
    [InlineData("--web-allow", "localhost")]
    [InlineData("--mcp", "npx -y one")]
    [InlineData("--add-root", @"..\Shared")]
    [InlineData("--status", null)]
    [InlineData("--json", null)]
    public void TheFlagsOfOtherCommandsAreRefusedOnConfigToo(string flag, string? value)
    {
        string[] arguments = value is null
            ? ["mcp", "config", flag, "--client", "claude"]
            : ["mcp", "config", flag, value, "--client", "claude"];

        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(arguments, NoEnvironment));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public async Task AnUnknownClientExitsTwoWithNothingOnStdout()
    {
        using var console = new ConsoleScope(string.Empty);

        var exit = await ProgramEntry.RunAsync(["mcp", "config", "--client", "emacs"]);

        Assert.Equal(2, exit);
        Assert.Contains("unknown client 'emacs'", console.Error.ToString(), StringComparison.Ordinal);
        Assert.Empty(console.Out.ToString());
    }

    private static string? NoEnvironment(string name) => null;
}
