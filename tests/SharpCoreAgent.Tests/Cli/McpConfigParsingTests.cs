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
    [InlineData("visualstudio", "VisualStudio")]
    [InlineData("vs", "VisualStudio")]
    [InlineData("codex", "Codex")]
    [InlineData("Claude", "Claude")]
    [InlineData("VSCODE", "VsCode")]
    [InlineData("VisualStudio", "VisualStudio")]
    public void TheClientIsParsedAndTheActionIsConfig(string client, string expected)
    {
        var options = CliOptions.Parse(["mcp", "config", "--client", client], NoEnvironment);

        Assert.Equal(CliCommand.Mcp, options.Command);
        Assert.Equal(McpAction.Config, options.McpAction);
        Assert.Equal(expected, options.McpClient?.ToString());
        Assert.True(options.IndexEnabled);

        // Printing is the default: a client named without --write is a block for a redirect or for a paste.
        Assert.False(options.Write);
    }

    [Fact]
    public void WriteIsParsedBesideTheClient()
    {
        var options = CliOptions.Parse(["mcp", "config", "--client", "visualstudio", "--write"], NoEnvironment);

        Assert.Equal(McpAction.Config, options.McpAction);
        Assert.Equal(McpClientKind.VisualStudio, options.McpClient);
        Assert.True(options.Write);
    }

    [Fact]
    public void ConfigWithoutAClientIsRefusedAndNamesTheFour()
    {
        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["mcp", "config"], NoEnvironment));

        Assert.Equal("mcp config needs --client.", error.Message);
        Assert.Contains("claude", error.Hint, StringComparison.Ordinal);
        Assert.Contains("vscode", error.Hint, StringComparison.Ordinal);
        Assert.Contains("visualstudio", error.Hint, StringComparison.Ordinal);
        Assert.Contains("codex", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownClientIsRefusedAndNamesTheFour()
    {
        var error = Assert.Throws<CliUsageException>(
            () => CliOptions.Parse(["mcp", "config", "--client", "emacs"], NoEnvironment));

        Assert.Equal("unknown client 'emacs'.", error.Message);
        Assert.Equal("Use claude, vscode, visualstudio or codex.", error.Hint);
    }

    [Fact]
    public void WriteIsRefusedForTheClientWhoseFileIsNotJson()
    {
        // Refused before anything is opened: a merge into config.toml would have to keep tables and comments this
        // repository cannot read, and half a rewrite is worse than no rewrite.
        var error = Assert.Throws<CliUsageException>(
            () => CliOptions.Parse(["mcp", "config", "--client", "codex", "--write"], NoEnvironment));

        Assert.Equal("--write does not apply to --client codex.", error.Message);
        Assert.Contains("by hand", error.Hint, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("run", "hello")]
    [InlineData("chat", null)]
    [InlineData("index", null)]
    [InlineData("models", null)]
    [InlineData("config", null)]
    [InlineData("setup", null)]
    public void TheWriteFlagAppliesToConfigIdeAndNothingElse(string command, string? operand)
    {
        // The same reasoning --client is refused by: a command told to write a file and not writing one sends the
        // developer looking for the file. `ide` is the other command that writes one, and it is exempted in Finish.
        string[] arguments = operand is null
            ? [command, "--write"]
            : [command, operand, "--write"];

        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(arguments, NoEnvironment));

        Assert.Equal("--write applies to mcp config and ide.", error.Message);
        Assert.Contains("mcp config --client visualstudio --write", error.Hint, StringComparison.Ordinal);
        Assert.Contains("ide agent --write", error.Hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("serve")]
    [InlineData("bench")]
    public void TheOtherMcpVerbsRefuseTheWriteFlag(string verb)
    {
        // `serve` most of all: its stdout is the protocol, and a mode that wrote a file into it would break the
        // session for the client before the first answer.
        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["mcp", verb, "--write"], NoEnvironment));

        Assert.Equal("--write applies to mcp config and ide.", error.Message);
    }

    [Fact]
    public void HelpAndVersionAnswerWithAWriteInTheirHistory()
    {
        Assert.Equal(CliCommand.Help, CliOptions.Parse(["--help", "--write"], NoEnvironment).Command);
        Assert.Equal(CliCommand.Version, CliOptions.Parse(["--version", "--write"], NoEnvironment).Command);
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
