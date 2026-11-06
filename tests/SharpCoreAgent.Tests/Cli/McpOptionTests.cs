using SharpCoreAgent.Cli;
using SharpCoreAgent.Infrastructure.Mcp;
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

    [Fact]
    public void SamplingIsDeniedUnlessTheCommandLineSaysOtherwise()
    {
        var options = CliOptions.Parse(["run", "hello"], NoEnvironment);

        Assert.Equal(McpSamplingPolicy.Deny, options.McpSampling);
        Assert.Equal(McpSamplingPolicy.Deny, options.ToSharpCoreAgentOptions().McpSampling);
    }

    [Theory]
    [InlineData("deny", McpSamplingPolicy.Deny)]
    [InlineData("first", McpSamplingPolicy.First)]
    [InlineData("unrestricted", McpSamplingPolicy.Unrestricted)]
    [InlineData("FIRST", McpSamplingPolicy.First)]
    public void SamplingPolicyIsParsedCaseInsensitively(string value, McpSamplingPolicy expected)
    {
        var options = CliOptions.Parse(["run", "hello", "--mcp-sampling", value], NoEnvironment);

        Assert.Equal(expected, options.McpSampling);
        Assert.Equal(expected, options.ToSharpCoreAgentOptions().McpSampling);
    }

    [Fact]
    public void AnUnknownSamplingPolicyIsRefusedWithTheThreeNames()
    {
        var error = Assert.Throws<CliUsageException>(
            () => CliOptions.Parse(["run", "hello", "--mcp-sampling", "always"], NoEnvironment));

        Assert.Contains("unknown MCP sampling policy 'always'", error.Message, StringComparison.Ordinal);
        Assert.Contains("deny, first or unrestricted", error.Hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("models")]
    [InlineData("index")]
    [InlineData("config")]
    [InlineData("setup")]
    public void SamplingIsRefusedWhereNoTurnRuns(string command)
    {
        var arguments = command == "index"
            ? new[] { command, "--status", "--mcp-sampling", "first" }
            : [command, "--mcp-sampling", "first"];

        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(arguments, NoEnvironment));

        Assert.Contains("--mcp-sampling applies there too", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARemoteUrlIsParsedForTheTurnCommands()
    {
        var options = CliOptions.Parse(
            ["run", "hello", "--mcp-url", "https://mcp.example/rpc"],
            NoEnvironment);

        Assert.Equal(["https://mcp.example/rpc"], options.McpUrls);
        Assert.Equal(["https://mcp.example/rpc"], options.ToSharpCoreAgentOptions().McpUrls);
    }

    [Fact]
    public void ARemoteUrlMustBeAbsoluteHttpOrHttps()
    {
        var relative = Assert.Throws<CliUsageException>(
            () => CliOptions.Parse(["run", "hello", "--mcp-url", "/rpc"], NoEnvironment));
        var ftp = Assert.Throws<CliUsageException>(
            () => CliOptions.Parse(["run", "hello", "--mcp-url", "ftp://mcp.example/rpc"], NoEnvironment));

        Assert.Contains("not an absolute http or https MCP URL", relative.Message, StringComparison.Ordinal);
        Assert.Contains("not an absolute http or https MCP URL", ftp.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARemoteUrlIsRefusedWhereNoTurnRuns()
    {
        var error = Assert.Throws<CliUsageException>(
            () => CliOptions.Parse(["models", "--mcp-url", "https://mcp.example/rpc"], NoEnvironment));

        Assert.Contains("--mcp-url", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALegacySseUrlIsParsedForTheTurnCommands()
    {
        var options = CliOptions.Parse(
            ["chat", "--mcp-sse-url", "https://mcp.example/sse"],
            NoEnvironment);

        Assert.Equal(["https://mcp.example/sse"], options.McpSseUrls);
        Assert.Equal(["https://mcp.example/sse"], options.ToSharpCoreAgentOptions().McpSseUrls);
    }

    [Fact]
    public void ALegacySseUrlIsRefusedWhereNoTurnRuns()
    {
        var error = Assert.Throws<CliUsageException>(
            () => CliOptions.Parse(["models", "--mcp-sse-url", "https://mcp.example/sse"], NoEnvironment));

        Assert.Contains("--mcp-sse-url", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OAuthFlagsBuildAValidatedFlow()
    {
        var options = CliOptions.Parse(
            [
                "run", "hello",
                "--mcp-url", "https://mcp.example/rpc",
                "--mcp-oauth-client", "sharp",
                "--mcp-oauth-auth", "https://id.example/authorize",
                "--mcp-oauth-token", "https://id.example/token",
                "--mcp-oauth-scope", "mcp.read",
            ],
            NoEnvironment);

        var oauth = options.CreateMcpOAuth();
        Assert.Equal("sharp", oauth.ClientId);
        Assert.Equal("https://id.example/authorize", oauth.AuthorizationEndpoint?.AbsoluteUri);
        Assert.Equal("mcp.read", oauth.Scope);
    }

    [Fact]
    public void APartialOAuthConfigurationIsRefused()
    {
        var options = CliOptions.Parse(
            ["run", "hello", "--mcp-oauth-client", "sharp"],
            NoEnvironment);

        var error = Assert.Throws<CliUsageException>(() => options.CreateMcpOAuth());
        Assert.Contains("endpoints, an issuer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AServerFileIsParsedOnlyForTheTurnCommands()
    {
        var path = Path.Combine(Path.GetTempPath(), "sca-mcp-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{"mcpServers":{"files":{"command":"npx","args":["-y","server"]}}}""");
            var options = CliOptions.Parse(["run", "hello", "--mcp-file", path], NoEnvironment);
            var mapped = options.ToSharpCoreAgentOptions();

            var definition = Assert.Single(mapped.McpServerDefinitions);
            Assert.Equal("files", definition.Name);
            Assert.Equal("npx -y server", definition.CommandLine);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AServerFileIsRefusedWhereNoTurnRuns()
    {
        var error = Assert.Throws<CliUsageException>(
            () => CliOptions.Parse(["models", "--mcp-file", "mcp.json"], NoEnvironment));

        Assert.Contains("--mcp-file", error.Message, StringComparison.Ordinal);
    }

    private static string? NoEnvironment(string name) => null;
}
