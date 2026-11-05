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
    private readonly IReadOnlyList<VerificationBinding> _verifications;
    private readonly IToolApprover _approver;
    private readonly AgentOptions _options;
    private readonly ILogger<CodingAgent> _logger;
    private readonly ITurnCheckpointJournal? _checkpoints;

    /// <summary>
    /// Creates an agent.
    /// </summary>
    /// <param name="models">Resolves a descriptor into a live, retry-wrapped streaming client.</param>
    /// <param name="tools">The tools the model may call.</param>
    /// <param name="approver">Decides whether a destructive tool may run.</param>
    /// <param name="options">The budgets for the turn.</param>
    /// <param name="logger">Optional diagnostics sink; the loop logs decisions, never transcript text.</param>
    /// <param name="checkpoints">Optional turn journal; when present, file tools can capture what they are about to change.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> is out of range.</exception>
    public CodingAgent(
        IChatModelResolver models,
        IToolRegistry tools,
        IToolApprover approver,
        AgentOptions options,
        ILogger<CodingAgent>? logger = null,
        ITurnCheckpointJournal? checkpoints = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentNullException.ThrowIfNull(options);

        _models = models;
        _tools = tools;
        _verifications = VerifyGate.Bind(tools);
        _approver = approver;
        _options = options.Validate();
        _logger = logger ?? NullLogger<CodingAgent>.Instance;
        _checkpoints = checkpoints;
    }

    /// <summary>Gets the validated options this agent runs with.</summary>
    public AgentOptions Options => _options;

    /// <summary>
    /// Gets how often the loop asks the model to verify a .NET edit before it accepts the answer.
    /// </summary>
    /// <remarks>
    /// Two, and then the truth: a model that obeys needs one, a model that ignores the first usually obeys the
    /// second, and a tree that genuinely does not build would otherwise spend the whole iteration budget being
    /// told to build again. After the last nudge the turn completes with
    /// <see cref="AgentReport.IsUnverified"/> set, because a turn that can never end is worse than one that ends
    /// honestly.
    /// </remarks>
    public const int MaxVerificationNudges = 2;

    /// <summary>
    /// Runs one turn: append the prompt, then loop until the model answers or a budget runs out.
    /// </summary>
    /// <param name="session">The conversation to run in; it is mutated in place so partial work survives a failure.</param>
    /// <param name="prompt">The developer's instruction.</param>
    /// <param name="observer">Receives progress; null discards it.</param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    /// <returns>A report describing how the turn ended. Cancellation is reported, not thrown.</returns>
    public Task<AgentReport> RunTurnAsync(
        AgentSession session,
        string prompt,
        IAgentObserver? observer = null,
        CancellationToken cancellationToken = default) =>
        RunTurnAsync(session, prompt, [], observer, cancellationToken);

    /// <summary>
    /// Runs one turn with pictures attached to the prompt.
    /// </summary>
    /// <param name="session">The conversation to run in; it is mutated in place so partial work survives a failure.</param>
    /// <param name="prompt">The developer's instruction.</param>
    /// <param name="images">Pictures attached to the prompt; empty for a text-only turn.</param>
    /// <param name="observer">Receives progress; null discards it.</param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    /// <returns>A report describing how the turn ended. Cancellation is reported, not thrown.</returns>
    public async Task<AgentReport> RunTurnAsync(
        AgentSession session,
        string prompt,
        IReadOnlyList<ImageContent> images,
        IAgentObserver? observer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var sink = observer ?? NullAgentObserver.Instance;
        var model = session.ActiveModel;

        // Every turn gets a checkpoint, even a read-only one: a checkpoint with no captured file is discarded at the
        // end, while a review that only reads leaves the previous edit's undo step in place. A host without a journal
        // gets null, and the file tools behave exactly as they did before checkpoints existed.
        using var checkpoint = _checkpoints?.BeginTurn();
        var state = new TurnState(model, AlreadyShown(session), checkpoint, _verifications);
        state.SeedImages(images);

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

        if (images is { Count: > 0 } && !model.SupportsImages)
        {
            return await FinishAsync(
                sink,
                state.Report(
                    AgentOutcome.Failed,
                    $"{model.QualifiedName} is declared text-only in this build and cannot read the attached image(s). Choose a model that supports images."))
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

        session.Add(ChatMessage.User(prompt, images));

        using var turnBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        turnBudget.CancelAfter(_options.TurnTimeout);

        await sink.OnEventAsync(new TurnStarted(model.QualifiedName), CancellationToken.None).ConfigureAwait(false);

        while (true)
        {
            // Before a request, not after the answer: the next call is what costs money, and a turn that has no
            // room left would only spend it to learn that. Refusing here is also what makes `/budget` a real
            // remedy — the transcript keeps its reasoning, and the same request continues once the ceiling rises.
            if (Spent(session) is { } exhausted)
            {
                return await FinishAsync(sink, state.Report(AgentOutcome.BudgetExhausted, exhausted)).ConfigureAwait(false);
            }

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

            // Before the request, because the window is what the request carries. A session left open all afternoon
            // outgrows any provider's limit, and a rejected request reads to the model as a bug it should retry.
            await CompactAsync(session, model, sink, turnBudget.Token).ConfigureAwait(false);

            var request = new ChatRequest(
                model,
                MessagesFor(session),
                DefinitionsFor(session),
                _options.Temperature,
                _options.MaxOutputTokensPerTurn);

            var (stream, failure) = await TryStreamAsync(client, request, sink, state, turnBudget.Token).ConfigureAwait(false);
            if (stream is null)
            {
                Log.TurnFailed(_logger, state.ServingModelId, state.Iterations, failure ?? "the model call failed", null);
                return await FinishAsync(sink, state.Report(AgentOutcome.Failed, failure)).ConfigureAwait(false);
            }

            if (stream.Interrupted)
            {
                return await FinishAsync(sink, Interruption(session, stream, state, cancellationToken)).ConfigureAwait(false);
            }

            var response = stream.Response!;
            session.Add(response.Message);
            state.Usage = state.Usage is null ? response.Usage : state.Usage + response.Usage;

            // Attributed to the model that answered, which is the fallback once a turn has moved there:
            // `/cost` prices each line from the table by exactly this name.
            session.RecordUsage(state.ServingModelId, response.Usage);

            var ending = await AdvanceAsync(session, response, state, sink, turnBudget.Token).ConfigureAwait(false);
            if (ending is not null)
            {
                return await FinishAsync(sink, ending).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Drops the oldest messages when the transcript has outgrown the configured window.
    /// </summary>
    /// <param name="session">The conversation to trim.</param>
    /// <param name="model">The model whose advertised context window bounds the estimated prompt.</param>
    /// <param name="sink">Receives the notice, so a silent loss of memory becomes a visible one.</param>
    /// <param name="cancellationToken">Cancels the notice.</param>
    /// <returns>How many messages were dropped; zero when the transcript already fits.</returns>
    /// <remarks>
    /// There are two triggers. The message count is the cheap one and keeps a long conversation from growing without
    /// bound; the context fraction is the size-aware one, and it sees what a count cannot — one huge tool result, a long
    /// system prompt or the tool definitions themselves. Both use <see cref="AgentSession.Compact(int)"/>, so neither can
    /// split a tool round, and <c>/compact</c> remains the manual door to the same behavior. The size loop keeps halving
    /// the recent window until the estimate fits; if a cut would orphan the only tool result, it stops rather than
    /// producing a request no provider accepts.
    /// </remarks>
    private async Task<int> CompactAsync(
        AgentSession session,
        ModelDescriptor model,
        IAgentObserver sink,
        CancellationToken cancellationToken)
    {
        var dropped = 0;
        var kept = 0;
        var window = _options.MaxContextMessages;
        if (window > 0 && session.Messages.Count > window)
        {
            kept = Math.Max(window / 2, 1);
            dropped += session.Compact(kept);
        }

        var fraction = _options.CompactAtContextFraction;
        if (fraction > 0 && model.ContextWindow is > 0 and var contextWindow)
        {
            var threshold = (int)Math.Floor(contextWindow * fraction);
            while (PromptTokenEstimator.Estimate(MessagesFor(session), DefinitionsFor(session)) > threshold)
            {
                var before = session.Messages.Count;
                var keep = 1;
                var removed = session.Compact(keep);
                if (removed <= 0 || session.Messages.Count >= before)
                {
                    break;
                }

                kept = keep;
                dropped += removed;
            }
        }

        if (dropped > 0)
        {
            await sink.OnEventAsync(new ContextCompacted(dropped, kept), cancellationToken).ConfigureAwait(false);
        }

        return dropped;
    }

    /// <summary>
    /// Streams one request, turning a provider failure into the detail a report can carry.
    /// </summary>
    /// <param name="client">The model to stream from.</param>
    /// <param name="request">The request to send.</param>
    /// <param name="sink">Receives progress.</param>
    /// <param name="state">The turn's running totals, which the fallback notice updates.</param>
    /// <param name="cancellationToken">Cancels the stream.</param>
    /// <returns>The outcome, or the failure message when the provider failed.</returns>
    private static async Task<(StreamOutcome? Stream, string? Failure)> TryStreamAsync(
        IStreamingChatModel client,
        ChatRequest request,
        IAgentObserver sink,
        TurnState state,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await StreamOnceAsync(client, request, sink, state, cancellationToken).ConfigureAwait(false), null);
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
            // A turn that edited profiled source cannot end on the model's word: the verifier for every profile it
            // touched has to have run, and passed, since the last such edit. Plan mode writes nothing and a registry
            // without that profile's verifier is not gated at all, so both keep the behaviour they had before this rule.
            var gaps = state.UnverifiedGaps;
            if (_options.VerifyEdits && VerifyGate.IsRequired(session, gaps))
            {
                if (_options.AutoVerify && !state.AutoVerified)
                {
                    state.AutoVerified = true;
                    foreach (var gap in gaps)
                    {
                        if (_tools.Find(gap.ToolName) is null)
                        {
                            continue;
                        }

                        var call = new ToolCall(
                            "auto-verify",
                            gap.ToolName,
                            "{}");
                        var result = await InvokeAsync(session, call, state, sink, cancellationToken).ConfigureAwait(false);
                        state.Add(result);
                        session.Add(ChatMessage.FromToolResult(result));
                    }

                    if (state.UnverifiedGaps.Count == 0)
                    {
                        Log.TurnCompleted(_logger, state.Model.QualifiedName, state.Iterations, state.ResultCount, null);
                        return state.Report(AgentOutcome.Completed, null, response.Message.Content);
                    }
                }

                if (state.VerificationNudges < MaxVerificationNudges)
                {
                    state.VerificationNudges++;
                    session.Add(ChatMessage.User(PromptLibrary.VerifyNudgeFor(gaps)));
                    Log.VerificationRequired(_logger, state.Model.QualifiedName, state.VerificationNudges, null);
                    return null;
                }

                // Out of nudges. The answer is accepted, and the report says what it is worth rather than
                // pretending the verifier ran: a turn that can never end is worse than one that ends honestly.
                Log.TurnCompletedUnverified(_logger, state.Model.QualifiedName, state.Iterations, null);
                return state.Report(
                    AgentOutcome.Completed,
                    VerifyGate.Detail(gaps),
                    response.Message.Content,
                    unverified: true);
            }

            Log.TurnCompleted(_logger, state.Model.QualifiedName, state.Iterations, state.ResultCount, null);
            return state.Report(AgentOutcome.Completed, null, response.Message.Content);
        }

        foreach (var call in response.Message.ToolCalls)
        {
            var result = await InvokeAsync(session, call, state, sink, cancellationToken).ConfigureAwait(false);
            state.Add(result);
            session.Add(ChatMessage.FromToolResult(result));

            // An image never travels on the tool role: every compatible provider takes image parts on a user message
            // and none of them on a tool result. So the picture follows the sentence that describes it, and the
            // sentence says where it came from for the transcript a person reads later. A tool that answered with
            // several pictures — one MCP call can — gets the plural sentence, which says the same thing about a reload.
            if (result.Images.Count > 0)
            {
                session.Add(ChatMessage.User(
                    result.Images.Count == 1 ? PromptLibrary.ImageAttached : PromptLibrary.ImagesAttached,
                    result.Images));
            }
        }

        // Checked after the tool round too, because one round can add several large results: the loop must not send
        // them back to a model the session can no longer pay for.
        if (Spent(session) is { } exhausted)
        {
            return state.Report(AgentOutcome.BudgetExhausted, exhausted);
        }

        return null;
    }

    /// <summary>
    /// Answers whether the conversation has spent its token budget, and what to say when it has.
    /// </summary>
    /// <param name="session">The conversation, for its spend and ceiling.</param>
    /// <returns>The detail for a <see cref="AgentOutcome.BudgetExhausted"/> report, or null when there is room.</returns>
    /// <remarks>
    /// The ceiling comes from the session when it has one, so a conversation can be given more room without
    /// rebuilding the agent; <see cref="AgentSession.MaxTotalTokens"/> is the only place that number lives.
    /// </remarks>
    private string? Spent(AgentSession session)
    {
        var ceiling = session.MaxTotalTokens ?? _options.MaxTotalTokens;
        return session.TotalUsage.Total is { } spent && spent > ceiling
            ? $"the session token budget of {ceiling} is spent ({spent} used)."
            : null;
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
    /// <param name="shown">The pictures the conversation already carries, which the turn adds to as its tools attach more.</param>
    /// <param name="checkpoint">The turn's file checkpoint, or null when the host did not enable one.</param>
    /// <param name="verifications">The verifier tools and the language profiles each declares.</param>
    private sealed class TurnState(
        ModelDescriptor model,
        HashSet<ImageContent> shown,
        ITurnCheckpoint? checkpoint,
        IReadOnlyList<VerificationBinding> verifications)
    {
        private readonly List<ToolResult> _results = [];
        private readonly List<FallbackUsed> _fallbacks = [];
        private readonly Dictionary<string, int> _lastEdit = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _lastVerification = new(StringComparer.Ordinal);
        private long _imageBytes;
        /// <summary>Gets the model the turn runs on.</summary>
        public ModelDescriptor Model { get; } = model;

        /// <summary>Gets the file checkpoint every mutating tool receives, or null when checkpoints are off.</summary>
        public ITurnCheckpoint? Checkpoint { get; } = checkpoint;

        /// <summary>Gets or sets the model that served the last attempt.</summary>
        /// <remarks>
        /// Equal to <see cref="Model"/> until a fallback notice arrives, and equal to the fallback after
        /// one, which is what a spend report and the report's <see cref="AgentReport.ModelId"/> both need.
        /// </remarks>
        public string ServingModelId { get; set; } = model.QualifiedName;

        /// <summary>Gets or sets the tokens the turn has spent so far.</summary>
        public TokenUsage? Usage { get; set; }

        /// <summary>Gets or sets how many model round trips the turn has used.</summary>
        public int Iterations { get; set; }

        /// <summary>Gets how many tool results the turn has collected.</summary>
        public int ResultCount => _results.Count;

        /// <summary>Gets the profiles whose last edit has no later passing verifier result.</summary>
        public IReadOnlyList<VerificationGap> UnverifiedGaps
        {
            get
            {
                var gaps = new List<VerificationGap>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var binding in verifications)
                {
                    var name = binding.Profile.Name;
                    if (!seen.Add(name))
                    {
                        continue;
                    }

                    if (_lastEdit.TryGetValue(name, out var edit)
                        && (!_lastVerification.TryGetValue(name, out var verified) || verified < edit))
                    {
                        gaps.Add(new VerificationGap(binding.Profile, binding.ToolName));
                    }
                }

                return gaps;
            }
        }

        /// <summary>Gets or sets how often the loop has asked the model to verify this turn.</summary>
        public int VerificationNudges { get; set; }

        /// <summary>Gets or sets a value indicating whether this turn already used its automatic verifier attempt.</summary>
        public bool AutoVerified { get; set; }

        /// <summary>
        /// Answers what the turn may still attach, for the one tool call about to run.
        /// </summary>
        /// <param name="ceiling">The turn's ceiling in bytes; a non-positive value turns the ceiling off.</param>
        /// <returns>The bytes still available, or <see langword="null"/> when there is no ceiling.</returns>
        /// <remarks>
        /// A number rather than the ceiling, because a tool decides what it alone may attach and must not be handed a
        /// policy it could reinterpret. Zero is a real answer — it says the turn is spent — and is not the same thing
        /// as <see langword="null"/>, which says nobody stated a budget at all.
        /// </remarks>
        public long? ImageBytesRemaining(long ceiling) => ceiling <= 0 ? null : Math.Max(0, ceiling - _imageBytes);

        /// <summary>Accounts for pictures already attached to the prompt before the first tool call.</summary>
        /// <param name="images">The prompt's pictures, in order.</param>
        /// <remarks>
        /// The same counters a tool result uses: a prompt picture is re-sent on every request, so it must consume the
        /// turn's ceiling and be treated as already shown when a tool offers the same bytes again.
        /// </remarks>
        public void SeedImages(IReadOnlyList<ImageContent> images)
        {
            foreach (var image in images)
            {
                _imageBytes += image.ByteCount;
                shown.Add(image);
            }
        }

        /// <summary>Gets the pictures the conversation already carries, including the ones this turn attached.</summary>
        public IReadOnlySet<ImageContent> ImagesAlreadyShown => shown;

        /// <summary>Records one tool result.</summary>
        /// <param name="result">The result to keep.</param>
        /// <remarks>
        /// The two indices are the whole memory the verify gate needs: what the turn last touched, and what it
        /// last proved. Recording them here rather than re-scanning the list keeps a long turn linear and, more
        /// importantly, makes the order explicit — a verify that arrives before an edit is remembered as such.
        /// <para>
        /// The picture total is kept here for the same reason: every result passes through this method, and the
        /// loop reads the total before the next call in the same round, so a tool that attached a picture has already
        /// spent that budget by the time its neighbour asks. The picture itself is recorded in the same pass, so the
        /// next call of the same round is told not to attach it again.
        /// </para>
        /// </remarks>
        public void Add(ToolResult result)
        {
            _results.Add(result);
            var index = _results.Count - 1;

            foreach (var image in result.Images)
            {
                _imageBytes += image.ByteCount;
                shown.Add(image);
            }

            foreach (var binding in verifications)
            {
                var name = binding.Profile.Name;
                if (!result.IsError
                    && !result.IsRefused
                    && string.Equals(result.ToolName, binding.ToolName, StringComparison.Ordinal))
                {
                    _lastVerification[name] = index;
                }

                if (result.Effects.Any(effect => VerifyGate.Edits(binding.Profile, effect)))
                {
                    _lastEdit[name] = index;
                }
            }
        }

        /// <summary>
        /// Records one fallback and the model that now serves the turn.
        /// </summary>
        /// <param name="fallback">The notice the stream carried.</param>
        /// <remarks>
        /// The turn keeps trying the model the request names on its next iteration — the decorator asks
        /// the primary again — so this is a record of what happened, not a permanent switch.
        /// </remarks>
        public void Record(FallbackUsed fallback)
        {
            _fallbacks.Add(fallback);
            ServingModelId = fallback.FallbackModelId;
        }

        /// <summary>
        /// Builds the report for an outcome.
        /// </summary>
        /// <param name="outcome">How the turn ended.</param>
        /// <param name="detail">Why, when the outcome needs explaining.</param>
        /// <param name="finalText">The answer, when there is one.</param>
        /// <param name="unverified">Whether a profile-owned edit went without a passing verification.</param>
        /// <returns>The report.</returns>
        public AgentReport Report(
            AgentOutcome outcome,
            string? detail = null,
            string? finalText = null,
            bool unverified = false) => new()
        {
            Outcome = outcome,
            ModelId = ServingModelId,
            FinalText = finalText,
            Usage = Usage ?? TokenUsage.Unknown,
            Iterations = Iterations,
            ToolResults = [.. _results],
            Fallbacks = [.. _fallbacks],
            Detail = detail,
            IsUnverified = unverified,
        };
    }

    /// <summary>
    /// Builds the messages one request carries, adding the plan rule while plan mode is on.
    /// </summary>
    /// <param name="session">The conversation.</param>
    /// <returns>The transcript, with the plan instruction in front when the session is planning.</returns>
    private static IReadOnlyList<ChatMessage> MessagesFor(AgentSession session) =>
        session.Mode == AgentMode.Plan
            ? [ChatMessage.System(PromptLibrary.PlanModeInstruction), .. session.Messages]
            : session.Messages;

    /// <summary>
    /// Collects the pictures the conversation already carries, so no tool attaches a second copy of one.
    /// </summary>
    /// <param name="session">The conversation, as it stands when the turn begins.</param>
    /// <returns>The pictures in the transcript, which the turn adds to as its tools attach more.</returns>
    /// <remarks>
    /// Derived from the transcript rather than remembered in a side table, which is what makes the three awkward cases
    /// right without a special case for any of them: a compacted conversation has dropped its older messages and may show
    /// those pictures again, a resumed one carries none at all because a session file holds no images, and a turn that
    /// attaches a picture adds it here as it goes. Identity is the <see cref="ImageContent"/> itself — type and bytes —
    /// because that is what a provider is charged for and what the model would otherwise see twice.
    /// </remarks>
    private static HashSet<ImageContent> AlreadyShown(AgentSession session)
    {
        var shown = new HashSet<ImageContent>();
        foreach (var message in session.Messages)
        {
            foreach (var image in message.Images)
            {
                shown.Add(image);
            }
        }

        return shown;
    }

    /// <summary>
    /// Builds the tool list one request advertises.
    /// </summary>
    /// <param name="session">The conversation.</param>
    /// <returns>
    /// Every definition in act mode; only the read-only ones in plan mode, so a model that ignores the
    /// instruction still cannot name a tool it was never given.
    /// </returns>
    private IReadOnlyList<ToolDefinition> DefinitionsFor(AgentSession session) =>
        session.Mode == AgentMode.Plan
            ? [.. _tools.Tools.Where(tool => tool.Safety == ToolSafety.ReadOnly).Select(tool => tool.Definition)]
            : _tools.Definitions;

    private async Task<ToolResult> InvokeAsync(
        AgentSession session,
        ToolCall call,
        TurnState state,
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
                session.Mode == AgentMode.Plan
                    ? "plan mode is on, so every destructive tool is refused. Do not repeat this call; describe the change you would make instead."
                    : $"the developer refused this call (approval mode: {session.Approval}). Do not repeat it; explain what you need or use a read-only tool.");
        }
        else
        {
            result = await ExecuteAsync(
                    tool,
                    call,
                    session,
                    sink,
                    state.ImageBytesRemaining(_options.MaxTurnImageBytes),
                    state.ImagesAlreadyShown,
                    state.Checkpoint,
                    cancellationToken)
                .ConfigureAwait(false);
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

        if (session.Mode == AgentMode.Plan)
        {
            return ApprovalOutcome.Denied;
        }

        // A tool the developer allowed earlier in this conversation does not ask again, whatever the mode says: the
        // answer was about this transcript, and `/approval` can still take the permission away.
        if (session.IsToolApproved(call.Name))
        {
            return ApprovalOutcome.Approved;
        }

        var outcome = session.Approval switch
        {
            ApprovalMode.AllowAll => ApprovalOutcome.Approved,
            ApprovalMode.ReadOnly => ApprovalOutcome.Denied,
            _ => await _approver.DecideAsync(
                new ToolApprovalRequest(call.Name, tool.Safety, Describe(tool, call), call.ArgumentsJson)
                {
                    Preview = await DescribeChangeAsync(tool, call, session, cancellationToken).ConfigureAwait(false),
                },
                cancellationToken).ConfigureAwait(false),
        };

        switch (outcome)
        {
            case ApprovalOutcome.ApprovedForTool:
                session.ApproveTool(call.Name);
                return ApprovalOutcome.Approved;

            case ApprovalOutcome.ApprovedForSession:
                // "Yes, and don't ask again" is a decision about this conversation, so it is written to the
                // session the loop already reads and nowhere else: the settings file is a default for future
                // processes, and a tired click at a prompt must not become one.
                session.Approval = ApprovalMode.AllowAll;
                return ApprovalOutcome.Approved;

            default:
                return outcome;
        }
    }

    /// <summary>
    /// Runs a tool, converting the failures a tool is allowed to report into results the model can read.
    /// </summary>
    /// <remarks>
    /// The catch list is deliberately short: cancellation must escape, and an unexpected exception is a
    /// bug that should surface rather than be papered over as a tool error the model will retry forever.
    /// </remarks>
    /// <param name="tool">The tool to run.</param>
    /// <param name="call">The model's call.</param>
    /// <param name="session">The session, for the workspace.</param>
    /// <param name="sink">Receives the tool's live output while it runs.</param>
    /// <param name="imageBytesRemaining">What the turn may still attach, or null when its ceiling is off.</param>
    /// <param name="imagesAlreadyShown">The pictures the conversation already carries.</param>
    /// <param name="checkpoint">The turn's file checkpoint, or null when the host did not enable one.</param>
    /// <param name="cancellationToken">Cancels the invocation.</param>
    /// <returns>The result, with a readable failure instead of an exception where the tool allows one.</returns>
    private static async Task<ToolResult> ExecuteAsync(
        ITool tool,
        ToolCall call,
        AgentSession session,
        IAgentObserver sink,
        long? imageBytesRemaining,
        IReadOnlySet<ImageContent> imagesAlreadyShown,
        ITurnCheckpoint? checkpoint,
        CancellationToken cancellationToken)
    {
        // Live output is a courtesy, never a precondition: the tool runs identically whether or not the
        // observer wants the text, and the result it returns is the complete record either way. The model
        // and the approval mode travel with the context because a tool that delegates work needs the
        // conversation's answers, not the host's startup settings. The picture budget travels the same way and for
        // the same reason: only the turn knows how much of it is spent, and a tool that attaches a picture must say
        // so in its own result rather than in a message the loop writes afterwards. The pictures already in the
        // transcript travel with it so no door attaches the same bytes a second time, which is a fact the door has to
        // state in its own words — a success for a picture that is already there, a note for one that is not sent.
        var context = new ToolInvocationContext(session.Workspace)
        {
            Output = new ObserverOutput(sink, call),
            Model = session.ActiveModel,
            Approval = session.Approval,
            ImageBytesRemaining = imageBytesRemaining,
            ImagesAlreadyShown = imagesAlreadyShown,
            Checkpoint = checkpoint,
        };
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

    /// <summary>
    /// Asks a tool what it would change, never letting a preview failure become an approval failure.
    /// </summary>
    /// <param name="tool">The tool about to run.</param>
    /// <param name="call">The call to preview.</param>
    /// <param name="session">The session, for the workspace.</param>
    /// <param name="cancellationToken">Cancels the preview.</param>
    /// <returns>The preview, or <see langword="null"/> when the tool has none or could not produce it.</returns>
    private static async ValueTask<string?> DescribeChangeAsync(
        ITool tool,
        ToolCall call,
        AgentSession session,
        CancellationToken cancellationToken)
    {
        if (tool is not IToolPreview preview)
        {
            return null;
        }

        try
        {
            return await preview
                .DescribeChangeAsync(call, new ToolInvocationContext(session.Workspace), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ToolArgumentException or IOException or UnauthorizedAccessException or SharpCoreAgentException)
        {
            // A preview is a courtesy: the call itself will report the same problem with a better message.
            return null;
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
    /// <param name="client">The model to stream from.</param>
    /// <param name="request">The request that produced the stream.</param>
    /// <param name="sink">Receives progress.</param>
    /// <param name="state">The turn's running totals; a fallback notice is recorded in them.</param>
    /// <param name="cancellationToken">Cancels the drain.</param>
    /// <returns>The completion, the text shown and whether the stream was cut short.</returns>
    /// <remarks>
    /// A fallback notice is not content: it is recorded, announced to the observer and otherwise
    /// skipped, so a stream the primary served is indistinguishable from one it never refused.
    /// </remarks>
    private static async Task<StreamOutcome> StreamOnceAsync(
        IStreamingChatModel client,
        ChatRequest request,
        IAgentObserver sink,
        TurnState state,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        ChatResponse? completed = null;

        // Every attempt starts on the model the request names; a notice moves it for the rest of the
        // stream, and that is the name the response is billed under.
        state.ServingModelId = request.Model.QualifiedName;

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
                    case FallbackUsed fallback:
                        state.Record(fallback);
                        await sink.OnEventAsync(new FallbackAttempted(fallback), CancellationToken.None).ConfigureAwait(false);
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
    /// Turns a running tool's output into events, tagged with the call that produced them.
    /// </summary>
    /// <param name="observer">Where the events go.</param>
    /// <param name="call">The call being executed.</param>
    /// <remarks>
    /// The observer is called with <see cref="CancellationToken.None"/> for the same reason every other
    /// event is: the token that cancels a turn must not also decide whether a diagnostic already produced
    /// reaches the developer, and an observer that throws on cancellation would turn a Ctrl+C into a tool
    /// failure.
    /// </remarks>
    private sealed class ObserverOutput(IAgentObserver observer, ToolCall call) : IToolOutputSink
    {
        /// <inheritdoc />
        public ValueTask WriteAsync(string text, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(text))
            {
                return ValueTask.CompletedTask;
            }

            return observer.OnEventAsync(new ToolOutputDelta(call.Id, call.Name, text), CancellationToken.None);
        }
    }


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

        internal static readonly Action<ILogger, string, int, Exception?> VerificationRequired =
            LoggerMessage.Define<string, int>(
                LogLevel.Information,
                new EventId(1006, nameof(VerificationRequired)),
                "Turn on {Model} edited .NET source without a passing dotnet_verify; asking again (nudge {Nudge}).");

        internal static readonly Action<ILogger, string, int, Exception?> TurnCompletedUnverified =
            LoggerMessage.Define<string, int>(
                LogLevel.Warning,
                new EventId(1007, nameof(TurnCompletedUnverified)),
                "Turn on {Model} completed in {Iterations} iteration(s) with a .NET edit that dotnet_verify never confirmed.");
    }
}
