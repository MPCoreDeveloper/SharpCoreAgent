using SharpCoreAgent.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary><c>--no-git</c> is the process-local opt-out from the default read-only Git evidence tools.</summary>
public sealed class GitOptionTests
{
    [Fact]
    public void GitIsOnByDefault()
    {
        var options = CliOptions.Parse(["chat"], NoEnvironment);

        Assert.True(options.Git);
        Assert.True(options.ToSharpCoreAgentOptions().GitToolsEnabled);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("chat")]
    public void TheFlagRemovesGitEvidenceForTheTurnCommands(string command)
    {
        var arguments = command == "run"
            ? new[] { "run", "hello", "--no-git" }
            : ["chat", "--no-git"];

        var options = CliOptions.Parse(arguments, NoEnvironment);

        Assert.False(options.Git);
        Assert.False(options.ToSharpCoreAgentOptions().GitToolsEnabled);
        Assert.Contains("--no-git", HelpText.Usage, StringComparison.Ordinal);
    }

    private static string? NoEnvironment(string name) => null;
}
