using SharpCoreAgent.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// <c>mcp serve</c> is the one command whose stdout is not a report: the flags it accepts are the ones that
/// describe a workspace, and everything that names a model, a transcript or a capability belonging to another
/// command is refused rather than ignored.
/// </summary>
/// <remarks>
/// The exit code of every refusal is <see cref="ExitCodes.Usage"/>; <c>McpServeCommandTests</c> pins the paths a
/// process actually takes, so these tests are about the words and the flag list.
/// </remarks>
public sealed class McpServeParsingTests
{
    [Fact]
    public void ServeIsTheAction()
    {
        var options = CliOptions.Parse(["mcp", "serve"], NoEnvironment);

        Assert.Equal(CliCommand.Mcp, options.Command);
        Assert.Equal(McpAction.Serve, options.McpAction);
        Assert.False(options.NoRefresh);
        Assert.True(options.IndexEnabled);
    }

    [Fact]
    public void TheWorkspaceFlagIsReusedAsTyped()
    {
        var options = CliOptions.Parse(["mcp", "serve", "--workspace", @"..\other"], NoEnvironment);

        Assert.Equal(CliCommand.Mcp, options.Command);
        Assert.Equal(@"..\other", options.Workspace);
    }

    [Fact]
    public void NoActionIsAUsageError()
    {
        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["mcp"], NoEnvironment));

        Assert.Equal("mcp needs an action.", error.Message);
        Assert.Contains("mcp serve", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownActionIsRefusedAndNamesTheThreeThereAre()
    {
        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["mcp", "demo"], NoEnvironment));

        Assert.Equal("'demo' is not an mcp action.", error.Message);
        Assert.Contains("serve", error.Hint, StringComparison.Ordinal);
        Assert.Contains("config", error.Hint, StringComparison.Ordinal);
        Assert.Contains("bench", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void BenchIsAnActionAndNeedsNoFlagOfItsOwn()
    {
        var options = CliOptions.Parse(["mcp", "bench"], NoEnvironment);

        Assert.Equal(CliCommand.Mcp, options.Command);
        Assert.Equal(McpAction.Bench, options.McpAction);
        Assert.True(options.IndexEnabled);
        Assert.False(options.NoRefresh);
    }

    [Fact]
    public void BenchTakesNoOperand()
    {
        // The questions are a file rather than an argument: a bench whose questions changed with the command line
        // would publish a number nobody could reproduce.
        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["mcp", "bench", "extra"], NoEnvironment));

        Assert.Equal("mcp bench does not take 'extra'.", error.Message);
        Assert.Contains(McpBench.QueriesRelativePath, error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void BenchRefusesTheFlagsOfTheOtherTwoActions()
    {
        Assert.Contains(
            "--no-index leaves mcp bench",
            Assert.Throws<CliUsageException>(() => CliOptions.Parse(["mcp", "bench", "--no-index"], NoEnvironment)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "--no-refresh applies to mcp serve",
            Assert.Throws<CliUsageException>(() => CliOptions.Parse(["mcp", "bench", "--no-refresh"], NoEnvironment)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "--client applies to mcp config",
            Assert.Throws<CliUsageException>(() => CliOptions.Parse(["mcp", "bench", "--client", "claude"], NoEnvironment)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnOperandAfterServeIsRefusedRatherThanServed()
    {
        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["mcp", "serve", "extra"], NoEnvironment));

        Assert.Equal("mcp serve does not take 'extra'.", error.Message);
    }

    [Fact]
    public void TheActionIsCaseInsensitive()
    {
        var options = CliOptions.Parse(["MCP", "Serve"], NoEnvironment);

        Assert.Equal(McpAction.Serve, options.McpAction);
    }

    [Fact]
    public void NoRefreshIsAcceptedHereAndNowhereElse()
    {
        Assert.True(CliOptions.Parse(["mcp", "serve", "--no-refresh"], NoEnvironment).NoRefresh);

        string[][] elsewhere =
        [
            ["run", "hello", "--no-refresh"],
            ["chat", "--no-refresh"],
            ["index", "--no-refresh"],
            ["models", "--no-refresh"],
            ["config", "--no-refresh"],
            ["setup", "--no-refresh"],
        ];

        Assert.All(
            elsewhere,
            arguments =>
            {
                var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(arguments, NoEnvironment));
                Assert.Equal("--no-refresh applies to mcp serve.", error.Message);
            });
    }

    [Fact]
    public void HelpAndVersionAnswerWithoutAnIndexSoTheFlagIsNotRefused()
    {
        // Neither acts on the flag: they answer a question about the build before any command runs. The point is
        // that a developer reading the help with the flag still in their shell history is not sent to fix it.
        Assert.Equal(CliCommand.Help, CliOptions.Parse(["--help", "--no-refresh"], NoEnvironment).Command);
        Assert.Equal(CliCommand.Version, CliOptions.Parse(["--version", "--no-refresh"], NoEnvironment).Command);
    }

    [Theory]
    [InlineData("--provider", "deepseek")]
    [InlineData("--model", "deepseek-flash")]
    [InlineData("--base-url", "https://example.test/v1")]
    [InlineData("--fallback", "openai-compatible:gpt-5.2")]
    [InlineData("--session", "turn.json")]
    public void TheModelFlagsAreRefusedNotIgnored(string flag, string value)
    {
        var error = Assert.Throws<CliUsageException>(
            () => CliOptions.Parse(["mcp", "serve", flag, value], NoEnvironment));

        Assert.Contains("runs no model and keeps no transcript", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExportedProviderIsNotADecisionAboutThisCommand()
    {
        // The check reads what was typed, not the merged value: an exported SHARPCOREAGENT_PROVIDER belongs to the
        // shell, and a server an editor starts inherits whatever that session happens to hold.
        var options = CliOptions.Parse(
            ["mcp", "serve"],
            name => name == "SHARPCOREAGENT_PROVIDER" ? "deepseek" : null);

        Assert.Equal(CliCommand.Mcp, options.Command);
    }

    [Theory]
    [InlineData("--web", null)]
    [InlineData("--web-allow", "localhost")]
    [InlineData("--mcp", "npx -y one")]
    [InlineData("--add-root", @"..\Shared")]
    [InlineData("--status", null)]
    [InlineData("--json", null)]
    public void TheFlagsOfOtherCommandsAreRefused(string flag, string? value)
    {
        string[] arguments = value is null ? ["mcp", "serve", flag] : ["mcp", "serve", flag, value];

        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(arguments, NoEnvironment));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void NoIndexIsRefusedWithTheFlagThatDoesApply()
    {
        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["mcp", "serve", "--no-index"], NoEnvironment));

        Assert.Contains("--no-index leaves mcp serve with nothing", error.Message, StringComparison.Ordinal);
        Assert.Contains("--no-refresh", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownCommandNamesMcpAmongTheOnesThereAre()
    {
        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["nope"], NoEnvironment));

        Assert.Contains("mcp", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedFlagExitsTwoBeforeAnyProtocolIsWritten()
    {
        using var console = new ConsoleScope(string.Empty);

        var exit = await ProgramEntry.RunAsync(["mcp", "serve", "--model", "deepseek-flash"]);

        Assert.Equal(2, exit);
        Assert.Contains("runs no model and keeps no transcript", console.Error.ToString(), StringComparison.Ordinal);
        Assert.Empty(console.Out.ToString());
    }

    private static string? NoEnvironment(string name) => null;
}
