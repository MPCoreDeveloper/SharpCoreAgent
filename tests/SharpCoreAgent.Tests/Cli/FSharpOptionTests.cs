using SharpCoreAgent.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// <c>--fsharp</c> is the only switch that puts a type checker in the process, so its default and its mapping onto the
/// container's options are part of the command-line contract.
/// </summary>
public sealed class FSharpOptionTests
{
    [Fact]
    public void TheCheckerIsOffUnlessItIsAskedFor()
    {
        var options = CliOptions.Parse(["chat"], NoEnvironment);

        Assert.False(options.FSharp);
        Assert.False(options.ToSharpCoreAgentOptions().FSharpToolsEnabled);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("chat")]
    public void TheFlagTurnsTheCheckerOnForTheTurnCommands(string command)
    {
        var arguments = command == "run" ? new[] { "run", "hello", "--fsharp" } : ["chat", "--fsharp"];

        var options = CliOptions.Parse(arguments, NoEnvironment);

        Assert.True(options.FSharp);
        Assert.True(options.ToSharpCoreAgentOptions().FSharpToolsEnabled);
    }

    private static string? NoEnvironment(string name) => null;
}
