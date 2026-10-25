using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Application;

/// <summary>
/// The turn loop: ask the model, run what it asks for, feed the answers back, stop when it answers.
/// </summary>
/// <remarks>
/// The loop owns three decisions and delegates everything else:
/// <list type="number">
/// <item><description>when to stop (a text answer, a budget, cancellation or a provider failure);</description></item>
/// <item><description>whether a tool may run (<see cref="IToolApprover"/> decides, not the loop);</description></item>
/// <item><description>what the transcript contains after each step.</description></item>
/// </list>
/// Every provider-specific detail lives behind <see cref="IChatModelResolver"/>, so adding a provider
/// never touches this file.
/// </remarks>
public sealed class CodingAgent
{
    private readonly IChatModelResolver _models;
    private readonly IToolRegistry _tools;
    private readonly IToolApprover _approver;
    private readonly AgentOptions _options;
    private readonly ILogger<CodingAgent> _logger;

    /// <summary>
    /// Creates an agent.
    /// </summary>
    /// <param name="models">Resolves a descriptor into a live, retry-wrapped streaming client.</param>
    /// <param name="tools">The tools the model may call.</param>
    /// <param name="approver">Decides whether a destructive tool may run.</param>
    /// <param name="options">The budgets for the turn.</param>
    /// <param name="logger">Optional diagnostics sink; the loop logs decisions, never transcript text.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> is out of range.</exception>
    public CodingAgent(
        IChatModelResolver models,
        IToolRegistry tools,
        IToolApprover approver,
        AgentOptions options,
        ILogger<CodingAgent>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentNullException.ThrowIfNull(options);

        _models = models;
        _tools = tools;
        _approver = approver;
        _options = options.Validate();
        _logger = logger ?? NullLogger<CodingAgent>.Instance;
    }

    /// <summary>Gets the validated options this agent runs with.</summary>
    public AgentOptions Options => _options;

