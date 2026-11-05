using SharpCoreAgent.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// <c>ide</c> is asked which of two files, and answers without opening an index, a settings document or a socket.
/// </summary>
/// <remarks>
/// Everything it will not do is refused at parse time, so nothing is half written. The refusals are the ones <c>ui</c>
/// and <c>mcp config</c> make — a flag that names a turn, a client, a network or an index action on a command that does
/// none of those — restated where the message has to name <c>ide</c> rather than the command it came from.
/// </remarks>
[Collection(CliConsole.Name)]
public sealed class IdeCommandParsingTests
{
    [Theory]
    [InlineData("agent", "Agent")]
    [InlineData("tasks", "Tasks")]
    [InlineData("prompt", "Prompt")]
    [InlineData("instructions", "Instructions")]
    [InlineData("AGENT", "Agent")]
    [InlineData("Tasks", "Tasks")]
    public void TheActionIsParsedAndTheWriteFlagDefaultsOff(string operand, string expected)
    {
        var options = CliOptions.Parse(["ide", operand], NoEnvironment);

        Assert.Equal(CliCommand.Ide, options.Command);
        Assert.Equal(expected, options.Ide.ToString());

        // Printing is the default for the same reason `mcp config` prints: the bytes are the answer.
        Assert.False(options.Write);
        Assert.True(options.IndexEnabled);
    }

    [Fact]
    public void TheWriteFlagParsesBesideTheAction()
    {
        var options = CliOptions.Parse(["ide", "agent", "--write"], NoEnvironment);

        Assert.Equal(CliCommand.Ide, options.Command);
        Assert.Equal(IdeAction.Agent, options.Ide);
        Assert.True(options.Write);
    }

    [Fact]
    public void NoActionIsAUsageError()
    {
        // Two files, so neither is a default: a command with no action must not pick one.
        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["ide"], NoEnvironment));

        Assert.Equal("ide needs an action.", error.Message);
        Assert.Contains("agent, prompt, instructions and tasks", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownActionNamesBoth()
    {
        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["ide", "serve"], NoEnvironment));

        Assert.Equal("'serve' is not an ide action.", error.Message);
        Assert.Contains("agent, prompt, instructions and tasks", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOperandAfterTheActionIsRefused()
    {
        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["ide", "agent", "extra"], NoEnvironment));

        Assert.Equal("ide does not take 'extra'.", error.Message);
        Assert.Contains("--workspace", error.Hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--client")]
    [InlineData("--json")]
    [InlineData("--no-index")]
    [InlineData("--no-refresh")]
    [InlineData("--web")]
    [InlineData("--status")]
    [InlineData("--rebuild")]
    [InlineData("--add-root")]
    [InlineData("--serve")]
    [InlineData("--port")]
    public void TheFlagsThatDescribeSomethingElseAreRefused(string flag)
    {
        // Each with a value where one is needed, so the refusal is about the flag rather than about a missing operand.
        string[] arguments = flag is "--client" or "--port" or "--add-root"
            ? ["ide", "agent", flag, "1"]
            : ["ide", "agent", flag];

        Assert.Throws<CliUsageException>(() => CliOptions.Parse(arguments, NoEnvironment));
    }

    [Theory]
    [InlineData("--model", "x")]
    [InlineData("--provider", "demo")]
    [InlineData("--session", "x.json")]
    [InlineData("--fallback", "demo:x")]
    public void TheFlagsThatNameAModelOrATranscriptAreRefused(string flag, string value)
    {
        // The refusal that has to be read at parse time: only there is it known which flags the developer typed. Each
        // value is one the flag accepts on its own, so the refusal is about `ide` rather than about a bad operand.
        var error = Assert.Throws<CliUsageException>(
            () => CliOptions.Parse(["ide", "tasks", flag, value], NoEnvironment));

        Assert.Contains("ide runs no model", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoIndexSaysWhyItDoesNotApply()
    {
        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["ide", "agent", "--no-index"], NoEnvironment));

        Assert.Equal("--no-index does not apply to ide.", error.Message);
        Assert.Contains("opens no index", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonSaysWhatIdePrintsInstead()
    {
        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["ide", "agent", "--json"], NoEnvironment));

        Assert.Equal("--json is only valid with run, index and ui.", error.Message);
        Assert.Contains("redirect", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void TheClientFlagIsStillRefusedWhereItBelongs()
    {
        // The other half of `ide` accepting `--write`: a client on `ide` is a mistake, and a write on `run` still is.
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(["ide", "agent", "--client", "vscode"], NoEnvironment));
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(["run", "hello", "--write"], NoEnvironment));
    }

    private static string? NoEnvironment(string name) => null;
}
