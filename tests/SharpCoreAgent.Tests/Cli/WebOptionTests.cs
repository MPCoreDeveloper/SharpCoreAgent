using SharpCoreAgent.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// <c>--web</c> is the only switch that lets a process reach the internet, so its default, its two spellings and the
/// commands that refuse it are part of the command-line contract rather than an implementation detail.
/// </summary>
public sealed class WebOptionTests
{
    [Fact]
    public void TheNetworkIsOffUnlessItIsAskedFor()
    {
        var options = CliOptions.Parse(["chat"], NoEnvironment);

        Assert.False(options.Web);
        Assert.Empty(options.WebAllowedHosts);
        Assert.False(options.ToSharpCoreAgentOptions().WebAccessEnabled);
        Assert.Empty(options.ToSharpCoreAgentOptions().WebAllowedHosts);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("chat")]
    public void TheFlagTurnsTheNetworkOnForTheTurnCommands(string command)
    {
        var arguments = command == "run" ? new[] { "run", "hello", "--web" } : ["chat", "--web"];

        var options = CliOptions.Parse(arguments, NoEnvironment);

        Assert.True(options.Web);
        Assert.True(options.ToSharpCoreAgentOptions().WebAccessEnabled);
    }

    [Fact]
    public void ANamedHostIsKeptInTheOrderItWasGiven()
    {
        var options = CliOptions.Parse(
            ["run", "hello", "--web", "--web-allow", "localhost", "--web-allow", "api.internal:8080"],
            NoEnvironment);

        Assert.Equal(["localhost", "api.internal:8080"], options.WebAllowedHosts);
        Assert.Equal(["localhost", "api.internal:8080"], options.ToSharpCoreAgentOptions().WebAllowedHosts);
    }

    [Theory]
    [InlineData("models", "--web")]
    [InlineData("index", "--web")]
    [InlineData("models", "--web-allow")]
    public void TheCommandsWithoutAWorkspaceRefuseTheFlags(string command, string flag)
    {
        var arguments = flag == "--web" ? new[] { command, "--web" } : [command, "--web-allow", "localhost"];

        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(arguments, NoEnvironment));

        Assert.Contains("apply to run and chat", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHostWithoutTheSwitchIsRefusedRatherThanIgnored()
    {
        var error = Assert.Throws<CliUsageException>(
            () => CliOptions.Parse(["run", "hello", "--web-allow", "localhost"], NoEnvironment));

        Assert.Contains("--web-allow needs --web", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoEnvironmentVariableTurnsTheNetworkOn()
    {
        // The decision the spec records: an inherited variable that opened the internet in a process nobody watched
        // would be the "authority nobody remembers granting" this program keeps refusing to create.
        var options = CliOptions.Parse(
            ["run", "hello"],
            name => name switch
            {
                "SHARPCOREAGENT_WEB" => "1",
                "SHARPCOREAGENT_WEB_ALLOW" => "localhost",
                _ => null,
            });

        Assert.False(options.Web);
        Assert.Empty(options.WebAllowedHosts);
    }

    [Fact]
    public void TheSwitchIsNotASettablePreference()
    {
        Assert.DoesNotContain("web", CliOptions.SettableNames);
        Assert.Throws<CliUsageException>(() => CliOptions.EnsureSettable("web"));
    }

    private static string? NoEnvironment(string name) => null;
}