    /// <summary>
    /// Runs one turn: append the prompt, then loop until the model answers or a budget runs out.
    /// </summary>
    /// <param name="session">The conversation to run in; it is mutated in place so partial work survives a failure.</param>
    /// <param name="prompt">The developer's instruction.</param>
    /// <param name="observer">Receives progress; null discards it.</param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    /// <returns>A report describing how the turn ended. Cancellation is reported, not thrown.</returns>
    public async Task<AgentReport> RunTurnAsync(
        AgentSession session,
        string prompt,
        IAgentObserver? observer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var sink = observer ?? NullAgentObserver.Instance;
        var model = session.ActiveModel;
        var state = new TurnState(model);

        // A model that cannot call tools would answer in prose and silently ignore every tool, so the
        // turn is refused before a request is spent and before the transcript is polluted.
        if (!model.SupportsTools)
        {
            Log.NoToolSupport(_logger, model.QualifiedName, null);
            return await FinishAsync(
                sink,
                state.Report(
                    AgentOutcome.Failed,
                    $"the model {model.QualifiedName} is declared as not supporting tool calls. Choose another model with /model or --model."))
                .ConfigureAwait(false);
        }

        IStreamingChatModel client;
        try
        {
            client = _models.Resolve(model);
        }
        catch (SharpCoreAgentException ex)
        {
            Log.ModelUnavailable(_logger, model.QualifiedName, ex.Message, ex);
            return await FinishAsync(sink, state.Report(AgentOutcome.Failed, ex.Message)).ConfigureAwait(false);
        }

        session.Add(ChatMessage.User(prompt));

        using var turnBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        turnBudget.CancelAfter(_options.TurnTimeout);

        await sink.OnEventAsync(new TurnStarted(model.QualifiedName), CancellationToken.None).ConfigureAwait(false);

        while (true)
        {
            if (state.Iterations >= _options.MaxIterations)
            {
                Log.IterationBudgetSpent(_logger, model.QualifiedName, _options.MaxIterations, null);
                return await FinishAsync(
                    sink,
                    state.Report(
                        AgentOutcome.BudgetExhausted,
                        $"the iteration budget of {_options.MaxIterations} model calls ran out before the model stopped calling tools."))
                    .ConfigureAwait(false);
            }

            state.Iterations++;

            var request = new ChatRequest(
                model,
                session.Messages,
                _tools.Definitions,
                _options.Temperature,
                _options.MaxOutputTokensPerTurn);

            var (stream, failure) = await TryStreamAsync(client, request, sink, turnBudget.Token).ConfigureAwait(false);
            if (stream is null)
            {
                Log.TurnFailed(_logger, model.QualifiedName, state.Iterations, failure ?? "the model call failed", null);
                return await FinishAsync(sink, state.Report(AgentOutcome.Failed, failure)).ConfigureAwait(false);
            }

            if (stream.Interrupted)
            {
                return await FinishAsync(sink, Interruption(session, stream, state, cancellationToken)).ConfigureAwait(false);
            }

            var response = stream.Response!;
            session.Add(response.Message);
            state.Usage = state.Usage is null ? response.Usage : state.Usage + response.Usage;
            session.RecordUsage(model.QualifiedName, response.Usage);

            var ending = await AdvanceAsync(session, response, state, sink, turnBudget.Token).ConfigureAwait(false);
            if (ending is not null)
            {
                return await FinishAsync(sink, ending).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Streams one request, turning a provider failure into the detail a report can carry.
    /// </summary>
    /// <param name="client">The model to stream from.</param>
    /// <param name="request">The request to send.</param>
    /// <param name="sink">Receives progress.</param>
    /// <param name="cancellationToken">Cancels the stream.</param>
    /// <returns>The outcome, or the failure message when the provider failed.</returns>
    private static async Task<(StreamOutcome? Stream, string? Failure)> TryStreamAsync(
        IStreamingChatModel client,
        ChatRequest request,
        IAgentObserver sink,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await StreamOnceAsync(client, request, sink, cancellationToken).ConfigureAwait(false), null);
        }
        catch (ModelException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Handles one model answer: the final answer, a filtered answer, or a round of tool calls.
    /// </summary>
    /// <param name="session">The conversation to append to.</param>
    /// <param name="response">What the model answered.</param>
    /// <param name="state">The turn's running totals.</param>
    /// <param name="sink">Receives tool events.</param>
    /// <param name="cancellationToken">Cancels the tool round.</param>
    /// <returns>The report that ends the turn, or <see langword="null"/> to continue it.</returns>
    private async Task<AgentReport?> AdvanceAsync(
        AgentSession session,
        ChatResponse response,
        TurnState state,
        IAgentObserver sink,
        CancellationToken cancellationToken)
    {
        if (response.FinishReason == FinishReason.ContentFilter)
        {
            return state.Report(
                AgentOutcome.Failed,
                "the provider filtered the model's output, so the answer cannot be trusted.",
                response.Message.Content);
        }

        if (!response.Message.HasToolCalls)
        {
            Log.TurnCompleted(_logger, state.Model.QualifiedName, state.Iterations, state.ResultCount, null);
            return state.Report(AgentOutcome.Completed, null, response.Message.Content);
        }

        foreach (var call in response.Message.ToolCalls)
        {
            var result = await InvokeAsync(session, call, sink, cancellationToken).ConfigureAwait(false);
            state.Add(result);
            session.Add(ChatMessage.FromToolResult(result));
        }

        // Checked after the tool round because a single round can add several large results.
        if (session.TotalUsage.Total is { } spent && spent > _options.MaxTotalTokens)
        {
            return state.Report(
                AgentOutcome.BudgetExhausted,
                $"the session token budget of {_options.MaxTotalTokens} is spent ({spent} used). Start a new session or raise the budget.");
        }

        return null;
    }

    /// <summary>
    /// Turns an interruption into the report that ends the turn.
    /// </summary>
    /// <param name="session">The conversation, which keeps whatever the model had said.</param>
    /// <param name="stream">The interrupted stream.</param>
    /// <param name="state">The turn's running totals.</param>
    /// <param name="cancellationToken">Tells a cancel from a timeout.</param>
    /// <returns>The report.</returns>
    /// <remarks>
    /// Cancellation and timeout are the same interruption to the protocol but not to the user: a timeout
    /// spent a budget, an interrupt did not. Partial text is kept so the developer can read what the model
    /// had already said.
    /// </remarks>
    private AgentReport Interruption(
        AgentSession session,
        StreamOutcome stream,
        TurnState state,
        CancellationToken cancellationToken)
    {
        if (stream.Text.Length > 0)
        {
            session.Add(ChatMessage.Assistant(stream.Text));
        }

        var cancelled = cancellationToken.IsCancellationRequested;

        return state.Report(
            cancelled ? AgentOutcome.Cancelled : AgentOutcome.BudgetExhausted,
            cancelled
                ? "the turn was cancelled before the model finished its answer."
                : $"the turn exceeded its wall-clock budget of {_options.TurnTimeout}.",
            stream.Text.Length > 0 ? stream.Text : null);
    }

    /// <summary>
    /// Announces the end of the turn to the observer and hands the report back.
    /// </summary>
    /// <param name="sink">Receives the terminal event.</param>
    /// <param name="report">The report to finish with.</param>
    /// <returns>The same report.</returns>
    private static async Task<AgentReport> FinishAsync(IAgentObserver sink, AgentReport report)
    {
        await sink.OnEventAsync(new TurnFinished(report), CancellationToken.None).ConfigureAwait(false);
        return report;
    }

    /// <summary>
    /// The running totals of one turn: what it spent, and what its tools reported.
    /// </summary>
    /// <param name="model">The model the turn runs on.</param>
    private sealed class TurnState(ModelDescriptor model)
    {
        private readonly List<ToolResult> _results = [];

        /// <summary>Gets the model the turn runs on.</summary>
        public ModelDescriptor Model { get; } = model;

        /// <summary>Gets or sets the tokens the turn has spent so far.</summary>
        public TokenUsage? Usage { get; set; }

        /// <summary>Gets or sets how many model round trips the turn has used.</summary>
        public int Iterations { get; set; }

        /// <summary>Gets how many tool results the turn has collected.</summary>
        public int ResultCount => _results.Count;

        /// <summary>Records one tool result.</summary>
        /// <param name="result">The result to keep.</param>
        public void Add(ToolResult result) => _results.Add(result);

        /// <summary>
        /// Builds the report for an outcome.
        /// </summary>
        /// <param name="outcome">How the turn ended.</param>
        /// <param name="detail">Why, when the outcome needs explaining.</param>
        /// <param name="finalText">The answer, when there is one.</param>
        /// <returns>The report.</returns>
        public AgentReport Report(AgentOutcome outcome, string? detail = null, string? finalText = null) => new()
        {
            Outcome = outcome,
            ModelId = Model.QualifiedName,
            FinalText = finalText,
            Usage = Usage ?? TokenUsage.Unknown,
            Iterations = Iterations,
            ToolResults = [.. _results],
            Detail = detail,
        };
    }

    private async Task<ToolResult> InvokeAsync(
        AgentSession session,
        ToolCall call,
        IAgentObserver sink,
        CancellationToken cancellationToken)
    {
        await sink.OnEventAsync(new ToolRequested(call.Id, call.Name, call.ArgumentsJson), CancellationToken.None)
            .ConfigureAwait(false);

        ToolResult result;
        var tool = _tools.Find(call.Name);
        if (tool is null)
        {
            // The model guessed a name. Telling it what exists is cheaper than failing the turn.
            result = ToolResult.Failure(
                call.Id,
                call.Name,
                $"no tool named '{call.Name}' exists. Available tools: {string.Join(", ", _tools.Definitions.Select(definition => definition.Name))}.");
        }
        else if (await GateAsync(session, tool, call, cancellationToken).ConfigureAwait(false) == ApprovalOutcome.Denied)
        {
            result = ToolResult.Refused(
                call.Id,
                call.Name,
                $"the developer refused this call (approval mode: {session.Approval}). Do not repeat it; explain what you need or use a read-only tool.");
        }
        else
        {
            result = await ExecuteAsync(tool, call, session, cancellationToken).ConfigureAwait(false);
        }

        result = ToolResultTrimmer.Trim(result, _options.MaxToolResultChars);
        await sink.OnEventAsync(
            new ToolFinished(call.Id, call.Name, result.IsError, Summarize(result)),
            CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Applies the session's approval mode. Read-only tools never prompt; destructive ones depend on it.
    /// </summary>
    /// <remarks>
    /// The mode is read from the session, not from the agent's options, because the session's setter
    /// promises that switching applies to the next tool call: the interactive loop relies on that when
    /// a developer types <c>/approval</c> mid-conversation.
    /// </remarks>
    private async ValueTask<ApprovalOutcome> GateAsync(
        AgentSession session,
        ITool tool,
        ToolCall call,
        CancellationToken cancellationToken)
    {
        if (tool.Safety == ToolSafety.ReadOnly)
        {
            return ApprovalOutcome.Approved;
        }

        return session.Approval switch
        {
            ApprovalMode.AllowAll => ApprovalOutcome.Approved,
            ApprovalMode.ReadOnly => ApprovalOutcome.Denied,
            _ => await _approver.DecideAsync(
                new ToolApprovalRequest(call.Name, tool.Safety, Describe(tool, call), call.ArgumentsJson),
                cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Runs a tool, converting the failures a tool is allowed to report into results the model can read.
    /// </summary>
    /// <remarks>
    /// The catch list is deliberately short: cancellation must escape, and an unexpected exception is a
    /// bug that should surface rather than be papered over as a tool error the model will retry forever.
    /// </remarks>
    private static async Task<ToolResult> ExecuteAsync(
        ITool tool,
        ToolCall call,
        AgentSession session,
        CancellationToken cancellationToken)
    {
        var context = new ToolInvocationContext(session.Workspace);
        try
        {
            return await tool.ExecuteAsync(call, context, cancellationToken).ConfigureAwait(false);
        }
        catch (ToolArgumentException ex)
        {
            return ToolResult.Failure(call.Id, call.Name, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SharpCoreAgentException)
        {
            return ToolResult.Failure(call.Id, call.Name, $"{call.Name} failed: {ex.Message}");
        }
    }

    private static string Describe(ITool tool, ToolCall call)
    {
        var flat = string.Join(' ', call.ArgumentsJson.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 160 ? $"{tool.Definition.Name} {flat}" : $"{tool.Definition.Name} {flat[..160]}…";
    }

    private static string Summarize(ToolResult result)
    {
        var text = result.Content.TrimStart();
        var newline = text.AsSpan().IndexOfAny('\r', '\n');
        var line = (newline < 0 ? text : text[..newline]).Trim();
        if (line.Length == 0)
        {
            line = result.Content.Trim();
        }

        if (line.Length == 0)
        {
            return "(no output)";
        }

        return line.Length <= 160 ? line : string.Concat(line.AsSpan(0, 160), "…");
    }

    /// <summary>
    /// Drains one streamed response, forwarding text increments and collecting the terminal completion.
    /// </summary>
    private static async Task<StreamOutcome> StreamOnceAsync(
        IStreamingChatModel client,
        ChatRequest request,
        IAgentObserver sink,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        ChatResponse? completed = null;

        try
        {
            await foreach (var streamEvent in client.StreamAsync(request, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                switch (streamEvent)
                {
                    case TextDelta delta:
                        text.Append(delta.Text);
                        await sink.OnEventAsync(new AgentTextDelta(delta.Text), CancellationToken.None).ConfigureAwait(false);
                        break;
                    case ThinkingDelta thinking:
                        await sink.OnEventAsync(new AgentThinkingDelta(thinking.Text), CancellationToken.None).ConfigureAwait(false);
                        break;
                    case StreamCompleted done:
                        completed = done.Response;
                        break;
                    default:
                        throw new ModelException(
                            $"{request.Model.QualifiedName} emitted a stream event SharpCoreAgent does not understand ({streamEvent.GetType().Name}).");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Prefer the text already shown over an exception: the developer can still read it.
            return new StreamOutcome(null, text.ToString(), true);
        }

        if (completed is null)
        {
            throw new ModelException(
                $"{request.Model.QualifiedName} ended its stream without a completion event after {text.Length} characters. The partial answer was discarded.");
        }

        return new StreamOutcome(completed, text.ToString(), false);
    }

    /// <summary>
    /// One drained stream: the completion when it arrived, the text that was shown, and whether the
    /// stream was cut short.
    /// </summary>
    private sealed record StreamOutcome(ChatResponse? Response, string Text, bool Interrupted);

    /// <summary>
    /// Cached logging delegates.
    /// </summary>
    /// <remarks>
    /// <see cref="LoggerMessage"/> builds the message template once, so a disabled log level
    /// costs a null check and CA1848 is satisfied without a source generator.
    /// </remarks>
    private static class Log
    {
        internal static readonly Action<ILogger, string, Exception?> NoToolSupport =
            LoggerMessage.Define<string>(
                LogLevel.Warning,
                new EventId(1001, nameof(NoToolSupport)),
                "Refusing a turn on {Model}: the model declares no tool support.");

        internal static readonly Action<ILogger, string, string, Exception?> ModelUnavailable =
            LoggerMessage.Define<string, string>(
                LogLevel.Error,
                new EventId(1002, nameof(ModelUnavailable)),
                "Cannot resolve {Model}: {Reason}");

        internal static readonly Action<ILogger, string, int, Exception?> IterationBudgetSpent =
            LoggerMessage.Define<string, int>(
                LogLevel.Warning,
                new EventId(1003, nameof(IterationBudgetSpent)),
                "Turn on {Model} spent the iteration budget of {Budget} model calls.");

        internal static readonly Action<ILogger, string, int, string, Exception?> TurnFailed =
            LoggerMessage.Define<string, int, string>(
                LogLevel.Error,
                new EventId(1004, nameof(TurnFailed)),
                "Turn on {Model} failed during iteration {Iteration}: {Reason}");

        internal static readonly Action<ILogger, string, int, int, Exception?> TurnCompleted =
            LoggerMessage.Define<string, int, int>(
                LogLevel.Information,
                new EventId(1005, nameof(TurnCompleted)),
                "Turn on {Model} completed in {Iterations} iteration(s) with {ToolCalls} tool call(s).");
    }
}
