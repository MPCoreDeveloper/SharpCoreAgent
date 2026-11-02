using SharpCoreAgent.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// The <c>ui</c> command's parsing rules: which flags it takes, and every combination the contract refuses.
/// </summary>
/// <remarks>
/// Each refusal is asserted by message rather than only by exception type, because the message is what a developer reads
/// and what tells them which flag was in the wrong place. The defaults matter as much: <c>--refresh</c> off is the safe
/// behaviour FR-007 names, and a parser that quietly defaulted it on would walk a tree on every page load.
/// </remarks>
public sealed class UiCommandParsingTests
{
    [Fact]
    public void UiWithoutServeIsARefusalRatherThanAServer()
    {
        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["ui"], NoEnvironment));

        Assert.Equal("ui needs --serve.", error.Message);
        Assert.Equal("For example: sharpcoreagent ui --serve --port 0", error.Hint);
    }

    [Fact]
    public void ServeIsAcceptedWithTheSafeDefaults()
    {
        var options = CliOptions.Parse(["ui", "--serve"], NoEnvironment);

        Assert.Equal(CliCommand.Ui, options.Command);
        Assert.True(options.UiServe);
        Assert.Equal(0, options.UiPort);
        Assert.False(options.UiRefresh);
        Assert.True(options.IndexEnabled);
        Assert.Equal(3000, options.IndexBudgetMs);
    }

    [Fact]
    public void AVerbAfterUiIsRefusedTheWayMcpWouldHaveTakenIt()
    {
        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["ui", "serve"], NoEnvironment));

        Assert.Equal("ui does not take 'serve'.", error.Message);
        Assert.Equal("For example: sharpcoreagent ui --serve", error.Hint);
    }

    [Fact]
    public void ThePortAndTheRefreshAreParsed()
    {
        var options = CliOptions.Parse(["ui", "--serve", "--port", "8080", "--refresh"], NoEnvironment);

        Assert.Equal(8080, options.UiPort);
        Assert.True(options.UiRefresh);
    }

    [Fact]
    public void PortZeroSaysWhatAbsenceSays()
    {
        Assert.Equal(
            CliOptions.Parse(["ui", "--serve"], NoEnvironment).UiPort,
            CliOptions.Parse(["ui", "--serve", "--port", "0"], NoEnvironment).UiPort);
    }

    [Theory]
    [InlineData("70000")]
    [InlineData("-1")]
    [InlineData("http")]
    [InlineData("80a")]
    public void APortOutsideTheRangeOrNotANumberIsRefused(string port)
    {
        var error = Assert.Throws<CliUsageException>(() =>
            CliOptions.Parse(["ui", "--serve", "--port", port], NoEnvironment));

        Assert.Equal($"option '--port' needs a whole number from 0 to 65535, but got '{port}'.", error.Message);
    }

    [Fact]
    public void TheHighestPortIsAccepted()
    {
        Assert.Equal(65535, CliOptions.Parse(["ui", "--serve", "--port", "65535"], NoEnvironment).UiPort);
    }

    [Fact]
    public void ThePanelsFlagsAreRefusedOnEveryOtherCommand()
    {
        string[][] lines =
        [
            ["run", "hello", "--port", "8080"],
            ["chat", "--port", "8080"],
            ["index", "--port", "8080"],
            ["models", "--port", "8080"],
            ["config", "--port", "8080"],
            ["setup", "--port", "8080"],
            ["mcp", "serve", "--port", "8080"],
            ["mcp", "bench", "--port", "8080"],
        ];

        foreach (var line in lines)
        {
            var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(line, NoEnvironment));
            Assert.Equal("--port 8080 belongs to the ui command.", error.Message);
            Assert.Equal("For example: sharpcoreagent ui --serve --port 8080", error.Hint);
        }
    }

    [Fact]
    public void ServeAndRefreshAreNamedWhenTheyAreTheMisplacedFlag()
    {
        Assert.Equal(
            "--serve belongs to the ui command.",
            Assert.Throws<CliUsageException>(() => CliOptions.Parse(["index", "--serve"], NoEnvironment)).Message);

        Assert.Equal(
            "--refresh belongs to the ui command.",
            Assert.Throws<CliUsageException>(() => CliOptions.Parse(["run", "hello", "--refresh"], NoEnvironment)).Message);
    }

    [Fact]
    public void NoIndexIsRefusedBecauseThePageWouldHaveNothingToShow()
    {
        var error = Assert.Throws<CliUsageException>(() =>
            CliOptions.Parse(["ui", "--serve", "--no-index"], NoEnvironment));

        Assert.Equal("--no-index leaves ui --serve with nothing to show.", error.Message);
    }

    [Fact]
    public void NoRefreshIsRefusedAndPointsAtTheFlagThatAsksForTheWalk()
    {
        var error = Assert.Throws<CliUsageException>(() =>
            CliOptions.Parse(["ui", "--serve", "--no-refresh"], NoEnvironment));

        Assert.StartsWith("--no-refresh applies to mcp serve", error.Message, StringComparison.Ordinal);
        Assert.Equal("For example: sharpcoreagent ui --serve --refresh", error.Hint);
    }

    [Fact]
    public void UiIsNotAnMcpAction()
    {
        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["mcp", "ui"], NoEnvironment));

        Assert.Equal("'ui' is not an mcp action.", error.Message);
        Assert.Equal("The actions are serve, config and bench.", error.Hint);
    }

    [Fact]
    public void JsonIsAcceptedAndKept()
    {
        var options = CliOptions.Parse(["ui", "--serve", "--json"], NoEnvironment);

        Assert.Equal(CliCommand.Ui, options.Command);
        Assert.True(options.Json);
    }

    /// <summary>
    /// FR-013's eleven flags, each on the command they do not belong to.
    /// </summary>
    /// <remarks>
    /// A turn's flags read as a promise when a command quietly accepts them: <c>--model</c> on <c>ui</c> would suggest a
    /// model was asked something, and <c>--write</c> that a file was written. The list is transcribed from the
    /// requirement rather than from the parser, which is what makes it a test of the requirement.
    /// </remarks>
    [Theory]
    [InlineData("--model", "fast")]
    [InlineData("--provider", "grok")]
    [InlineData("--planner", "fast")]
    [InlineData("--executor", "fast")]
    [InlineData("--reviewer", "fast")]
    [InlineData("--session", "turn.json")]
    [InlineData("--approval", "ask")]
    [InlineData("--client", "claude")]
    [InlineData("--write", null)]
    [InlineData("--mcp", "npx -y server")]
    [InlineData("--web", null)]
    public void EveryFlagThatDescribesATurnIsRefusedOnThePanel(string flag, string? value)
    {
        string[] line = value is null ? ["ui", "--serve", flag] : ["ui", "--serve", flag, value];

        var error = Assert.Throws<CliUsageException>(() => CliOptions.Parse(line, NoEnvironment));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void TheShorthandForApprovalIsRefusedTheSameWay()
    {
        var error = Assert.Throws<CliUsageException>(() =>
            CliOptions.Parse(["ui", "--serve", "-y"], NoEnvironment));

        Assert.Equal("ui --serve asks no questions, so --approval does not apply.", error.Message);
    }

    /// <summary>
    /// T016: the note after the URL names both editors' browsers, and the workspace line follows it.
    /// </summary>
    /// <remarks>
    /// The command is run rather than described: the token is cancelled before the call, so the listener is bound and the
    /// three lines are printed and then the loop ends at once. That makes the printed note assertable without opening a
    /// socket, which is the point of <see cref="UiCommand.ExecuteAsync(CliOptions, TextWriter, TextWriter, CancellationToken)"/>'s
    /// writers.
    /// </remarks>
    [Fact]
    public async Task TheNoteNamesBothEditorsAndTheWorkspaceFollowsTheUrl()
    {
        using var sandbox = Tools.ToolTestWorkspace.Create();
        var options = CliOptions.Parse(["ui", "--serve", "--port", "0", "--workspace", sandbox.Root], NoEnvironment);
        var output = new StringWriter();
        var error = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var exitCode = await UiCommand.ExecuteAsync(options, output, error, cancellation.Token);

        string[] lines = error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(output.ToString());
        Assert.StartsWith("http://127.0.0.1:", lines[0], StringComparison.Ordinal);
        Assert.Contains("Simple Browser", lines[1], StringComparison.Ordinal);
        Assert.Contains("Visual Studio", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("note: ", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("note: ", lines[2], StringComparison.Ordinal);
        Assert.Contains(sandbox.Root.Replace('\\', '/'), lines[2], StringComparison.Ordinal);
        Assert.Contains("Ctrl+C", lines[3], StringComparison.Ordinal);
    }

    private static string? NoEnvironment(string name) => null;
}
