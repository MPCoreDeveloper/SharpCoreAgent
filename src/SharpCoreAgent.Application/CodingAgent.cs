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
        var results = new List<ToolResult>();
        var iterations = 0;
        TokenUsage? turnUsage = null;

        AgentReport Build(AgentOutcome outcome, string? detail = null, string? finalText = null) => new()
        {
            Outcome = outcome,
            ModelId = model.QualifiedName,
            FinalText = finalText,
            Usage = turnUsage ?? TokenUsage.Unknown,
            Iterations = iterations,
            ToolResults = [.. results],
            Detail = detail,
        };

        async Task<AgentReport> FinishAsync(AgentReport report)
        {
            await sink.OnEventAsync(new TurnFinished(report), CancellationToken.None).ConfigureAwait(false);
            return report;
        }

        // A model that cannot call tools would answer in prose and silently ignore every tool, so the
        // turn is refused before a request is spent and before the transcript is polluted.
        if (!model.SupportsTools)
        {
            Log.NoToolSupport(_logger, model.QualifiedName, null);
            return await FinishAsync(Build(
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
            return await FinishAsync(Build(AgentOutcome.Failed, ex.Message)).ConfigureAwait(false);
        }

        session.Add(ChatMessage.User(prompt));

        using var turnBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        turnBudget.CancelAfter(_options.TurnTimeout);

        await sink.OnEventAsync(new TurnStarted(model.QualifiedName), CancellationToken.None).ConfigureAwait(false);

        while (true)
        {
            if (iterations >= _options.MaxIterations)
            {
                Log.IterationBudgetSpent(_logger, model.QualifiedName, _options.MaxIterations, null);
                return await FinishAsync(Build(
                    AgentOutcome.BudgetExhausted,
                    $"the iteration budget of {_options.MaxIterations} model calls ran out before the model stopped calling tools."))
                    .ConfigureAwait(false);
            }

            iterations++;
            var request = new ChatRequest(
                model,
                session.Messages,
                _tools.Definitions,
                _options.Temperature,
                _options.MaxOutputTokensPerTurn);

            StreamOutcome stream;
            try
            {
                stream = await StreamOnceAsync(client, request, sink, turnBudget.Token).ConfigureAwait(false);
            }
            catch (ModelException ex)
            {
                Log.TurnFailed(_logger, model.QualifiedName, iterations, ex.Message, ex);
                return await FinishAsync(Build(AgentOutcome.Failed, ex.Message)).ConfigureAwait(false);
            }

            // Cancellation and timeout are the same interruption to the protocol but not to the user:
            // a timeout spent a budget, an interrupt did not. Partial text is kept so the developer can
            // read what the model had already said.
            if (stream.Interrupted)
            {
                if (stream.Text.Length > 0)
                {
                    session.Add(ChatMessage.Assistant(stream.Text));
                }

                var cancelled = cancellationToken.IsCancellationRequested;
                var detail = cancelled
                    ? "the turn was cancelled before the model finished its answer."
                    : $"the turn exceeded its wall-clock budget of {_options.TurnTimeout}.";
                return await FinishAsync(Build(
                    cancelled ? AgentOutcome.Cancelled : AgentOutcome.BudgetExhausted,
                    detail,
                    stream.Text.Length > 0 ? stream.Text : null))
                    .ConfigureAwait(false);
            }

            var response = stream.Response!;
            session.Add(response.Message);
            turnUsage = turnUsage is null ? response.Usage : turnUsage + response.Usage;
            session.RecordUsage(model.QualifiedName, response.Usage);

            if (response.FinishReason == FinishReason.ContentFilter)
            {
                return await FinishAsync(Build(
                    AgentOutcome.Failed,
                    "the provider filtered the model's output, so the answer cannot be trusted.",
                    response.Message.Content))
                    .ConfigureAwait(false);
            }

            if (!response.Message.HasToolCalls)
            {
                Log.TurnCompleted(_logger, model.QualifiedName, iterations, results.Count, null);
                return await FinishAsync(Build(AgentOutcome.Completed, null, response.Message.Content)).ConfigureAwait(false);
            }

            foreach (var call in response.Message.ToolCalls)
            {
                var result = await InvokeAsync(session, call, sink, turnBudget.Token).ConfigureAwait(false);
                results.Add(result);
                session.Add(ChatMessage.FromToolResult(result));
            }

            // Checked after the tool round because a single round can add several large results.
            var spent = session.TotalUsage.Total;
            if (spent is not null && spent > _options.MaxTotalTokens)
            {
                return await FinishAsync(Build(
                    AgentOutcome.BudgetExhausted,
                    $"the session token budget of {_options.MaxTotalTokens} is spent ({spent} used). Start a new session or raise the budget."))
                    .ConfigureAwait(false);
            }
        }
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
