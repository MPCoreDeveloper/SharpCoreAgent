using SharpCoreAgent.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// <c>--mcp</c> starts a program, so it is authority for one invocation rather than a preference: never a setting,
/// never an environment variable, and refused by the commands that never run a turn.
/// </summary>
public sealed class McpOptionTests
{
    [Fact]
    public void NoServerIsStartedUnlessOneIsNamed()
    {
        var options = CliOptions.Parse(["chat"], NoEnvironment);

        Assert.Empty(options.McpServers);
        Assert.Empty(options.ToSharpCoreAgentOptions().McpServers);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("chat")]
    public void TheFlagNamesAServerForTheTurnCommands(string command)
    {
        var arguments = command == "run"
            ? new[] { "run", "hello", "--mcp", "npx -y @modelcontextprotocol/server-filesystem ." }
            : ["chat", "--mcp", "npx -y @modelcontextprotocol/server-filesystem ."];

        var options = CliOptions.Parse(arguments, NoEnvironment);

        Assert.Equal(["npx -y @modelcontextprotocol/server-filesystem ."], options.McpServers);
        Assert.Equal(options.McpServers, options.ToSharpCoreAgentOptions().McpServers);
    }

    [Fact]
    public void ServersAreKeptInTheOrderTheyWereGiven()
    {
        var options = CliOptions.Parse(
            ["run", "hello", "--mcp", "npx -y one", "--mcp", "uvx two"],
            NoEnvironment);

        Assert.Equal(["npx -y one", "uvx two"], options.McpServers);
    }

    [Theory]
    [InlineData("models")]
    [InlineData("index")]
    [InlineData("config")]
    [InlineData("setup")]
    public void TheCommandsThatNeverRunATurnRefuseTheFlag(string command)
    {
        var arguments = command == "index"
            ? new[] { command, "--status", "--mcp", "npx -y one" }
            : [command, "--mcp", "npx -y one"];

        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(arguments, NoEnvironment));

        Assert.Contains("--mcp applies to run and chat", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoEnvironmentVariableStartsAServer()
    {
        var options = CliOptions.Parse(
            ["run", "hello"],
            name => name == "SHARPCOREAGENT_MCP" ? "npx -y one" : null);

        Assert.Empty(options.McpServers);
    }

    [Fact]
    public void TheServerListIsNotASettablePreference()
    {
        Assert.DoesNotContain("mcp", CliOptions.SettableNames);
        Assert.Throws<CliUsageException>(() => CliOptions.EnsureSettable("mcp"));
    }

    [Fact]
    public void ThePromptExplainsTheServersOnlyWhenOneWasNamed()
    {
        var with = CliOptions.Parse(["run", "hello", "--mcp", "npx -y one"], NoEnvironment)
            .ToSharpCoreAgentOptions()
            .ToAgentOptions();
        var without = CliOptions.Parse(["run", "hello"], NoEnvironment)
            .ToSharpCoreAgentOptions()
            .ToAgentOptions();

        Assert.Contains("Using the MCP servers", with.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Using the MCP servers", without.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBrowserFlagStartsThePlaywrightServer()
    {
        var run = CliOptions.Parse(["run", "open the page", "--browser"], NoEnvironment);
        var chat = CliOptions.Parse(["chat", "--browser"], NoEnvironment);

        Assert.True(run.Browser);
        Assert.True(chat.Browser);
        Assert.Empty(run.McpServers);
        Assert.Contains(CliOptions.BrowserServerCommand, run.ToSharpCoreAgentOptions().McpServers);
        Assert.Contains(CliOptions.BrowserServerCommand, chat.ToSharpCoreAgentOptions().McpServers);
    }

    [Theory]
    [InlineData("models")]
    [InlineData("index")]
    [InlineData("config")]
    [InlineData("setup")]
    public void TheBrowserFlagIsRefusedWhereNoTurnRuns(string command)
    {
        var arguments = command == "index"
            ? new[] { command, "--status", "--browser" }
            : [command, "--browser"];

        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(arguments, NoEnvironment));

        Assert.Contains("--browser applies there too", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBrowserFlagAndAnExplicitServerStayInOrder()
    {
        var options = CliOptions.Parse(
            ["run", "hello", "--browser", "--mcp", "npx -y one"],
            NoEnvironment);

        Assert.Equal(["npx -y one", CliOptions.BrowserServerCommand], options.ToSharpCoreAgentOptions().McpServers);
    }

    private static string? NoEnvironment(string name) => null;
}
