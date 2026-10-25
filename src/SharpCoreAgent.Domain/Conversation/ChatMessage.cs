namespace SharpCoreAgent.Domain.Conversation;

/// <summary>
/// One entry in the conversation transcript.
/// </summary>
/// <remarks>
/// The same record shape carries all four roles so a transcript can be appended to, trimmed and
/// persisted without a type switch at every step:
/// <list type="bullet">
/// <item><description>system and user messages use <see cref="Content"/> only;</description></item>
/// <item><description>an assistant message may carry <see cref="ToolCalls"/> next to its text;</description></item>
/// <item><description>a tool message echoes <see cref="ToolCallId"/> and <see cref="ToolName"/>.</description></item>
/// </list>
/// </remarks>
public sealed record ChatMessage
{
    /// <summary>Gets who produced the message.</summary>
    public required ChatRole Role { get; init; }

    /// <summary>Gets the message body. Empty, never null.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Gets the tool calls an assistant message requested.</summary>
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];

    /// <summary>Gets the identifier of the tool call a tool message answers.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Gets the tool name a tool message answers.</summary>
    public string? ToolName { get; init; }

    /// <summary>Gets a value indicating whether a tool message reports a failure or refusal.</summary>
    public bool IsError { get; init; }

    /// <summary>Gets a value indicating whether the assistant message requested at least one tool call.</summary>
    public bool HasToolCalls => ToolCalls.Count > 0;

    /// <summary>Creates a system message.</summary>
    /// <param name="content">The prompt text.</param>
    /// <returns>A message with <see cref="ChatRole.System"/>.</returns>
    public static ChatMessage System(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new ChatMessage { Role = ChatRole.System, Content = content };
    }

    /// <summary>Creates a user message.</summary>
    /// <param name="content">The instruction text.</param>
    /// <returns>A message with <see cref="ChatRole.User"/>.</returns>
    public static ChatMessage User(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new ChatMessage { Role = ChatRole.User, Content = content };
    }

    /// <summary>Creates an assistant message.</summary>
    /// <param name="content">The response text; may be empty when the model only requested tools.</param>
    /// <param name="toolCalls">The requested tool calls, or <see langword="null"/> for a text-only answer.</param>
    /// <returns>A message with <see cref="ChatRole.Assistant"/>.</returns>
    public static ChatMessage Assistant(string content, IReadOnlyList<ToolCall>? toolCalls = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = content,
            ToolCalls = toolCalls ?? [],
        };
    }

    /// <summary>Creates the transcript entry that answers a tool call.</summary>
    /// <param name="result">The tool outcome to feed back to the model.</param>
    /// <returns>A message with <see cref="ChatRole.Tool"/>.</returns>
    public static ChatMessage FromToolResult(Tools.ToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new ChatMessage
        {
            Role = ChatRole.Tool,
            Content = result.Content,
            ToolCallId = result.CallId,
            ToolName = result.ToolName,
            IsError = result.IsError || result.IsRefused,
        };
    }
}
