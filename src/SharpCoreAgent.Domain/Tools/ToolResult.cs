namespace SharpCoreAgent.Domain.Tools;

/// <summary>
/// The kind of change a tool reports, so a turn can say what it touched without re-reading the disk.
/// </summary>
public enum ToolEffectKind
{
    /// <summary>The tool read workspace content.</summary>
    Read,

    /// <summary>The tool created a file or directory.</summary>
    Created,

    /// <summary>The tool changed an existing file.</summary>
    Modified,

    /// <summary>The tool removed a file or directory.</summary>
    Deleted,

    /// <summary>The tool started an external process.</summary>
    CommandRun,
}

/// <summary>
/// One observable consequence of a tool invocation.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Target">The workspace-relative path, or the command line for <see cref="ToolEffectKind.CommandRun"/>.</param>
public sealed record ToolEffect(ToolEffectKind Kind, string Target)
{
    /// <inheritdoc />
    public override string ToString() => $"{Kind}: {Target}";
}

/// <summary>
/// The outcome of one tool invocation, in the shape the model will read.
/// </summary>
/// <remarks>
/// Failure is data, not an exception: a bad argument, a missing file, a denied approval and a crashed
/// process all come back as a <see cref="ToolResult"/> with <see cref="IsError"/> set, so the model can
/// see what went wrong and try something else. Only cancellation escapes as an exception.
/// </remarks>
public sealed record ToolResult
{
    /// <summary>Gets the identifier of the <see cref="Conversation.ToolCall"/> this answers.</summary>
    public required string CallId { get; init; }

    /// <summary>Gets the tool name, echoed back for providers that key on it.</summary>
    public required string ToolName { get; init; }

    /// <summary>Gets the text handed to the model. Empty only when the tool genuinely produced nothing.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the invocation failed.</summary>
    public bool IsError { get; init; }

    /// <summary>Gets a value indicating whether the developer refused the invocation.</summary>
    public bool IsRefused { get; init; }

    /// <summary>Gets a value indicating whether <see cref="Content"/> was shortened to respect the budget.</summary>
    public bool IsTruncated { get; init; }

    /// <summary>Gets the changes the invocation reported.</summary>
    public IReadOnlyList<ToolEffect> Effects { get; init; } = [];

    /// <summary>Creates a successful result.</summary>
    /// <param name="callId">The tool call identifier.</param>
    /// <param name="toolName">The tool name.</param>
    /// <param name="content">The text handed to the model.</param>
    /// <param name="effects">The reported changes, if any.</param>
    /// <returns>A result with <see cref="IsError"/> and <see cref="IsRefused"/> false.</returns>
    public static ToolResult Success(string callId, string toolName, string content, params ToolEffect[] effects) => new()
    {
        CallId = callId,
        ToolName = toolName,
        Content = content,
        Effects = effects ?? [],
    };

    /// <summary>Creates a failed result.</summary>
    /// <param name="callId">The tool call identifier.</param>
    /// <param name="toolName">The tool name.</param>
    /// <param name="message">Why the invocation failed, phrased for the model.</param>
    /// <returns>A result with <see cref="IsError"/> set.</returns>
    public static ToolResult Failure(string callId, string toolName, string message) => new()
    {
        CallId = callId,
        ToolName = toolName,
        Content = message,
        IsError = true,
    };

    /// <summary>Creates the result of a developer refusing the invocation.</summary>
    /// <param name="callId">The tool call identifier.</param>
    /// <param name="toolName">The tool name.</param>
    /// <param name="reason">Why the invocation was refused, phrased for the model.</param>
    /// <returns>A result with <see cref="IsRefused"/> and <see cref="IsError"/> set.</returns>
    public static ToolResult Refused(string callId, string toolName, string reason) => new()
    {
        CallId = callId,
        ToolName = toolName,
        Content = reason,
        IsError = true,
        IsRefused = true,
    };
}
