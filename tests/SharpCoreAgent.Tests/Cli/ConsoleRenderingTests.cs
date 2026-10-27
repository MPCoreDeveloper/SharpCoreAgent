using System.Text.Json;
using SharpCoreAgent.Cli;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// The terminal side of the CLI: what a developer sees, what a pipe captures and what <c>--json</c> promises.
/// </summary>
/// <remarks>
/// The streams are injected, so these tests assert the product's output contract without a console: answer text
/// on stdout, everything else on stderr, one JSON object per line in machine mode.
/// </remarks>
public sealed class ConsoleRenderingTests
{
    [Fact]
    public async Task AnswerTextGoesToStdoutAndTheSummaryToStderr()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var observer = new ConsoleObserver(output, error, json: false, quiet: false);

        await observer.OnEventAsync(new TurnStarted("demo:demo-scripted"), TestContext.Current.CancellationToken);
        await observer.OnEventAsync(new AgentTextDelta("Hello"), TestContext.Current.CancellationToken);
        await observer.OnEventAsync(new TurnFinished(Report()), TestContext.Current.CancellationToken);

        Assert.Equal("Hello" + Environment.NewLine, output.ToString());
        Assert.Contains("— completed", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThinkingIsPrefixedAndClosedBeforeTheAnswer()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var observer = new ConsoleObserver(output, error, json: false, quiet: false);

        await observer.OnEventAsync(new AgentThinkingDelta("weighing it up"), TestContext.Current.CancellationToken);
        await observer.OnEventAsync(new AgentTextDelta("answer"), TestContext.Current.CancellationToken);

        Assert.StartsWith("· weighing it up", error.ToString(), StringComparison.Ordinal);
        Assert.Equal("answer", output.ToString());
    }

    [Fact]
    public async Task QuietKeepsOnlyTheAnswer()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var observer = new ConsoleObserver(output, error, json: false, quiet: true);

        await observer.OnEventAsync(new AgentThinkingDelta("hidden"), TestContext.Current.CancellationToken);
        await observer.OnEventAsync(new ToolRequested("call-1", "read_file", """{"path":"a.txt"}"""), TestContext.Current.CancellationToken);
        await observer.OnEventAsync(new ToolFinished("call-1", "read_file", false, "ok"), TestContext.Current.CancellationToken);
        await observer.OnEventAsync(new AgentTextDelta("only me"), TestContext.Current.CancellationToken);
        await observer.OnEventAsync(new TurnFinished(Report()), TestContext.Current.CancellationToken);

