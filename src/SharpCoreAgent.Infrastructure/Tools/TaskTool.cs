using SharpCoreAgent.Application;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Runs one self-contained task in a sub-agent with its own transcript and returns that sub-agent's report.
/// </summary>
/// <remarks>
/// The point is context, not parallelism. A broad investigation can cost fifteen tool calls and thirty
/// thousand tokens of intermediate output that the main conversation will never refer to again; a
/// sub-agent burns that context in its own window and hands back the answer. The price is that it starts
/// blind: it sees the instruction and nothing else, which is why the tool's description tells the model to
/// put every fact the child needs into the prompt.
/// <para>
/// Three invariants hold by construction. The child registry is this registry minus <c>task</c>, so delegation
/// cannot recurse. The child never prompts: it inherits <see cref="ApprovalMode.AllowAll"/> only when the
/// conversation already allows everything, and is <see cref="ApprovalMode.ReadOnly"/> otherwise, because a
/// modal question raised from inside a tool call would arrive while the developer is watching the parent turn
/// and has no idea which of two nested turns is asking. And the child's non-read effects are copied onto this
/// result, so a delegated edit still reaches the turn's <c>VerifyGate</c> — a sub-agent is not a way to launder
/// an unverified change.
/// </para>
/// </remarks>
public sealed class TaskTool : ITool, IToolPreview
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "task";

    /// <summary>Gets the most iterations a sub-agent may use, whatever the caller asks for.</summary>
    public const int MaxSubAgentIterations = 24;

    /// <summary>Gets the most characters a delegation's label may use before it is cut.</summary>
    public const int MaxDescriptionChars = 120;

    /// <summary>Gets the iterations a sub-agent is given when the caller does not say.</summary>
    public const int DefaultSubAgentIterations = 12;

    private const string Schema =
        """{"type":"object","properties":{"description":{"type":"string","description":"Short label for the work, shown in the transcript and in an approval prompt: 'find every caller of PriceCalculator'."},"prompt":{"type":"string","description":"The complete instruction. The sub-agent sees this and nothing else of this conversation, so state the goal, the paths you already know, and what done looks like."},"max_iterations":{"type":"integer","minimum":1,"maximum":24,"description":"Round trips the sub-agent may take before it must report. Defaults to 12."}},"required":["prompt"],"additionalProperties":false}""";

    private readonly IChatModelResolver _models;
    private readonly Func<IToolRegistry> _registry;
    private readonly AgentOptions _options;
    private readonly ITurnCheckpointJournal? _checkpoints;

    /// <summary>
    /// Creates the delegation tool.
    /// </summary>
    /// <param name="models">Resolves the conversation's model for the child turn.</param>
    /// <param name="registry">
    /// Reads the registry this build serves from: the child gets every tool in it except this one. A
    /// <see cref="Func{TResult}"/> rather than the registry itself, because a tool that is part of the tool set
    /// cannot be handed that set while the set is being built — the container reports a circular dependency and
    /// every host fails at startup.
    /// </param>
    /// <param name="options">The budgets the child inherits, narrowed to the sub-agent's iteration ceiling.</param>
    /// <param name="checkpoints">Optional turn journal the child shares, so a delegated edit joins the parent's undo step.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> is out of range.</exception>
    public TaskTool(
        IChatModelResolver models,
        Func<IToolRegistry> registry,
        AgentOptions options,
        ITurnCheckpointJournal? checkpoints = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);

        _models = models;
        _registry = registry;
        _options = options.Validate();
        _checkpoints = checkpoints;
    }

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Delegate one self-contained investigation to a sub-agent with its own transcript, and get back only its answer. The sub-agent sees the prompt and nothing else of this conversation, so state the goal, the paths you already know and what done looks like. Worth its cost when answering takes many tool calls whose output you will never refer to again; do edits yourself. A sub-agent may write only when this conversation already allows every tool without asking.",
        Schema);

    /// <inheritdoc />
    /// <remarks>
    /// Destructive rather than read-only even though the default child cannot write: the safety class is a static
    /// property and the child's powers are decided at call time, so a planning or <c>readonly</c> turn must refuse
    /// the delegation itself rather than rely on what the child would have been allowed to do.
    /// </remarks>
    public ToolSafety Safety => ToolSafety.Destructive;

    /// <inheritdoc />
    public async ValueTask<ToolResult> ExecuteAsync(
        ToolCall toolCall,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(context);

        using var arguments = ToolArgumentReader.Parse(Name, toolCall.ArgumentsJson);
        var prompt = arguments.GetRequiredString("prompt");
        var description = Describe(arguments.GetOptionalString("description"), prompt);
        var iterations = arguments.GetOptionalInt(
            "max_iterations",
            DefaultSubAgentIterations,
            minimum: 1,
            maximum: MaxSubAgentIterations);

        if (context.Model is not { } model)
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                "this invocation did not carry the conversation's model, so there is no sub-agent to run.");
        }

        // Read now, not at construction: the container cannot hand this tool the set that contains it, and --mcp
        // replaces the registry with a superset of what was registered at startup. Filtered by name rather than
        // trusted to be absent, so a host that registered this tool twice still cannot delegate twice.
        var tools = new ToolRegistry(_registry().Tools.Where(
            tool => !string.Equals(tool.Definition.Name, Name, StringComparison.Ordinal)));

        var child = new AgentSession(
            context.Workspace,
            model,
            context.Approval == ApprovalMode.AllowAll ? ApprovalMode.AllowAll : ApprovalMode.ReadOnly,
            PromptLibrary.SubAgentPrompt);

        var agent = new CodingAgent(
            _models,
            tools,
            NoPromptApprover.Instance,
            _options with { MaxIterations = Math.Clamp(iterations, 1, MaxSubAgentIterations) },
            checkpoints: _checkpoints);

        await NarrateAsync(context.Output, TaskFormat.Started(description, iterations), cancellationToken)
            .ConfigureAwait(false);

        var report = await agent.RunTurnAsync(child, prompt, observer: null, cancellationToken)
            .ConfigureAwait(false);

        await NarrateAsync(context.Output, TaskFormat.Finished(description, report), cancellationToken)
            .ConfigureAwait(false);

        // A cancelled parent is not a result. The loop reports the child's cancellation rather than throwing, so the
        // tool is where "Ctrl+C stops both" becomes the OperationCanceledException every tool contract promises.
        if (report.Outcome == AgentOutcome.Cancelled)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return Answer(toolCall, report, child, description, iterations);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The developer is approving an instruction, so the instruction is what the prompt shows: the label, the
    /// iteration ceiling and the prompt itself, instead of a JSON line whose escaping hides both.
    /// </remarks>
    public ValueTask<string?> DescribeChangeAsync(
        ToolCall toolCall,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(context);

        using var arguments = ToolArgumentReader.Parse(Name, toolCall.ArgumentsJson);
        var prompt = arguments.GetRequiredString("prompt");
        var description = Describe(arguments.GetOptionalString("description"), prompt);
        var iterations = arguments.GetOptionalInt(
            "max_iterations",
            DefaultSubAgentIterations,
            minimum: 1,
            maximum: MaxSubAgentIterations);

        return ValueTask.FromResult<string?>(TaskFormat.Preview(description, iterations, prompt));
    }

    /// <summary>
    /// Turns the child's report into the result the parent's model reads.
    /// </summary>
    /// <param name="toolCall">The call being answered.</param>
    /// <param name="report">How the child's turn ended.</param>
    /// <param name="child">The child's session, read only for the last thing it said.</param>
    /// <param name="description">The label for the header.</param>
    /// <param name="iterations">The ceiling the child was given.</param>
    /// <returns>The result, with the child's non-read effects attached either way.</returns>
    /// <remarks>
    /// Effects travel with a failure too. A child that edited a file and then ran out of model calls did change the
    /// disk, and a failure that reported nothing would let the parent's turn look untouched.
    /// </remarks>
    private ToolResult Answer(
        ToolCall toolCall,
        AgentReport report,
        AgentSession child,
        string description,
        int iterations)
    {
        var effects = Forwarded(report);
        var header = TaskFormat.Header(description, report.Iterations, report.Usage);

        if (report.Outcome != AgentOutcome.Completed)
        {
            return Failure(
                toolCall,
                string.Concat(header, "\n", TaskFormat.NotCompleted(report.Outcome, iterations, report.Detail)),
                effects);
        }

        // FinalText is the answer of every completed turn the loop reports; the transcript is read only when the
        // report's text is empty. Neither being present is a real outcome, and it is the model's to react to.
        var answer = string.IsNullOrWhiteSpace(report.FinalText) ? LastAssistantText(child) : report.FinalText;
        if (string.IsNullOrWhiteSpace(answer))
        {
            return Failure(toolCall, string.Concat(header, "\n", TaskFormat.Nothing), effects);
        }

        var text = TextLimits.Bound(string.Concat(header, "\n", answer), _options.MaxToolResultChars, out var truncated);
        return ToolResult.Success(toolCall.Id, Name, text, [.. effects]) with { IsTruncated = truncated };
    }

    /// <summary>
    /// Builds a failed result whose text respects the caller's budget.
    /// </summary>
    /// <param name="toolCall">The call being answered.</param>
    /// <param name="text">The header and the sentence that explains the outcome.</param>
    /// <param name="effects">The child's changes, which a failure does not undo.</param>
    /// <returns>The result.</returns>
    private ToolResult Failure(ToolCall toolCall, string text, IReadOnlyList<ToolEffect> effects)
    {
        var bounded = TextLimits.Bound(text, _options.MaxToolResultChars, out var truncated);

        return ToolResult.Failure(toolCall.Id, Name, bounded) with
        {
            Effects = [.. effects],
            IsTruncated = truncated,
        };
    }

    /// <summary>
    /// Collects the child's changes for the parent's report.
    /// </summary>
    /// <param name="report">The child's report.</param>
    /// <returns>The created, modified, deleted and command effects, in the order the child produced them.</returns>
    /// <remarks>
    /// <see cref="ToolEffectKind.Read"/> is dropped on purpose: the parent did not read those files, and forwarding
    /// them would let a listing-heavy child inflate the parent's <c>FilesTouched</c> into a claim it never made.
    /// </remarks>
    private static ToolEffect[] Forwarded(AgentReport report) =>
        [.. report.ToolResults
            .SelectMany(result => result.Effects)
            .Where(effect => effect.Kind != ToolEffectKind.Read)];

    /// <summary>
    /// Reads the last thing the child said, for a turn whose report carries no final text.
    /// </summary>
    /// <param name="child">The child's session.</param>
    /// <returns>The last non-empty assistant message, or null when the child said nothing at all.</returns>
    private static string? LastAssistantText(AgentSession child)
    {
        for (var index = child.Messages.Count - 1; index >= 0; index--)
        {
            var message = child.Messages[index];
            if (message.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(message.Content))
            {
                return message.Content;
            }
        }

        return null;
    }

    /// <summary>
    /// Writes one line to the developer's terminal, when anybody is listening.
    /// </summary>
    /// <param name="output">The sink, or null in tests and quiet runs.</param>
    /// <param name="line">The line to show.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the line has been rendered.</returns>
    private static async ValueTask NarrateAsync(
        IToolOutputSink? output,
        string line,
        CancellationToken cancellationToken)
    {
        if (output is not null)
        {
            await output.WriteAsync(string.Concat(line, "\n"), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads the label the header shows.
    /// </summary>
    /// <param name="description">The label the model supplied, if any.</param>
    /// <param name="prompt">The instruction, whose first line is the fallback label.</param>
    /// <returns>One line of at most <see cref="MaxDescriptionChars"/> characters.</returns>
    /// <remarks>
    /// A label is a label: a model that writes a paragraph into it gets it flattened and cut rather than an error,
    /// because refusing to delegate over the wording of a header would cost a turn and teach nothing.
    /// </remarks>
    private static string Describe(string? description, string prompt)
    {
        var text = string.IsNullOrWhiteSpace(description) ? FirstLine(prompt) : description;
        var label = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (label.Length == 0)
        {
            return "delegated task";
        }

        return label.Length <= MaxDescriptionChars
            ? label
            : string.Concat(label[..(MaxDescriptionChars - 1)], "…");
    }

    /// <summary>
    /// Reads a prompt's first line.
    /// </summary>
    /// <param name="prompt">The instruction.</param>
    /// <returns>The first line, trimmed, or the whole text when it has no line break.</returns>
    private static string FirstLine(string prompt)
    {
        var text = prompt.TrimStart();
        var newline = text.AsSpan().IndexOfAny('\r', '\n');
        return newline < 0 ? text : text[..newline].Trim();
    }
}
