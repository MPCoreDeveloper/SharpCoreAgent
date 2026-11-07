using SharpCoreAgent.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// The <c>serve</c> command's parsing rules: it is chat with a port, and it refuses the panel's own two flags.
/// </summary>
/// <remarks>
/// <c>serve</c> keeps every flag chat keeps — a model, an approval mode, the web, the roots — because it runs a turn. What
/// it adds is <c>--port</c>, shared with <c>ui</c>, and what it refuses is <c>--serve</c> and <c>--refresh</c>, which name
/// the read-only panel the command name has already displaced.
/// </remarks>
public sealed class ServeCommandParsingTests
{
    [Fact]
    public void ServeIsItsOwnCommandAndTakesThePort()
    {
        var options = CliOptions.Parse(["serve", "--port", "8080"], NoEnvironment);

        Assert.Equal(CliCommand.Serve, options.Command);
        Assert.Equal(8080, options.UiPort);
    }

    [Fact]
    public void ServeKeepsTheFlagsAChatKeeps()
    {
        var options = CliOptions.Parse(["serve", "--provider", "grok", "--approval", "ask", "--web"], NoEnvironment);

        Assert.Equal(CliCommand.Serve, options.Command);
        Assert.Equal("grok", options.Provider);
        Assert.Equal(SharpCoreAgent.Domain.Agents.ApprovalMode.Ask, options.Approval);
        Assert.True(options.Web);
    }

    [Fact]
    public void ServeTakesNoPositional()
    {
        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["serve", "please"], NoEnvironment));

        Assert.Contains("serve does not take 'please'.", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--serve")]
    [InlineData("--refresh")]
    public void ThePanelsOwnFlagsBelongToUi(string flag)
    {
        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["serve", flag], NoEnvironment));

        Assert.Contains("belong to 'ui'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnImageOnServeIsRefusedBecausePicturesArrivePerTurn()
    {
        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["serve", "--image", "a.png"], NoEnvironment));

        Assert.Contains("--image applies to run", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResumeAndSessionTogetherAreRefused()
    {
        var error = Assert.Throws<CliUsageException>(() =>
            CliOptions.Parse(["serve", "--resume", "r.json", "--session", "s.json"], NoEnvironment));

        Assert.Contains("--resume and --session name the same transcript", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheJsonObjectIsAllowedBecauseTheStartupLineIsOne()
    {
        var options = CliOptions.Parse(["serve", "--json"], NoEnvironment);

        Assert.True(options.Json);
    }

    private static string? NoEnvironment(string name) => null;
}
