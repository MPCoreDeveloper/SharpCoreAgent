using System.Runtime.CompilerServices;
using SharpCoreAgent.Application;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Chat;
using Xunit;

namespace SharpCoreAgent.Tests.Application;

/// <summary>
/// The turn loop: how it stops, what it reports, and what it tells the model when a tool cannot run.
/// </summary>
/// <remarks>
/// Every test drives <see cref="CodingAgent"/> with a scripted model and a real tool registry, so the loop is
/// the shipping one and the only fake is the provider. The outcome and the report are the contract the CLI
/// maps onto exit codes, which is why most assertions are about the report rather than about the text.
/// </remarks>
public sealed class CodingAgentTests
{
    private static ModelDescriptor Descriptor => new("demo", "demo-scripted", "Demo");

    [Fact]
    public async Task ATextAnswerCompletesTheTurn()
    {
        using var directory = TempDirectory.Create();
        var model = new ScriptedModel(
            Descriptor,
            [
                new ThinkingDelta("considering"),
                new TextDelta("all "),
                new TextDelta("done"),
                Completed("all done"),
            ]);
        var observer = new RecordingObserver();

        var report = await Agent(model, []).RunTurnAsync(
            Session(directory.Path), "hi", observer, TestContext.Current.CancellationToken);

        Assert.Equal(AgentOutcome.Completed, report.Outcome);
        Assert.Equal("all done", report.FinalText);
        Assert.Equal(1, report.Iterations);
        Assert.Equal(10, report.Usage.InputTokens ?? 0);
        Assert.Contains(observer.Events.OfType<AgentThinkingDelta>(), e => e.Text == "considering");
        Assert.Equal(2, observer.Events.OfType<AgentTextDelta>().Count());
        Assert.IsType<TurnFinished>(observer.Events[^1]);
    }

