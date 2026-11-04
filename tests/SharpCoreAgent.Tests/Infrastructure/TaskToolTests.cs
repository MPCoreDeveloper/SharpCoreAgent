using System.Runtime.CompilerServices;
using SharpCoreAgent.Application;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Checkpoints;
using SharpCoreAgent.Infrastructure.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The delegation tool: one child turn, one bounded answer, and a transcript the parent never inherits.
/// </summary>
/// <remarks>
/// Every test drives the shipping <see cref="TaskTool"/> over the shipping <see cref="CodingAgent"/>, so the only fake
/// is the provider: what a child may do and what the parent reads are the product's own decisions rather than a
/// restatement of them. The four small fakes at the bottom of this file are copies of private nested types in
/// <c>CodingAgentTests</c>, which is cheaper than a test-utility project for four shapes.
/// </remarks>
public sealed class TaskToolTests
{
    private static ModelDescriptor Descriptor => new("demo", "demo-scripted", "Demo");

    [Fact]
    public async Task TheChildsAnswerAndItsCostComeBackInOneResult()
    {
        using var directory = TempDirectory.Create();
        var tool = Tool(new ScriptedModel(Descriptor, [Completed("three callers, all in Billing")]), Probe());

        var result = await tool.ExecuteAsync(
            Call("""{"prompt":"find every caller of PriceCalculator","description":"callers"}"""),
            Context(directory),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.False(result.IsTruncated);
        Assert.Equal(
            "task · callers · 1 model calls · 15 tokens\nthree callers, all in Billing",
            result.Content);
    }

    [Fact]
    public async Task TheParentsTranscriptGainsNoMessageFromTheChild()
    {
        using var directory = TempDirectory.Create();
        var model = new ScriptedModel(
            Descriptor,
            [ToolCallTurn(TaskTool.Name, """{"prompt":"find every caller","description":"callers"}""")],
            [Completed("three callers, all in Billing")],
            [Completed("the parent is done")]);
        var agent = Agent(model, Tool(model, Probe()));
        var session = Session(directory);

        var report = await agent.RunTurnAsync(
            session,
            "delegate the search",
            observer: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(AgentOutcome.Completed, report.Outcome);
        Assert.Equal("the parent is done", report.FinalText);

        // The answer arrives, and it arrives as a tool result — never as an assistant message, which is what a
        // shared transcript would have produced.
        var result = Assert.Single(report.ToolResults);
        Assert.Equal(TaskTool.Name, result.ToolName);
        Assert.Contains("1 model calls · 15 tokens", result.Content, StringComparison.Ordinal);
        Assert.Contains("three callers, all in Billing", result.Content, StringComparison.Ordinal);

        Assert.DoesNotContain(
            session.Messages,
            message => message.Role == ChatRole.Assistant
                && message.Content.Contains("three callers", StringComparison.Ordinal));
        Assert.Contains(
            session.Messages,
            message => message.Role == ChatRole.Tool
                && message.Content.Contains("three callers", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheChildSeesThisRegistryWithoutDelegationItself()
    {
        using var directory = TempDirectory.Create();
        var model = new ScriptedModel(Descriptor, [Completed("done")]);
        var delegation = new StubTool(
            TaskTool.Name,
            ToolSafety.Destructive,
            call => ToolResult.Success(call.Id, call.Name, "a stand-in, so the filter has something to remove"));
        var tool = Tool(model, Probe(), delegation);

        await tool.ExecuteAsync(
            Call("""{"prompt":"look around"}"""),
            Context(directory),
            TestContext.Current.CancellationToken);

        string[] advertised = [.. Assert.Single(model.Requests).Tools.Select(definition => definition.Name)];
        Assert.Equal(["probe"], advertised);
    }

    [Fact]
    public async Task TheChildBudgetDefaultsToTwelveModelCalls()
    {
        using var directory = TempDirectory.Create();
        var model = new ScriptedModel(Descriptor, [ToolCallTurn("probe", "{}")]);
        var tool = Tool(model, Probe());

        var result = await tool.ExecuteAsync(
            Call("""{"prompt":"keep looking until you find it"}"""),
            Context(directory),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains(
            "ran out of its 12 model calls without answering (BudgetExhausted).",
            result.Content,
            StringComparison.Ordinal);
        Assert.Equal(TaskTool.DefaultSubAgentIterations, model.Requests.Count);
    }

    [Fact]
    public async Task TheChildBudgetIsTheCeilingTheCallerAskedFor()
    {
        using var directory = TempDirectory.Create();
        var model = new ScriptedModel(Descriptor, [ToolCallTurn("probe", "{}")]);
        var tool = Tool(model, Probe());

        var result = await tool.ExecuteAsync(
            Call("""{"prompt":"keep looking","max_iterations":3}"""),
            Context(directory),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("· 3 model calls ·", result.Content, StringComparison.Ordinal);
        Assert.Contains("ran out of its 3 model calls", result.Content, StringComparison.Ordinal);
        Assert.Equal(3, model.Requests.Count);
    }

    [Fact]
    public async Task AnIterationRequestAboveTheCeilingIsRefusedRatherThanHonoured()
    {
        using var directory = TempDirectory.Create();
        var tool = Tool(new ScriptedModel(Descriptor, [Completed("done")]), Probe());

        var error = await Assert.ThrowsAsync<ToolArgumentException>(async () =>
        {
            await tool.ExecuteAsync(
                Call("""{"prompt":"look","max_iterations":25}"""),
                Context(directory),
                TestContext.Current.CancellationToken);
        });

        Assert.Equal("max_iterations", error.Parameter);
        Assert.Contains("between 1 and 24", error.Message, StringComparison.Ordinal);
        Assert.True(TaskTool.MaxSubAgentIterations < 25);
    }

    [Fact]
    public async Task ACompletedChildThatSaidNothingIsAFailure()
    {
        using var directory = TempDirectory.Create();
        var tool = Tool(new ScriptedModel(Descriptor, [Completed(string.Empty)]), Probe());

        var result = await tool.ExecuteAsync(
            Call("""{"prompt":"look and report"}"""),
            Context(directory),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("the sub-agent completed without saying anything.", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChildThatCouldNotRunIsAFailureNamingTheOutcome()
    {
        using var directory = TempDirectory.Create();
        var tool = Tool(new ScriptedModel(Descriptor, [Completed("never read")]), Probe());
        var context = new ToolInvocationContext(Workspace.Create(directory.Path))
        {
            Model = Descriptor with { SupportsTools = false },
            Approval = ApprovalMode.AllowAll,
        };

        var result = await tool.ExecuteAsync(
            Call("""{"prompt":"look and report"}"""),
            context,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("(Failed).", result.Content, StringComparison.Ordinal);
        Assert.Contains("not supporting tool calls", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABlankPromptIsARejectedArgument()
    {
        using var directory = TempDirectory.Create();
        var tool = Tool(new ScriptedModel(Descriptor, [Completed("never read")]), Probe());

        var error = await Assert.ThrowsAsync<ToolArgumentException>(async () =>
        {
            await tool.ExecuteAsync(
                Call("""{"prompt":"   "}"""),
                Context(directory),
                TestContext.Current.CancellationToken);
        });

        Assert.Equal("prompt", error.Parameter);
        Assert.Contains("must not be empty", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswerLongerThanTheBudgetIsCutAndReportedAsTruncated()
    {
        using var directory = TempDirectory.Create();
        var answer = new string('x', 5_000);
        var tool = Tool(
            new ScriptedModel(Descriptor, [Completed(answer)]),
            new AgentOptions { MaxToolResultChars = 1_000 },
            Probe());

        var result = await tool.ExecuteAsync(
            Call("""{"prompt":"write it all out"}"""),
            Context(directory),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.True(result.IsTruncated);
        Assert.True(result.Content.Length <= 1_000);
        Assert.EndsWith(TextLimits.TruncatedMarker, result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellingTheParentEscapesAsACancellationRatherThanAnAnswer()
    {
        using var directory = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var tool = Tool(new ScriptedModel(Descriptor, [Completed("never read")]), Probe());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await tool.ExecuteAsync(
                Call("""{"prompt":"look and report"}"""),
                Context(directory),
                cancellation.Token);
        });
    }

    [Fact]
    public async Task TheApprovalPreviewShowsThePromptAndTheCeiling()
    {
        using var directory = TempDirectory.Create();

        var preview = await Tool(new ScriptedModel(Descriptor, [Completed("never read")]), Probe())
            .DescribeChangeAsync(
                Call("""{"prompt":"find every caller\nand list them","max_iterations":4}"""),
                Context(directory),
                TestContext.Current.CancellationToken);

        Assert.NotNull(preview);
        Assert.Equal(
            "sub-agent · find every caller · at most 4 model calls\nfind every caller\nand list them",
            preview);
    }

    [Fact]
    public async Task AChildOfAnAskingParentCannotWrite()
    {
        var (result, model, written) = await RunChildWriteAsync(ApprovalMode.Ask);

        Assert.False(result.IsError);
        Assert.Null(written);

        // Not "nothing happened": the child asked, was refused, and was told so in its own transcript. That message is
        // the only place the refusal is visible, because the parent sees the answer and nothing else.
        var refusal = Assert.Single(
            model.Requests[^1].Messages,
            message => message.Role == ChatRole.Tool);
        Assert.Contains("approval mode: ReadOnly", refusal.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChildOfAnAllowAllParentWrites()
    {
        var (result, _, written) = await RunChildWriteAsync(ApprovalMode.AllowAll);

        Assert.False(result.IsError);
        Assert.Equal("written by a child", written);
    }

    [Fact]
    public async Task AChildsWritesReadsAndCommandsAreForwardedAsEffects()
    {
        using var directory = TempDirectory.Create();
        var model = new ScriptedModel(
            Descriptor,
            [ToolCallTurn(
                (WriteFileTool.Name, """{"path":"notes.txt","content":"first"}"""),
                (ReadFileTool.Name, """{"path":"notes.txt"}"""),
                (ReplaceInFileTool.Name, """{"path":"notes.txt","find":"first","replace":"second"}"""),
                ("run_shell", """{"command":"dotnet build"}"""),
                (DeleteFileTool.Name, """{"path":"notes.txt"}"""))],
            [Completed("wrote it, read it, changed it, built it and removed it")]);
        var tool = Tool(
            model,
            new WriteFileTool(),
            new ReadFileTool(),
            new ReplaceInFileTool(),
            Shell(),
            new DeleteFileTool());

        var result = await tool.ExecuteAsync(
            Call("""{"prompt":"do five things","description":"five things"}"""),
            Context(directory, ApprovalMode.AllowAll),
            TestContext.Current.CancellationToken);

        // Four effects, in the order the child produced them, and the read is not one of them: the parent did not read
        // that file, and forwarding the read would inflate its FilesTouched into a claim it never made.
        Assert.False(result.IsError);
        Assert.Equal(
            [
                new ToolEffect(ToolEffectKind.Created, "notes.txt"),
                new ToolEffect(ToolEffectKind.Modified, "notes.txt"),
                new ToolEffect(ToolEffectKind.CommandRun, "dotnet build"),
                new ToolEffect(ToolEffectKind.Deleted, "notes.txt"),
            ],
            result.Effects);
        Assert.DoesNotContain(result.Effects, effect => effect.Kind == ToolEffectKind.Read);
        Assert.False(File.Exists(Path.Combine(directory.Path, "notes.txt")));
    }

    [Fact]
    public async Task AChildEditBelongsToTheParentsUndoStep()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "notes.txt");
        await File.WriteAllTextAsync(path, "original", TestContext.Current.CancellationToken);
        var journal = new TurnCheckpointStore();
        var childModel = new ScriptedModel(
            Descriptor,
            [ToolCallTurn(WriteFileTool.Name, """{"path":"notes.txt","content":"child"}""")],
            [Completed("child done")]);
        var parentModel = new ScriptedModel(
            Descriptor,
            [ToolCallTurn(TaskTool.Name, """{"prompt":"write notes.txt","description":"write"}""")],
            [Completed("parent done")]);
        var task = Tool(childModel, new AgentOptions(), journal, new WriteFileTool());
        var agent = Agent(parentModel, journal, task);

        var report = await agent.RunTurnAsync(
            Session(directory),
            "delegate the write",
            observer: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(AgentOutcome.Completed, report.Outcome);
        Assert.Equal("child", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));

        var undo = journal.UndoLast();
        Assert.True(undo.Succeeded);
        Assert.Equal(["notes.txt"], undo.Restored);
        Assert.Equal("original", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.False(journal.UndoLast().HadCheckpoint);
    }

    private static TaskTool Tool(IStreamingChatModel model, params ITool[] tools) =>
        Tool(model, new AgentOptions(), null, tools);

    private static TaskTool Tool(IStreamingChatModel model, AgentOptions options, params ITool[] tools) =>
        Tool(model, options, null, tools);

    private static TaskTool Tool(
        IStreamingChatModel model,
        AgentOptions options,
        ITurnCheckpointJournal? checkpoints,
        params ITool[] tools) =>
        new(new StubResolver(_ => model), () => new ToolRegistry(tools), options, checkpoints);

    private static CodingAgent Agent(IStreamingChatModel model, params ITool[] tools) =>
        Agent(model, null, tools);

    private static CodingAgent Agent(
        IStreamingChatModel model,
        ITurnCheckpointJournal? checkpoints,
        params ITool[] tools) =>
        new(
            new StubResolver(_ => model),
            new ToolRegistry(tools),
            new StubApprover(ApprovalOutcome.Approved),
            new AgentOptions(),
            checkpoints: checkpoints);

    private static AgentSession Session(TempDirectory directory) =>
        new(Workspace.Create(directory.Path), Descriptor, ApprovalMode.AllowAll, "you are a test");

    private static ToolInvocationContext Context(
        TempDirectory directory,
        ApprovalMode approval = ApprovalMode.Ask) =>
        new(Workspace.Create(directory.Path))
        {
            Model = Descriptor,
            Approval = approval,
        };

    private static ToolCall Call(string argumentsJson) => new("call_1", TaskTool.Name, argumentsJson);

    /// <summary>
    /// Runs one child that tries to write a file, under a parent in <paramref name="approval"/>, and reports the result,
    /// the scripted model and what the disk holds afterwards. Both halves of the approval matrix call this, so the mode
    /// is the only difference between them.
    /// </summary>
    /// <param name="approval">The parent's approval mode, which decides the child's.</param>
    /// <returns>The task result, the scripted model, and the file's content — null when the child never wrote it.</returns>
    private static async Task<(ToolResult Result, ScriptedModel Model, string? Written)> RunChildWriteAsync(
        ApprovalMode approval)
    {
        using var directory = TempDirectory.Create();
        var model = new ScriptedModel(
            Descriptor,
            [ToolCallTurn(WriteFileTool.Name, """{"path":"notes.txt","content":"written by a child"}""")],
            [Completed("finished")]);
        var path = Path.Combine(directory.Path, "notes.txt");

        var result = await Tool(model, new WriteFileTool()).ExecuteAsync(
            Call("""{"prompt":"write notes.txt","description":"write"}"""),
            Context(directory, approval),
            TestContext.Current.CancellationToken);

        return (result, model, File.Exists(path) ? await File.ReadAllTextAsync(path) : null);
    }

    /// <summary>Gets a read-only tool the child can call, so "a child that keeps working" is expressible.</summary>
    private static ITool Probe() =>
        new StubTool("probe", ToolSafety.ReadOnly, call => ToolResult.Success(call.Id, call.Name, "probe found nothing"));

    /// <summary>
    /// Gets a stand-in for the shell tool that reports the effect the real one reports. The real tool starts a process,
    /// and this file is about which of a child's effects a parent inherits, not about the shell.
    /// </summary>
    private static ITool Shell() =>
        new StubTool(
            "run_shell",
            ToolSafety.Destructive,
            call => ToolResult.Success(
                call.Id,
                call.Name,
                "exit code: 0",
                new ToolEffect(ToolEffectKind.CommandRun, "dotnet build")));

    private static StreamCompleted Completed(string answer) => new(new ChatResponse(
        ChatMessage.Assistant(answer),
        FinishReason.Stop,
        new TokenUsage(10, 5)));

    private static StreamCompleted ToolCallTurn(string name, string argumentsJson) => new(new ChatResponse(
        ChatMessage.Assistant(string.Empty, [new ToolCall("call-1", name, argumentsJson)]),
        FinishReason.ToolCalls,
        new TokenUsage(10, 5)));

    /// <summary>Plays one round that asks for several tools at once, so their effects arrive in a known order.</summary>
    private static StreamCompleted ToolCallTurn(params (string Name, string ArgumentsJson)[] calls) => new(new ChatResponse(
        ChatMessage.Assistant(
            string.Empty,
            [.. calls.Select((call, index) => new ToolCall($"call-{index + 1}", call.Name, call.ArgumentsJson))]),
        FinishReason.ToolCalls,
        new TokenUsage(10, 5)));

    /// <summary>Plays a fixed script, one entry per request, repeating the last one, and keeps every request.</summary>
    private sealed class ScriptedModel(ModelDescriptor model, params ChatStreamEvent[][] turns) : IStreamingChatModel
    {
        private readonly List<ChatRequest> _requests = [];
        private int _calls;

        /// <summary>Gets every request the child or the parent made, in order.</summary>
        public IReadOnlyList<ChatRequest> Requests => _requests;

        public ModelDescriptor Model => model;

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _requests.Add(request);
            var turn = turns[Math.Min(_calls++, turns.Length - 1)];

            foreach (var streamEvent in turn)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return streamEvent;
            }
        }
    }

    private sealed class StubResolver(Func<ModelDescriptor, IStreamingChatModel> resolve) : IChatModelResolver
    {
        public IStreamingChatModel Resolve(ModelDescriptor model) => resolve(model);
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

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "sharpcoreagent-task",
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
