using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Application;

/// <summary>
/// Estimates how many tokens a request's transcript and tool definitions will occupy.
/// </summary>
/// <remarks>
/// This is a conservative planning estimate, not a provider bill: four characters per token is the usual
/// approximation for English and source text, and images are counted by their base64 length so a large picture
/// cannot hide from the compaction decision. The loop uses it only to decide whether to compact before a request;
/// actual usage still comes from the provider's response.
/// </remarks>
internal static class PromptTokenEstimator
{
    private const int CharsPerToken = 4;
    private const int MessageOverhead = 16;
    private const int ToolOverhead = 16;

    /// <summary>Estimates the input tokens for one request.</summary>
    /// <param name="messages">The transcript that will be sent, including system messages.</param>
    /// <param name="tools">The tool definitions that will be advertised.</param>
    /// <returns>An estimate bounded to <see cref="int.MaxValue"/>.</returns>
    public static int Estimate(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(tools);

        long characters = 0;
        foreach (var message in messages)
        {
            characters += MessageOverhead + message.Content.Length;
            foreach (var call in message.ToolCalls)
            {
                characters += call.Name.Length + call.ArgumentsJson.Length;
            }

            foreach (var image in message.Images)
            {
                characters += image.Base64.Length;
            }
        }

        foreach (var tool in tools)
        {
            characters += ToolOverhead + tool.Name.Length + tool.Description.Length + tool.ParametersSchemaJson.Length;
        }

        var estimate = (characters / CharsPerToken) + 1;
        return estimate > int.MaxValue ? int.MaxValue : (int)estimate;
    }
}
