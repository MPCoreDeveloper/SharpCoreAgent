namespace SharpCoreAgent.Domain.Conversation;

/// <summary>
/// Who produced a message in the transcript.
/// </summary>
public enum ChatRole
{
    /// <summary>The system prompt. Always the first message of a request.</summary>
    System,

    /// <summary>A developer or user instruction.</summary>
    User,

    /// <summary>A model response, possibly carrying tool calls.</summary>
    Assistant,

    /// <summary>The serialized result of one tool invocation.</summary>
    Tool,
}