    [Fact]
    public async Task AModelThatCannotCallToolsIsRefusedBeforeItIsResolved()
    {
        using var directory = TempDirectory.Create();
        var resolver = new StubResolver(_ => throw new InvalidOperationException("the model must not be resolved."));
        var agent = new CodingAgent(
            resolver,
            new ToolRegistry([]),
            new StubApprover(ApprovalOutcome.Approved),
            new AgentOptions());
        var session = new AgentSession(
            Workspace.Create(directory.Path),
            Descriptor with { SupportsTools = false },
            ApprovalMode.AllowAll,
            "system");

        var report = await agent.RunTurnAsync(session, "hi", null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentOutcome.Failed, report.Outcome);
        Assert.Contains("not supporting tool calls", report.Detail!, StringComparison.Ordinal);
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task AProviderThatCannotBeResolvedFailsTheTurnWithItsReason()
    {
        using var directory = TempDirectory.Create();
        var resolver = new StubResolver(_ => throw new ModelCredentialException("DEMO_API_KEY"));

        var report = await new CodingAgent(
                resolver,
                new ToolRegistry([]),
                new StubApprover(ApprovalOutcome.Approved),
                new AgentOptions())
            .RunTurnAsync(Session(directory.Path), "hi", null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentOutcome.Failed, report.Outcome);
        Assert.Contains("DEMO_API_KEY", report.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswerTheProviderFilteredFailsTheTurn()
    {
        using var directory = TempDirectory.Create();
        var model = new ScriptedModel(
            Descriptor,
            [new StreamCompleted(new ChatResponse(ChatMessage.Assistant("redacted"), FinishReason.ContentFilter, new TokenUsage(5, 1)))]);

        var report = await Agent(model, []).RunTurnAsync(
            Session(directory.Path), "hi", null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentOutcome.Failed, report.Outcome);
        Assert.Contains("filtered", report.Detail!, StringComparison.Ordinal);
        Assert.Equal("redacted", report.FinalText);
    }

    [Fact]
    public async Task AnUnknownToolIsReportedBackToTheModel()
    {
        using var directory = TempDirectory.Create();
        var model = new ScriptedModel(
            Descriptor,
            [ToolCallTurn("ghost")], [Completed("done")]);

        var report = await Agent(model, []).RunTurnAsync(
            Session(directory.Path), "hi", null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentOutcome.Completed, report.Outcome);
        var result = Assert.Single(report.ToolResults);
        Assert.True(result.IsError);
        Assert.Contains("no tool named 'ghost'", result.Content, StringComparison.Ordinal);
        Assert.Equal(2, report.Iterations);
    }

    [Fact]
    public async Task ARefusedToolKeepsTheTurnRunning()
    {
        using var directory = TempDirectory.Create();
        var model = new ScriptedModel(Descriptor, [ToolCallTurn("write_file")], [Completed("understood")]);
        var tool = new StubTool("write_file", ToolSafety.Destructive, _ => ToolResult.Success("call-1", "write_file", "wrote"));

        var report = await Agent(model, [tool]).RunTurnAsync(
            Session(directory.Path, ApprovalMode.ReadOnly), "hi", null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentOutcome.Completed, report.Outcome);
        var result = Assert.Single(report.ToolResults);
        Assert.True(result.IsRefused);
        Assert.Contains("ReadOnly", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheApproverIsAskedInAskModeAndItsRefusalIsHonoured()
    {
        using var directory = TempDirectory.Create();
        var model = new ScriptedModel(Descriptor, [ToolCallTurn("write_file")], [Completed("fine")]);
        var tool = new StubTool("write_file", ToolSafety.Destructive, _ => ToolResult.Success("call-1", "write_file", "wrote"));

        var report = await new CodingAgent(
                new StubResolver(_ => model),
                new ToolRegistry([tool]),
                new StubApprover(ApprovalOutcome.Denied),
                new AgentOptions())
            .RunTurnAsync(Session(directory.Path, ApprovalMode.Ask), "hi", null, TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(report.ToolResults).IsRefused);
    }

    [Fact]
    public async Task AnAnswerToAllowEveryToolStopsTheQuestionsForTheRestOfTheSession()
    {
        using var directory = TempDirectory.Create();
        var model = new ScriptedModel(
            Descriptor,
            [ToolCallTurn("write_file")],
            [Completed("first")],
            [ToolCallTurn("write_file")],
            [Completed("second")]);
        var tool = new StubTool("write_file", ToolSafety.Destructive, _ => ToolResult.Success("call-1", "write_file", "wrote"));
        var approver = new CountingApprover(ApprovalOutcome.ApprovedForSession);
        var agent = new CodingAgent(new StubResolver(_ => model), new ToolRegistry([tool]), approver, new AgentOptions());
        var session = Session(directory.Path, ApprovalMode.Ask);

        var first = await agent.RunTurnAsync(session, "write it", null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentOutcome.Completed, first.Outcome);
        Assert.False(Assert.Single(first.ToolResults).IsError);
        Assert.Equal(ApprovalMode.AllowAll, session.Approval);

        // The second destructive call runs on the answer already given: the mode is read from the
        // session, so "don't ask again" survives into the next turn of the same conversation.
        var second = await agent.RunTurnAsync(session, "write it again", null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentOutcome.Completed, second.Outcome);
        Assert.False(Assert.Single(second.ToolResults).IsError);
        Assert.Equal(1, approver.Calls);
    }

    [Fact]
    public async Task AnAnswerToAllowOneToolStopsTheQuestionsForThatToolOnly()
    {
        using var directory = TempDirectory.Create();
        var model = new ScriptedModel(
            Descriptor,
            [ToolCallTurn("run_shell_command")],
            [Completed("ran it")],
            [ToolCallTurn("write_file")],
            [Completed("declined")],
            [ToolCallTurn("run_shell_command")],
            [Completed("ran it again")]);
        var shell = new StubTool(
            "run_shell_command",
            ToolSafety.Destructive,
            call => ToolResult.Success(call.Id, call.Name, "ran"));
        var write = new StubTool(
            "write_file",
            ToolSafety.Destructive,
            call => ToolResult.Success(call.Id, call.Name, "wrote"));
        var approver = new ScriptedApprover(ApprovalOutcome.ApprovedForTool, ApprovalOutcome.Denied);
        var agent = new CodingAgent(
            new StubResolver(_ => model),
            new ToolRegistry([shell, write]),
            approver,
            new AgentOptions());
        var session = Session(directory.Path, ApprovalMode.Ask);

        var first = await agent.RunTurnAsync(session, "run the tests", null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentOutcome.Completed, first.Outcome);
        Assert.False(Assert.Single(first.ToolResults).IsError);
        Assert.Equal(new[] { "run_shell_command" }, session.ApprovedTools);

        // The mode is untouched: "this tool" must not become "every tool", or a developer who is tired of
        // approving a shell command would also have waved through every file edit.
        Assert.Equal(ApprovalMode.Ask, session.Approval);

        var second = await agent.RunTurnAsync(session, "write the file", null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentOutcome.Completed, second.Outcome);
        Assert.True(Assert.Single(second.ToolResults).IsRefused);
        Assert.Equal(new[] { "run_shell_command", "write_file" }, approver.Asked);

        // The third call of the approved tool never reaches the approver: the memo removed the question
        // rather than answering it a second time.
        var third = await agent.RunTurnAsync(session, "run the tests again", null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentOutcome.Completed, third.Outcome);
        Assert.False(Assert.Single(third.ToolResults).IsError);
        Assert.Equal(2, approver.Asked.Count);
    }

    [Fact]
    public async Task ATranscriptPastTheWindowIsCompactedAndTheTurnSaysSo()
    {
        using var directory = TempDirectory.Create();
        var model = new ScriptedModel(Descriptor, [Completed("still here")]);
        var observer = new RecordingObserver();
        var session = Session(directory.Path);

        // Twenty-five old messages plus the prompt this turn adds is well past a twenty-message window: that is
        // what an afternoon-long conversation looks like just before the provider would reject it for its size.
        for (var index = 0; index < 25; index++)
        {
            session.Add(ChatMessage.User($"old question {index}"));
        }

        var report = await AgentWithOptions(new AgentOptions { MaxContextMessages = 20 }, model, [])
            .RunTurnAsync(session, "and now", observer, TestContext.Current.CancellationToken);

        Assert.Equal(AgentOutcome.Completed, report.Outcome);

        var compacted = Assert.Single(observer.Events.OfType<ContextCompacted>());
        Assert.Equal(10, compacted.Kept);
        Assert.True(compacted.Dropped > 0, "the oldest messages have to go");

        // The instruction that started the work survives, and the transcript is back inside the window it was
        // measured against, so the next request is one the provider will accept.
        Assert.Equal(ChatRole.System, session.Messages[0].Role);
        Assert.True(session.Messages.Count <= 20, $"the transcript is still {session.Messages.Count} messages long");
    }

    [Fact]
    public async Task AToolThatThrowsIsReportedAsAFailure()
    {
        using var directory = TempDirectory.Create();
        var model = new ScriptedModel(Descriptor, [ToolCallTurn("explode")], [Completed("ok")]);
        var tool = new StubTool("explode", ToolSafety.ReadOnly, _ => throw new IOException("disk on fire"));

        var report = await Agent(model, [tool]).RunTurnAsync(
            Session(directory.Path), "hi", null, TestContext.Current.CancellationToken);

        var result = Assert.Single(report.ToolResults);
        Assert.True(result.IsError);
        Assert.Contains("explode failed: disk on fire", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABadlyFormedToolArgumentIsReportedAsAFailure()
    {
        using var directory = TempDirectory.Create();
        var model = new ScriptedModel(Descriptor, [ToolCallTurn("strict")], [Completed("ok")]);
        var tool = new StubTool("strict", ToolSafety.ReadOnly, _ => throw new ToolArgumentException("strict", "path", "is required"));

        var report = await Agent(model, [tool]).RunTurnAsync(
            Session(directory.Path), "hi", null, TestContext.Current.CancellationToken);

        var result = Assert.Single(report.ToolResults);
        Assert.Equal("strict: parameter 'path' is required", result.Content);
    }

    [Fact]
    public async Task TheIterationBudgetEndsATurnThatKeepsCallingTools()
    {
        using var directory = TempDirectory.Create();
        var model = new ScriptedModel(Descriptor, [ToolCallTurn("ping")]);
        var tool = new StubTool("ping", ToolSafety.ReadOnly, _ => ToolResult.Success("call-1", "ping", "pong"));

        var report = await AgentWithOptions(new AgentOptions { MaxIterations = 2 }, model, [tool])
            .RunTurnAsync(Session(directory.Path), "hi", null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentOutcome.BudgetExhausted, report.Outcome);
        Assert.Contains("iteration budget of 2", report.Detail!, StringComparison.Ordinal);
        Assert.Equal(2, report.Iterations);
    }

    [Fact]
    public async Task TheSessionTokenBudgetEndsATurnThatKeptSpending()
    {
        using var directory = TempDirectory.Create();
        var model = new ScriptedModel(
            Descriptor,
            [new StreamCompleted(new ChatResponse(
                ChatMessage.Assistant(string.Empty, [new ToolCall("ping", "ping", "{}")]),
                FinishReason.ToolCalls,
                new TokenUsage(900, 900)))]);
        var tool = new StubTool("ping", ToolSafety.ReadOnly, _ => ToolResult.Success("ping", "ping", "pong"));

        var report = await AgentWithOptions(new AgentOptions { MaxTotalTokens = 1_000 }, model, [tool])
            .RunTurnAsync(Session(directory.Path), "hi", null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentOutcome.BudgetExhausted, report.Outcome);
        Assert.Contains("session token budget of 1000", report.Detail!, StringComparison.Ordinal);
        Assert.Single(report.ToolResults);
    }

    [Fact]
    public async Task ATurnWithNoBudgetLeftDoesNotAskTheModel()
    {
        using var directory = TempDirectory.Create();

        // A model with nothing scripted: reaching it at all would fail the turn differently, so the assertion below
        // is also the proof that no request was sent.
        var model = new ScriptedModel(Descriptor, []);
        var session = Session(directory.Path);
        session.MaxTotalTokens = 1_000;
        session.RecordUsage("demo:demo-scripted", new TokenUsage(2_000, 0));

        var report = await AgentWithOptions(new AgentOptions { MaxTotalTokens = 1_000 }, model, [])
            .RunTurnAsync(session, "hi", null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentOutcome.BudgetExhausted, report.Outcome);
        Assert.Contains("session token budget of 1000", report.Detail!, StringComparison.Ordinal);
        Assert.Equal(0, report.Iterations);
    }

    [Fact]
    public async Task TheSessionCeilingOverridesTheOptionsBudget()
    {
        using var directory = TempDirectory.Create();
        var model = new ScriptedModel(
            Descriptor,
            [new StreamCompleted(new ChatResponse(
                ChatMessage.Assistant(string.Empty, [new ToolCall("ping", "ping", "{}")]),
                FinishReason.ToolCalls,
                new TokenUsage(900, 900)))]);
        var tool = new StubTool("ping", ToolSafety.ReadOnly, _ => ToolResult.Success("ping", "ping", "pong"));

        // A session that carries its own ceiling is the one the loop obeys: that is what makes raising the budget
        // for a running conversation possible without rebuilding the agent.
        var session = Session(directory.Path);
        session.MaxTotalTokens = 500;

        var report = await AgentWithOptions(new AgentOptions { MaxTotalTokens = 1_000_000 }, model, [tool])
            .RunTurnAsync(session, "hi", null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentOutcome.BudgetExhausted, report.Outcome);
        Assert.Contains("session token budget of 500", report.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APermanentProviderFailureFailsTheTurn()
    {
        using var directory = TempDirectory.Create();
        var model = new FailingModel(Descriptor, new ModelException("the provider said no"));

        var report = await Agent(model, []).RunTurnAsync(
            Session(directory.Path), "hi", null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentOutcome.Failed, report.Outcome);
        Assert.Equal("the provider said no", report.Detail);
    }

    [Fact]
    public async Task AStreamWithoutACompletionEventIsAFailure()
    {
        using var directory = TempDirectory.Create();
        var model = new ScriptedModel(Descriptor, [new TextDelta("half an answer")]);

        var report = await Agent(model, []).RunTurnAsync(
            Session(directory.Path), "hi", null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentOutcome.Failed, report.Outcome);
        Assert.Contains("without a completion event", report.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEventTheLoopDoesNotUnderstandIsAFailure()
    {
        using var directory = TempDirectory.Create();
        var model = new ScriptedModel(Descriptor, [new UnknownEvent()]);

        var report = await Agent(model, []).RunTurnAsync(
            Session(directory.Path), "hi", null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentOutcome.Failed, report.Outcome);
        Assert.Contains("does not understand", report.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationKeepsWhateverTheModelHadSaid()
    {
        using var directory = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var model = new CancellingModel(Descriptor, cancellation, "partial answer");

        var report = await Agent(model, []).RunTurnAsync(
            Session(directory.Path), "hi", null, cancellation.Token);

        Assert.Equal(AgentOutcome.Cancelled, report.Outcome);
        Assert.Equal("partial answer", report.FinalText);
        Assert.Contains("cancelled", report.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AToolsLiveOutputReachesTheObserverWithItsCallId()
    {
        using var directory = TempDirectory.Create();
        var model = new ScriptedModel(Descriptor, [ToolCallTurn("chatty")], [Completed("done")]);
        var observer = new RecordingObserver();

        var report = await Agent(model, [new ChattyTool()]).RunTurnAsync(
            Session(directory.Path), "hi", observer, TestContext.Current.CancellationToken);

        Assert.Equal(AgentOutcome.Completed, report.Outcome);
        var delta = Assert.Single(observer.Events.OfType<ToolOutputDelta>());
        Assert.Equal("call-1", delta.CallId);
        Assert.Equal("chatty", delta.ToolName);
        Assert.Equal("building\n", delta.Text);
    }

    [Fact]
    public async Task AToolThatSaysNothingEmitsNoOutputEvent()
    {
        using var directory = TempDirectory.Create();
        var model = new ScriptedModel(Descriptor, [ToolCallTurn("quiet")], [Completed("done")]);
        var observer = new RecordingObserver();
        var quiet = new StubTool("quiet", ToolSafety.ReadOnly, call => ToolResult.Success(call.Id, call.Name, "ok"));

        var report = await Agent(model, [quiet]).RunTurnAsync(
            Session(directory.Path), "hi", observer, TestContext.Current.CancellationToken);

        Assert.Equal(AgentOutcome.Completed, report.Outcome);
        Assert.Empty(observer.Events.OfType<ToolOutputDelta>());
    }

    [Fact]
    public async Task AFallbackIsRecordedAndTheAnswerIsBilledToTheFallback()
    {
        using var directory = TempDirectory.Create();
        var grok = new ModelDescriptor("grok", "grok-4.7", "Grok");
        var model = new FallbackChatModel(
            new FailingModel(Descriptor, new ModelRequestException("demo", "demo-scripted", 401, "invalid key", isTransient: false)),
            new ScriptedModel(grok, [Completed("from the fallback")]));
        var observer = new RecordingObserver();
        var session = Session(directory.Path);

        var report = await Agent(model, []).RunTurnAsync(
            session, "hi", observer, TestContext.Current.CancellationToken);

        Assert.Equal(AgentOutcome.Completed, report.Outcome);
        Assert.Equal("grok:grok-4.7", report.ModelId);
        Assert.Equal(1, report.Iterations);

        var fallback = Assert.Single(report.Fallbacks);
        Assert.Equal("demo:demo-scripted", fallback.PrimaryModelId);
        Assert.Equal(401, fallback.StatusCode);
        Assert.Equal("grok:grok-4.7", fallback.FallbackModelId);
        Assert.Contains(observer.Events.OfType<FallbackAttempted>(), attempted => attempted.Fallback == fallback);

        // The tokens belong to the model that produced them: `/cost` prices the session's lines by name.
        var usage = Assert.Single(session.UsageByModel);
        Assert.Equal("grok:grok-4.7", usage.ModelId);
        Assert.Equal(10, usage.InputTokens ?? 0);
        Assert.Equal(5, usage.OutputTokens ?? 0);
    }

    [Fact]
    public async Task ATurnThatFallsBackAndThenFailsNamesBothAttempts()
    {
        using var directory = TempDirectory.Create();
        var grok = new ModelDescriptor("grok", "grok-4.7", "Grok");
        var model = new FallbackChatModel(
            new FailingModel(Descriptor, new ModelRequestException("demo", "demo-scripted", 503, "busy", isTransient: true)),
            new FailingModel(grok, new ModelRequestException("grok", "grok-4.7", 500, "still down", isTransient: true)));

        var report = await Agent(model, []).RunTurnAsync(
            Session(directory.Path), "hi", null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentOutcome.Failed, report.Outcome);
        Assert.Equal("grok:grok-4.7", report.ModelId);
        Assert.Contains("still down", report.Detail!, StringComparison.Ordinal);
        var fallback = Assert.Single(report.Fallbacks);
        Assert.Equal("demo:demo-scripted", fallback.PrimaryModelId);
        Assert.Equal(503, fallback.StatusCode);
    }

    private static CodingAgent Agent(IStreamingChatModel model, IEnumerable<ITool> tools) =>
        AgentWithOptions(new AgentOptions(), model, tools);

    private static CodingAgent AgentWithOptions(
        AgentOptions options,
        IStreamingChatModel model,
        IEnumerable<ITool> tools) =>
        new(
            new StubResolver(_ => model),
            new ToolRegistry(tools),
            new StubApprover(ApprovalOutcome.Approved),
            options);

    private static AgentSession Session(string root, ApprovalMode approval = ApprovalMode.AllowAll) =>
        new(Workspace.Create(root), Descriptor, approval, "you are a test");

    private static StreamCompleted Completed(string answer) => new(new ChatResponse(
        ChatMessage.Assistant(answer),
        FinishReason.Stop,
        new TokenUsage(10, 5)));

    private static StreamCompleted ToolCallTurn(string name) => new(new ChatResponse(
        ChatMessage.Assistant(string.Empty, [new ToolCall("call-1", name, """{"path":"a.txt"}""")]),
        FinishReason.ToolCalls,
        new TokenUsage(10, 5)));

    /// <summary>Plays a fixed script, one script entry per request, repeating the last one.</summary>
    private sealed class ScriptedModel(ModelDescriptor model, params ChatStreamEvent[][] turns)
        : IStreamingChatModel
    {
        private int _calls;

        public ModelDescriptor Model => model;

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var turn = turns[Math.Min(_calls++, turns.Length - 1)];

            foreach (var streamEvent in turn)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return streamEvent;
            }
        }
    }

    /// <summary>Fails every request with the given provider error.</summary>
    private sealed class FailingModel(ModelDescriptor model, ModelException failure) : IStreamingChatModel
    {
        public ModelDescriptor Model => model;

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();

            if (failure is not null)
            {
                throw failure;
            }

            yield break;
        }
    }

    /// <summary>Says one thing, then has the developer press Ctrl+C.</summary>
    private sealed class CancellingModel(
        ModelDescriptor model,
        CancellationTokenSource cancellation,
        string text) : IStreamingChatModel
    {
        public ModelDescriptor Model => model;

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new TextDelta(text);

            await cancellation.CancelAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            yield return Completed("never reached");
        }
    }

    /// <summary>A stream event from a provider this build does not know.</summary>
    private sealed record UnknownEvent : ChatStreamEvent;

    private sealed class StubResolver(Func<ModelDescriptor, IStreamingChatModel> resolve) : IChatModelResolver
    {
        public int Calls { get; private set; }

        public IStreamingChatModel Resolve(ModelDescriptor model)
        {
            Calls++;
            return resolve(model);
        }
    }

    private sealed class StubApprover(ApprovalOutcome outcome) : IToolApprover
    {
        public ValueTask<ApprovalOutcome> DecideAsync(
            ToolApprovalRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(outcome);
    }

    /// <summary>
    /// Answers the same way every time and counts how often it was asked, so a test can prove that a
    /// session-wide approval actually removed the question rather than merely answering it.
    /// </summary>
    private sealed class CountingApprover(ApprovalOutcome outcome) : IToolApprover
    {
        public int Calls { get; private set; }

        public ValueTask<ApprovalOutcome> DecideAsync(
            ToolApprovalRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(outcome);
        }
    }

    /// <summary>
    /// Answers in the order the script wrote and records which tools it was asked about, so a test can prove
    /// which questions a standing approval removed and which it left alone.
    /// </summary>
    private sealed class ScriptedApprover(params ApprovalOutcome[] answers) : IToolApprover
    {
        private readonly List<string> _asked = [];

        public IReadOnlyList<string> Asked => _asked;

        public ValueTask<ApprovalOutcome> DecideAsync(
            ToolApprovalRequest request,
            CancellationToken cancellationToken)
        {
            _asked.Add(request.ToolName);

            // Running out of script is a mistake in the test, not a decision: fail loudly rather than answer
            // for a prompt the test never expected the loop to ask.
            if (_asked.Count > answers.Length)
            {
                throw new InvalidOperationException(
                    $"the approver was asked about {request.ToolName} with no answer left in the script");
            }

            return ValueTask.FromResult(answers[_asked.Count - 1]);
        }
    }

    /// <summary>A tool that narrates while it works, as a shell command does.</summary>
    private sealed class ChattyTool : ITool
    {
        public ToolDefinition Definition { get; } = new("chatty", "a test tool that talks", """{"type":"object"}""");

        public ToolSafety Safety => ToolSafety.ReadOnly;

        public async ValueTask<ToolResult> ExecuteAsync(
            ToolCall toolCall,
            ToolInvocationContext context,
            CancellationToken cancellationToken)
        {
            if (context.Output is { } output)
            {
                await output.WriteAsync("building\n", cancellationToken).ConfigureAwait(false);
            }

            return ToolResult.Success(toolCall.Id, toolCall.Name, "ok");
        }
    }

    private sealed class StubTool(string name, ToolSafety safety, Func<ToolCall, ToolResult> run) : ITool
    {
        public ToolDefinition Definition { get; } = new(name, "a test tool", """{"type":"object"}""");

        public ToolSafety Safety { get; } = safety;

        public ValueTask<ToolResult> ExecuteAsync(
            ToolCall toolCall,
            ToolInvocationContext context,
            CancellationToken cancellationToken) => ValueTask.FromResult(run(toolCall));
    }

    private sealed class RecordingObserver : IAgentObserver
    {
        private readonly List<AgentEvent> _events = [];

        public IReadOnlyList<AgentEvent> Events => _events;

        public ValueTask OnEventAsync(AgentEvent agentEvent, CancellationToken cancellationToken)
        {
            _events.Add(agentEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "sharpcoreagent-agent",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temp directory is not worth failing a passing test over.
            }
        }
    }
}




