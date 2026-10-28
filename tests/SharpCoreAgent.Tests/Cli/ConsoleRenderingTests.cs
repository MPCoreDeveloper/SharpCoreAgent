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
        Assert.Contains("✻ completed", error.ToString(), StringComparison.Ordinal);
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
        Assert.Contains("⏺ read_file", text, StringComparison.Ordinal);
        Assert.Contains("a.txt", text, StringComparison.Ordinal);
        Assert.Contains("⎿ ok", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedToolIsReportedAsAnError()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var observer = new ConsoleObserver(output, error, json: false, quiet: false);

        await observer.OnEventAsync(new ToolFinished("call-2", "write_file", true, "denied"), TestContext.Current.CancellationToken);

        Assert.Contains("⎿ error", output.ToString(), StringComparison.Ordinal);
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

        Assert.Contains("✻ budget exhausted", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("the iteration budget ran out", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The closing line has to carry the spend, because a budget a developer cannot see is the one that surprises
    /// them — and an unlimited ceiling has to read as a word rather than as nineteen digits.
    /// </summary>
    /// <param name="ceiling">The ceiling the observer is told about.</param>
    /// <param name="expected">The text the summary must contain.</param>
    /// <returns>The pending event.</returns>
    [Theory]
    [InlineData(1_000L, "614/1,000 tokens")]
    [InlineData(long.MaxValue, "614 tokens (unlimited)")]
    public async Task TheSummaryShowsWhatTheSessionSpent(long ceiling, string expected)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var observer = new ConsoleObserver(
            output,
            error,
            json: false,
            quiet: false,
            budget: () => (614, ceiling));

        await observer.OnEventAsync(new TurnFinished(Report(AgentOutcome.Completed)), TestContext.Current.CancellationToken);

        Assert.Contains(expected, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreportedUsageIsLeftOutOfTheSummary()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var observer = new ConsoleObserver(output, error, json: false, quiet: false, budget: () => (0, 0));

        await observer.OnEventAsync(
            new TurnFinished(Report(AgentOutcome.BudgetExhausted, usage: TokenUsage.Unknown)),
            TestContext.Current.CancellationToken);

        // The session's own spend is still known even when the endpoint stayed silent about the last turn.
        Assert.Contains("✻ budget exhausted · 1 iteration(s)", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("unknown", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ACompactedTranscriptIsAWarningOnTheDiagnosticsStream()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var observer = new ConsoleObserver(output, error, json: false, quiet: false);

        await observer.OnEventAsync(new ContextCompacted(12, 20), TestContext.Current.CancellationToken);

        // A long session stops being able to answer questions about its own beginning, so the note is a warning
        // rather than a dim aside: "as we discussed" now refers to a shorter discussion than it did a minute ago.
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("! context: dropped 12 old message(s)", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("the last 20", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuietSaysNothingAboutCompactionAndJsonEmitsIt()
    {
        using var quietError = new StringWriter();
        using var quietOutput = new StringWriter();
        var quiet = new ConsoleObserver(quietOutput, quietError, json: false, quiet: true);

        await quiet.OnEventAsync(new ContextCompacted(12, 20), TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, quietError.ToString());
        Assert.Equal(string.Empty, quietOutput.ToString());

        using var output = new StringWriter();
        using var error = new StringWriter();
        var json = new ConsoleObserver(output, error, json: true, quiet: false);

        await json.OnEventAsync(new ContextCompacted(12, 20), TestContext.Current.CancellationToken);

        using var line = JsonDocument.Parse(output.ToString().Trim());
        Assert.Equal("context_compacted", line.RootElement.GetProperty("type").GetString());
        Assert.Equal(12, line.RootElement.GetProperty("dropped").GetInt32());
        Assert.Equal(20, line.RootElement.GetProperty("kept").GetInt32());
        Assert.Equal(string.Empty, error.ToString());
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

        // All four answers are visible before the cursor, so a developer who is tired of typing does
        // not have to read the documentation to discover the one that stops the questions.
        var text = error.ToString();
        Assert.Contains("[y/t/a/N]", text, StringComparison.Ordinal);
        Assert.Contains("t = this tool for the session", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswerToAllowOneToolIsAToolDecision()
    {
        foreach (var answer in new[] { "t\n", "tool\n", "T\n" })
        {
            using var error = new StringWriter();
            var approver = new ConsoleApprover(new StringReader(answer), error, () => true);

            var outcome = await approver.DecideAsync(Request(), TestContext.Current.CancellationToken);

            // Narrower than "every tool": the developer stops being asked about run_shell_command while every
            // file edit still asks, which is the answer two choices could not express.
            Assert.Equal(ApprovalOutcome.ApprovedForTool, outcome);

            // The receipt is the only feedback the developer gets -- the next call of this tool prints nothing.
            Assert.Contains(
                "read_file runs without asking for the rest of this session",
                error.ToString(),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnAmbiguousLongerWordIsNotOneOfTheAnswers()
    {
        // "allow" is one character away from "all" and means the opposite of "one tool", so it must never be
        // read as either standing approval: the prompt offers y, t and a, and a word that is not one of them
        // refuses rather than guessing what the developer meant.
        foreach (var answer in new[] { "allow\n", "tool all\n", "tt\n" })
        {
            using var error = new StringWriter();
            var approver = new ConsoleApprover(new StringReader(answer), error, () => true);

            Assert.Equal(
                ApprovalOutcome.Denied,
                await approver.DecideAsync(Request(), TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task AnAnswerToAllowEveryToolIsASessionDecision()
    {
        foreach (var answer in new[] { "a\n", "all\n", "always\n", "A\n" })
        {
            using var error = new StringWriter();
            var approver = new ConsoleApprover(new StringReader(answer), error, () => true);

            var outcome = await approver.DecideAsync(Request(), TestContext.Current.CancellationToken);

            Assert.Equal(ApprovalOutcome.ApprovedForSession, outcome);
            Assert.Contains("without asking", error.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnythingOtherThanYesRefuses()
    {
        foreach (var answer in new[] { "n\n", "\n", "sure\n", "yess\n", "aye\n", "allow\n" })
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
        Assert.DoesNotContain("⎿", text, StringComparison.Ordinal);

        // The tool's own line still goes to stdout, as every tool diagnostic does.
        Assert.Contains("⎿ ok", output.ToString(), StringComparison.Ordinal);
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

    /// <summary>
    /// A fallback is announced when it happens, not in the closing summary: the developer is about to
    /// read an answer from a provider the prompt did not name.
    /// </summary>
    [Fact]
    public async Task AFallbackIsAnnouncedOnStandardErrorBeforeTheAnswer()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var observer = new ConsoleObserver(output, error, json: false, quiet: false);

        await observer.OnEventAsync(
            new FallbackAttempted(new FallbackUsed(
                "deepseek:deepseek-chat",
                401,
                "deepseek:deepseek-chat returned HTTP 401: invalid key",
                "grok:grok-4.7")),
            TestContext.Current.CancellationToken);
        await observer.OnEventAsync(new AgentTextDelta("hello"), TestContext.Current.CancellationToken);

        var text = error.ToString();
        Assert.Contains("fallback:", text, StringComparison.Ordinal);
        Assert.Contains("deepseek:deepseek-chat failed with HTTP 401", text, StringComparison.Ordinal);
        Assert.Contains("grok:grok-4.7 answered", text, StringComparison.Ordinal);
        Assert.Contains("invalid key", text, StringComparison.Ordinal);
        Assert.Equal("hello", output.ToString());
    }

    private static AgentReport Report(
        AgentOutcome outcome = AgentOutcome.Completed,
        string? finalText = null,
        string? detail = null,
        TokenUsage? usage = null) =>
        new()
        {
            Outcome = outcome,
            ModelId = "demo:demo-scripted",
            FinalText = finalText,
            Usage = usage ?? new TokenUsage(10, 5),
            Iterations = 1,
            Detail = detail,
        };
}
