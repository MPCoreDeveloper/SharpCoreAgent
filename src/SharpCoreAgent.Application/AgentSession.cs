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

    /// <summary>Gets the transcript so far, in order.</summary>
    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>Gets the token usage accumulated across every turn, or unknown when nothing has been reported.</summary>
    public TokenUsage TotalUsage => _totalUsage ?? TokenUsage.Unknown;

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

        // The first sighting seeds the per-model row with the reported numbers, so the addition below
        // counts this request exactly once.
        var previous = _usage.TryGetValue(modelId, out var existing)
            ? existing
            : new ModelUsage(modelId, usage.InputTokens, usage.OutputTokens, 0);

        _usage[modelId] = new ModelUsage(
            modelId,
            Sum(previous.InputTokens, usage.InputTokens),
            Sum(previous.OutputTokens, usage.OutputTokens),
            previous.Turns + 1);
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
