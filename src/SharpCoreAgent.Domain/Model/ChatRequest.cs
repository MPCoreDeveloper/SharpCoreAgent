using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Domain.Model;

/// <summary>
/// One request to a model: who it is, what it may say, and what it may call.
/// </summary>
/// <param name="Model">The model and its declared capabilities.</param>
/// <param name="Messages">The transcript, system message first.</param>
/// <param name="Tools">The tools to advertise; empty when the caller wants prose only.</param>
/// <param name="Temperature">The sampling temperature.</param>
/// <param name="MaxOutputTokens">The output ceiling for this request, when the caller sets one.</param>
public sealed record ChatRequest(
    ModelDescriptor Model,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ToolDefinition> Tools,
    double Temperature = 0.2,
    int? MaxOutputTokens = null);

/// <summary>
/// One completed model response.
/// </summary>
/// <param name="Message">The assistant message, including any requested tool calls.</param>
/// <param name="FinishReason">Why generation stopped.</param>
/// <param name="Usage">Token counts, or <see cref="TokenUsage.Unknown"/> when the provider omitted them.</param>
public sealed record ChatResponse(ChatMessage Message, FinishReason FinishReason, TokenUsage Usage)
{
    /// <summary>Gets the text of the response. Convenience for the common case.</summary>
    public string Text => Message.Content;
}
