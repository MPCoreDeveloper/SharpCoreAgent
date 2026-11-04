using SharpCoreAgent.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary><c>--no-csharp</c> is the process-local opt-out from the default Roslyn adapter.</summary>
public sealed class CSharpOptionTests
{
    [Fact]
    public void TheCompilerIsOnByDefault()
    {
        var options = CliOptions.Parse(["chat"], NoEnvironment);

        Assert.True(options.CSharp);
        Assert.True(options.ToSharpCoreAgentOptions().CSharpToolsEnabled);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("chat")]
    public void TheFlagRemovesTheCSharpAdapterForTheTurnCommands(string command)
    {
        var arguments = command == "run"
            ? new[] { "run", "hello", "--no-csharp" }
            : ["chat", "--no-csharp"];

        var options = CliOptions.Parse(arguments, NoEnvironment);

        Assert.False(options.CSharp);
        Assert.False(options.ToSharpCoreAgentOptions().CSharpToolsEnabled);
    }

    private static string? NoEnvironment(string name) => null;
}
