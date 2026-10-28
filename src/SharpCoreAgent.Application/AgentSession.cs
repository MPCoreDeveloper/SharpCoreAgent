using System.Globalization;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Sessions;

namespace SharpCoreAgent.Application;

/// <summary>
/// One conversation: the transcript, the active model, the approval mode and the running cost.
/// </summary>
/// <remarks>
/// The session is the only mutable object in the agent's vocabulary. The loop reads it, appends to it
/// and reports on it, and a <see cref="SessionSnapshot"/> is a lossless copy of it that can be written
/// to disk mid-conversation.
/// </remarks>
public sealed class AgentSession
{
    private readonly List<ChatMessage> _messages;
    private readonly Dictionary<string, ModelUsage> _usage;
    private readonly HashSet<string> _approvedTools = new(StringComparer.OrdinalIgnoreCase);
    private readonly DateTimeOffset _createdAtUtc;
    private TokenUsage? _totalUsage;

    /// <summary>
    /// Creates a session.
    /// </summary>
    /// <param name="workspace">The directory the conversation is confined to.</param>
    /// <param name="model">The model the conversation starts on.</param>
    /// <param name="approval">The approval mode.</param>
    /// <param name="systemPrompt">The system prompt; when null or blank the transcript starts empty of policy.</param>
    public AgentSession(Workspace workspace, ModelDescriptor model, ApprovalMode approval, string? systemPrompt = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(model);

        Workspace = workspace;
        ActiveModel = model;
        Approval = approval;
        _messages = [];
        _usage = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);
        _createdAtUtc = DateTimeOffset.UtcNow;

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            _messages.Add(ChatMessage.System(systemPrompt));
        }
    }

    /// <summary>Gets the workspace every tool call is confined to.</summary>
    public Workspace Workspace { get; }

    /// <summary>Gets the model the next turn will use.</summary>
    public ModelDescriptor ActiveModel { get; private set; }

    /// <summary>Gets or sets the approval mode. Switching applies to the next tool call.</summary>
    public ApprovalMode Approval { get; set; }

    /// <summary>Gets or sets what the next turn is allowed to do. <c>/mode</c> switches it mid-conversation.</summary>
    /// <remarks>
    /// The loop reads this rather than the host's options, so a plan turn and an act turn can share one
    /// transcript: the developer reads the plan, then says "go" without losing the reasoning behind it.
    /// </remarks>
    public AgentMode Mode { get; set; } = AgentMode.Act;

    /// <summary>Gets the tools the developer allowed for the rest of this conversation.</summary>
    /// <remarks>
    /// Empty for a resumed transcript on purpose: an approval is a decision about a conversation in
    /// progress, so reusing a session file must not re-arm the commands a developer once said yes to.
    /// This is the narrow answer to "stop asking" <c>/approval allow</c> cannot give — approving
    /// <c>run_shell_command</c> here leaves every file edit still asking.
    /// </remarks>
    public IReadOnlyCollection<string> ApprovedTools => _approvedTools;

    /// <summary>Answers whether every later call of one tool may run without asking.</summary>
    /// <param name="toolName">The tool's name, matched case-insensitively as the registry resolves it.</param>
    /// <returns><see langword="true"/> when the loop may skip the approval prompt for that tool.</returns>
    public bool IsToolApproved(string toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && _approvedTools.Contains(toolName);

    /// <summary>
    /// Allows every later call of one tool in this conversation.
    /// </summary>
    /// <param name="toolName">The tool's name.</param>
    /// <exception cref="ArgumentException"><paramref name="toolName"/> is null, empty or whitespace.</exception>
    public void ApproveTool(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        _approvedTools.Add(toolName);
    }

    /// <summary>
    /// Forgets every tool approval, so the next destructive call asks again.
    /// </summary>
    /// <returns>How many tools were allowed; zero when there was nothing to forget.</returns>
    /// <remarks>
    /// The escape hatch for the one mistake a standing approval makes possible: a developer who allowed
    /// <c>run_shell_command</c> an hour ago and has since opened a workspace they do not trust.
    /// </remarks>
    public int ForgetApprovedTools()
    {
        var count = _approvedTools.Count;
        _approvedTools.Clear();
        return count;
    }

    /// <summary>Gets the transcript so far, in order.</summary>
    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>Gets the token usage accumulated across every turn, or unknown when nothing has been reported.</summary>
    public TokenUsage TotalUsage => _totalUsage ?? TokenUsage.Unknown;

    /// <summary>
    /// Gets or sets the ceiling on <see cref="TokensSpent"/> for this conversation, or null to use the loop's default.
    /// </summary>
    /// <remarks>
    /// The budget belongs to the conversation rather than to the process. A resumed transcript has already paid for
    /// the tokens recorded in it, so a resumed session starts with that spend as headroom instead of being killed by
    /// history it did not create; and a developer who decides to spend more raises the number here instead of
    /// restarting and losing the reasoning the transcript holds. <see cref="CodingAgent"/> reads this before it falls
    /// back to <see cref="AgentOptions.MaxTotalTokens"/>; the CLI writes it with <c>/budget</c>.
    /// </remarks>
    public long? MaxTotalTokens { get; set; }

    /// <summary>Gets the tokens reported so far, which is what the budget is measured against.</summary>
    public long TokensSpent => TotalUsage.Total ?? 0;

    /// <summary>
    /// Raises the budget, so a conversation that ran out continues without losing its transcript.
    /// </summary>
    /// <param name="additional">How many tokens to add.</param>
    /// <param name="fallback">The ceiling to start from when the session has not set one yet.</param>
    /// <returns>The new ceiling.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="additional"/> is not positive.</exception>
    /// <remarks>
    /// Adding to an unlimited ceiling stays unlimited instead of wrapping negative, which would read as a budget
    /// that is already spent.
    /// </remarks>
    public long RaiseBudget(long additional, long fallback)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(additional);

        var current = MaxTotalTokens ?? fallback;
        MaxTotalTokens = current > long.MaxValue - additional ? long.MaxValue : current + additional;
        return MaxTotalTokens.Value;
    }

    /// <summary>Gets the usage attributed to each model that served the session, ordered by model name.</summary>
    public IReadOnlyList<ModelUsage> UsageByModel =>
        [.. _usage.Values.OrderBy(usage => usage.ModelId, StringComparer.Ordinal)];

    /// <summary>Gets when the session started.</summary>
    public DateTimeOffset CreatedAtUtc => _createdAtUtc;

    /// <summary>
    /// Appends a message.
    /// </summary>
    /// <param name="message">The message to append.</param>
    public void Add(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _messages.Add(message);
    }

    /// <summary>
    /// Records the usage one model request reported.
    /// </summary>
    /// <param name="modelId">The <c>provider:model</c> name that served the request.</param>
    /// <param name="usage">The reported usage; <see cref="TokenUsage.Unknown"/> is recorded as unknown, never as zero.</param>
    public void RecordUsage(string modelId, TokenUsage usage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(usage);

        // A session that has not heard from a provider yet reports unknown, not a confident zero.
        _totalUsage = _totalUsage is null ? usage : _totalUsage + usage;

        // The first sighting starts from zero, so the addition below counts this request exactly once.
        // Seeding with the reported numbers and then adding them again reported double the first turn.
        var previous = _usage.TryGetValue(modelId, out var existing)
            ? existing
            : new ModelUsage(modelId, 0, 0, 0);

        _usage[modelId] = new ModelUsage(
            modelId,
            Sum(previous.InputTokens, usage.InputTokens),
            Sum(previous.OutputTokens, usage.OutputTokens),
            previous.Turns + 1);
    }

    /// <summary>
    /// Drops the oldest part of the conversation, keeping the system prompts and the most recent messages.
    /// </summary>
    /// <param name="keepRecent">How many non-system messages to keep; must be positive.</param>
    /// <returns>The number of messages removed; zero when the transcript already fits.</returns>
    /// <remarks>
    /// Long sessions fail in the same way whether or not a token counter exists: the request grows until
    /// the provider rejects it or the budget stops the turn. Compaction is the escape hatch the developer
    /// controls with <c>/compact</c>. It keeps every system prompt verbatim and never leaves a tool
    /// result without its call, because a window that starts mid-tool-round is not a valid request for
    /// any provider.
    /// </remarks>
    public int Compact(int keepRecent)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keepRecent, 1);

        var systemCount = 0;
        while (systemCount < _messages.Count && _messages[systemCount].Role == ChatRole.System)
        {
            systemCount++;
        }

        if (_messages.Count - systemCount <= keepRecent)
        {
            return 0;
        }

        var start = _messages.Count - keepRecent;
        while (start < _messages.Count && _messages[start].Role == ChatRole.Tool)
        {
            start++;
        }

        if (start >= _messages.Count)
        {
            return 0;
        }

        var removed = start - systemCount;
        if (removed <= 0)
        {
            return 0;
        }

        _messages.RemoveRange(systemCount, removed);
        _messages.Insert(
            systemCount,
            ChatMessage.System(
                string.Create(CultureInfo.InvariantCulture, $"Earlier conversation elided: {removed} message(s) were dropped to keep the context window small. Ask again if you need a detail that is no longer here.")));

        return removed;
    }

    /// <summary>
    /// Switches the model used by the next turn. The transcript is kept.
    /// </summary>
    /// <param name="model">The new model.</param>
    public void SwitchModel(ModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        ActiveModel = model;
    }

    /// <summary>
    /// Copies the session into the shape a session file stores.
    /// </summary>
    /// <param name="updatedAtUtc">The timestamp to record as the last write.</param>
    /// <returns>A snapshot that can be written to disk and reloaded.</returns>
    public SessionSnapshot ToSnapshot(DateTimeOffset updatedAtUtc) => new(
        Workspace.Root,
        ActiveModel.Provider,
        ActiveModel.Id,
        Approval,
        _createdAtUtc,
        updatedAtUtc,
        [.. _messages],
        UsageByModel);

    /// <summary>
    /// Rebuilds a session from a snapshot written by <see cref="ToSnapshot(DateTimeOffset)"/>.
    /// </summary>
    /// <param name="snapshot">The persisted state.</param>
    /// <param name="workspace">The workspace to resume in; normally the root recorded in the snapshot.</param>
    /// <param name="model">The resolved descriptor for the snapshot's provider and model identifiers.</param>
    /// <returns>A session positioned exactly where the archived one left off.</returns>
    public static AgentSession FromSnapshot(SessionSnapshot snapshot, Workspace workspace, ModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(model);

        var session = new AgentSession(workspace, model, snapshot.Approval);
        foreach (var message in snapshot.Messages)
        {
            session.Add(message);
        }

        foreach (var usage in snapshot.Usage)
        {
            // The snapshot already holds sums; restoring must not add them to a per-request tally.
            session._usage[usage.ModelId] = usage;
            var usageTotal = new TokenUsage(usage.InputTokens, usage.OutputTokens);
            session._totalUsage = session._totalUsage is null ? usageTotal : session._totalUsage + usageTotal;
        }

        return session;
    }

    private static int? Sum(int? left, int? right) => left is null || right is null ? null : left + right;
}
