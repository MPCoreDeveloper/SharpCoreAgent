using System.Runtime.CompilerServices;
using SharpCoreAgent.Application;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Tools;
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