        Assert.Equal($"only me{Environment.NewLine}", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task AnEmptyDeltaWritesNothing()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var observer = new ConsoleObserver(output, error, json: false, quiet: false);

        await observer.OnEventAsync(new AgentTextDelta(string.Empty), TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task TextThatAlreadyEndsItsLineDoesNotGetASecondOne()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var observer = new ConsoleObserver(output, error, json: false, quiet: false);

        await observer.OnEventAsync(new AgentTextDelta($"already ended{Environment.NewLine}"), TestContext.Current.CancellationToken);
        await observer.OnEventAsync(new TurnFinished(Report()), TestContext.Current.CancellationToken);

        Assert.Equal($"already ended{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public async Task ToolLinesShowWhatWasCalledAndHowItWent()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var observer = new ConsoleObserver(output, error, json: false, quiet: false);

        await observer.OnEventAsync(
            new ToolRequested("call-1", "read_file", """{"path":"a.txt"}"""),
            TestContext.Current.CancellationToken);
        await observer.OnEventAsync(
            new ToolFinished("call-1", "read_file", false, "read 5 bytes"),
            TestContext.Current.CancellationToken);

        var text = output.ToString();
        Assert.Contains("→ read_file(", text, StringComparison.Ordinal);
        Assert.Contains("a.txt", text, StringComparison.Ordinal);
        Assert.Contains("← read_file: ok (", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedToolIsReportedAsAnError()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var observer = new ConsoleObserver(output, error, json: false, quiet: false);

        await observer.OnEventAsync(new ToolFinished("call-2", "write_file", true, "denied"), TestContext.Current.CancellationToken);

        Assert.Contains("← write_file: error (", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LongArgumentsAreCompacted()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var observer = new ConsoleObserver(output, error, json: false, quiet: false);
        var arguments = $"{{\"content\":\"{new string('z', 400)}\"}}";

        await observer.OnEventAsync(new ToolRequested("call-1", "write_file", arguments), TestContext.Current.CancellationToken);

        Assert.Contains("…", output.ToString(), StringComparison.Ordinal);
        Assert.True(output.ToString().Length < 200, output.ToString());
    }

    [Fact]
    public async Task ArgumentsThatAreNotJsonAreShownAsTheyArrived()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var observer = new ConsoleObserver(output, error, json: false, quiet: false);

        await observer.OnEventAsync(new ToolRequested("call-1", "run_shell_command", "not json at all"), TestContext.Current.CancellationToken);

        Assert.Contains("not json at all", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheReportsTextIsPrintedWhenNoDeltaArrived()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var observer = new ConsoleObserver(output, error, json: false, quiet: false);

        await observer.OnEventAsync(new TurnFinished(Report(finalText: "buffered answer")), TestContext.Current.CancellationToken);

        Assert.Contains("buffered answer", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDetailIsPrintedUnderTheSummary()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var observer = new ConsoleObserver(output, error, json: false, quiet: false);

        await observer.OnEventAsync(
            new TurnFinished(Report(AgentOutcome.BudgetExhausted, detail: "the iteration budget ran out")),
            TestContext.Current.CancellationToken);

        Assert.Contains("— budget exhausted", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("the iteration budget ran out", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonModeEmitsOneObjectPerEvent()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var observer = new ConsoleObserver(output, error, json: true, quiet: false);

        await observer.OnEventAsync(new TurnStarted("demo:demo-scripted"), TestContext.Current.CancellationToken);
        await observer.OnEventAsync(new AgentThinkingDelta("hmm"), TestContext.Current.CancellationToken);
        await observer.OnEventAsync(new AgentTextDelta("hi"), TestContext.Current.CancellationToken);
        await observer.OnEventAsync(new ToolRequested("call-1", "read_file", """{"path":"a.txt"}"""), TestContext.Current.CancellationToken);
        await observer.OnEventAsync(new ToolFinished("call-1", "read_file", false, "ok"), TestContext.Current.CancellationToken);
        await observer.OnEventAsync(new TurnFinished(Report()), TestContext.Current.CancellationToken);

        var lines = output.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(6, lines.Length);

        using var started = JsonDocument.Parse(lines[0]);
        Assert.Equal("turn_started", started.RootElement.GetProperty("type").GetString());
        Assert.Equal("demo:demo-scripted", started.RootElement.GetProperty("model").GetString());

        using var thinking = JsonDocument.Parse(lines[1]);
        Assert.Equal("thinking_delta", thinking.RootElement.GetProperty("type").GetString());

        using var text = JsonDocument.Parse(lines[2]);
        Assert.Equal("text_delta", text.RootElement.GetProperty("type").GetString());

        using var tool = JsonDocument.Parse(lines[3]);
        Assert.Equal("tool_started", tool.RootElement.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Object, tool.RootElement.GetProperty("arguments").ValueKind);

        using var finished = JsonDocument.Parse(lines[4]);
        Assert.True(finished.RootElement.GetProperty("ok").GetBoolean());

        using var report = JsonDocument.Parse(lines[5]);
        Assert.Equal("report", report.RootElement.GetProperty("type").GetString());
        Assert.Equal("completed", report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Array, report.RootElement.GetProperty("files").ValueKind);

        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task JsonModeKeepsUnparseableArgumentsAsText()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var observer = new ConsoleObserver(output, error, json: true, quiet: false);

        await observer.OnEventAsync(new ToolRequested("call-1", "run_shell_command", "oops"), TestContext.Current.CancellationToken);

        using var line = JsonDocument.Parse(output.ToString().Trim());
        Assert.Equal(JsonValueKind.String, line.RootElement.GetProperty("arguments").ValueKind);
        Assert.Equal("oops", line.RootElement.GetProperty("arguments").GetString());
    }

    [Fact]
    public async Task AnInteractiveYesApprovesTheCall()
    {
        using var error = new StringWriter();
        var approver = new ConsoleApprover(new StringReader("Yes\n"), error, () => true);

        var outcome = await approver.DecideAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(ApprovalOutcome.Approved, outcome);
        Assert.Contains("allow read_file?", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnythingOtherThanYesRefuses()
    {
        foreach (var answer in new[] { "n\n", "\n", "sure\n", "yess\n" })
        {
            using var error = new StringWriter();
            var approver = new ConsoleApprover(new StringReader(answer), error, () => true);

            var outcome = await approver.DecideAsync(Request(), TestContext.Current.CancellationToken);

            Assert.Equal(ApprovalOutcome.Denied, outcome);
            Assert.Contains("the developer declined", error.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task EndOfInputRefuses()
    {
        using var error = new StringWriter();
        var approver = new ConsoleApprover(new StringReader(string.Empty), error, () => true);

        Assert.Equal(ApprovalOutcome.Denied, await approver.DecideAsync(Request(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ANonInteractiveTerminalRefusesWithoutAsking()
    {
        using var error = new StringWriter();
        var input = new StringReader("y\n");
        var approver = new ConsoleApprover(input, error, () => false);

        var outcome = await approver.DecideAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(ApprovalOutcome.Denied, outcome);
        Assert.Contains("not interactive", error.ToString(), StringComparison.Ordinal);
        Assert.Equal('y', (char)input.Read());
    }

    [Fact]
    public async Task ACancelledApprovalThrows()
    {
        using var error = new StringWriter();
        var approver = new ConsoleApprover(new StringReader("y\n"), error, () => true);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => approver.DecideAsync(Request(), cancellation.Token).AsTask());
    }

    [Fact]
    public async Task ANullApprovalRequestIsRejected()
    {
        using var error = new StringWriter();
        var approver = new ConsoleApprover(new StringReader("y\n"), error, () => true);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => approver.DecideAsync(null!, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public void TheFirstCtrlCPressCancelsOnlyTheTurn()
    {
        using var scope = new CancellationScope();
        var turn = scope.BeginTurn();

        scope.HandleCancelKeyPress();

        Assert.True(turn.IsCancellationRequested);
        Assert.False(scope.ExitToken.IsCancellationRequested);
    }

    [Fact]
    public void ASecondCtrlCPressEndsTheProgram()
    {
        using var scope = new CancellationScope();
        _ = scope.BeginTurn();

        scope.HandleCancelKeyPress();
        scope.HandleCancelKeyPress();

        Assert.True(scope.ExitToken.IsCancellationRequested);
    }

    [Fact]
    public void APressWithNoTurnRunningEndsTheProgram()
    {
        using var scope = new CancellationScope();

        scope.HandleCancelKeyPress();

        Assert.True(scope.ExitToken.IsCancellationRequested);
    }

    [Fact]
    public void AfterTheTurnEndsAPressMeansLeave()
    {
        using var scope = new CancellationScope();
        _ = scope.BeginTurn();
        scope.EndTurn();

        scope.HandleCancelKeyPress();

        Assert.True(scope.ExitToken.IsCancellationRequested);
    }

    [Theory]
    [InlineData(AgentOutcome.Completed, 0)]
    [InlineData(AgentOutcome.Failed, 1)]
    [InlineData(AgentOutcome.BudgetExhausted, 4)]
    [InlineData(AgentOutcome.Cancelled, 4)]
    public void EveryOutcomeMapsToItsDocumentedExitCode(AgentOutcome outcome, int expected) =>
        Assert.Equal(expected, ExitCodes.ForReport(Report(outcome)));

    [Fact]
    public async Task ToolOutputGoesToStderrBehindAGutter()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var observer = new ConsoleObserver(output, error, json: false, quiet: false);

        await observer.OnEventAsync(
            new ToolOutputDelta("call-1", "run_shell", "building\nlinking\n"),
            TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("│ building", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("│ linking", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnfinishedToolOutputLineIsClosedBeforeTheToolLine()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var observer = new ConsoleObserver(output, error, json: false, quiet: false);

        await observer.OnEventAsync(
            new ToolOutputDelta("call-1", "run_shell", "no newline"),
            TestContext.Current.CancellationToken);
        await observer.OnEventAsync(
            new ToolFinished("call-1", "run_shell", false, "ok"),
            TestContext.Current.CancellationToken);

        var text = error.ToString();
        Assert.Contains("│ no newline" + Environment.NewLine, text, StringComparison.Ordinal);
        Assert.DoesNotContain("← run_shell", text, StringComparison.Ordinal);

        // The tool's own line still goes to stdout, as every tool diagnostic does.
        Assert.Contains("← run_shell", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonModeEmitsToolOutputAsItsOwnLine()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var observer = new ConsoleObserver(output, error, json: true, quiet: false);

        await observer.OnEventAsync(
            new ToolOutputDelta("call-1", "run_shell", "compiling"),
            TestContext.Current.CancellationToken);

        using var line = JsonDocument.Parse(output.ToString().Trim());
        Assert.Equal("tool_output", line.RootElement.GetProperty("type").GetString());
        Assert.Equal("call-1", line.RootElement.GetProperty("callId").GetString());
        Assert.Equal("run_shell", line.RootElement.GetProperty("tool").GetString());
        Assert.Equal("compiling", line.RootElement.GetProperty("text").GetString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task QuietSuppressesToolOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var observer = new ConsoleObserver(output, error, json: false, quiet: true);

        await observer.OnEventAsync(
            new ToolOutputDelta("call-1", "run_shell", "hidden\n"),
            TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    private static ToolApprovalRequest Request() =>
        new("read_file", ToolSafety.ReadOnly, "read a.txt", """{"path":"a.txt"}""");

    private static AgentReport Report(
        AgentOutcome outcome = AgentOutcome.Completed,
        string? finalText = null,
        string? detail = null) =>
        new()
        {
            Outcome = outcome,
            ModelId = "demo:demo-scripted",
            FinalText = finalText,
            Usage = new TokenUsage(10, 5),
            Iterations = 1,
            Detail = detail,
        };
}
