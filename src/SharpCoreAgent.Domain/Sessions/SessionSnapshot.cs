using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Conversation;

namespace SharpCoreAgent.Domain.Sessions;

/// <summary>
/// Token usage attributed to one model.
/// </summary>
/// <param name="ModelId">The <c>provider:model</c> name.</param>
/// <param name="InputTokens">Prompt tokens, or <see langword="null"/> when any contributing request was unreported.</param>
/// <param name="OutputTokens">Completion tokens, or <see langword="null"/> when any contributing request was unreported.</param>
/// <param name="Turns">How many model round trips this model served.</param>
public sealed record ModelUsage(string ModelId, int? InputTokens, int? OutputTokens, int Turns);

/// <summary>
/// A conversation's complete state, in the shape a session file stores it.
/// </summary>
/// <param name="WorkspaceRoot">The root the conversation is confined to.</param>
/// <param name="Provider">The active provider key.</param>
/// <param name="ModelId">The active model identifier.</param>
/// <param name="Approval">The approval mode in force.</param>
/// <param name="CreatedAtUtc">When the session started.</param>
/// <param name="UpdatedAtUtc">When the session was last written.</param>
/// <param name="Messages">The transcript, system message first.</param>
/// <param name="Usage">Per-model token usage.</param>
/// <remarks>
/// The snapshot holds no credentials by construction: it is written to disk and may be committed,
/// mailed or pasted into an issue.
/// </remarks>
public sealed record SessionSnapshot(
    string WorkspaceRoot,
    string Provider,
    string ModelId,
    ApprovalMode Approval,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ModelUsage> Usage);

/// <summary>
/// Reads and writes session snapshots.
/// </summary>
public interface ISessionStore
{
    /// <summary>
    /// Writes a snapshot atomically: a reader never sees a half-written file.
    /// </summary>
    /// <param name="snapshot">The state to persist.</param>
    /// <param name="path">The destination file.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the file is in place.</returns>
    Task SaveAsync(SessionSnapshot snapshot, string path, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a snapshot.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The persisted state.</returns>
    Task<SessionSnapshot> LoadAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// Supplies provider credentials by name, never by value.
/// </summary>
/// <remarks>
/// The product only ever passes a credential's <em>name</em> around; the value is read as late as
/// possible and never logged, stored or included in an error message.
/// </remarks>
public interface ICredentialStore
{
    /// <summary>
    /// Finds a credential.
    /// </summary>
    /// <param name="name">The configuration key, for example <c>DEEPSEEK_API_KEY</c>.</param>
    /// <returns>The value, or <see langword="null"/> when it is not configured.</returns>
    string? Find(string name);

    /// <summary>
    /// Finds a credential or explains which key is missing.
    /// </summary>
    /// <param name="name">The configuration key.</param>
    /// <returns>The value.</returns>
    /// <exception cref="Errors.ModelCredentialException">The key is not configured.</exception>
    string GetRequired(string name);
}
