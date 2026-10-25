using System.Text.Json.Serialization;

namespace SharpCoreAgent.Infrastructure.Chat;

// The streaming half of the wire: one SSE frame is one OpenAiStreamChunk, and tool calls arrive as
// fragments that must be joined by index before the model may see them.

/// <summary>One <c>data:</c> frame of a streamed completion.</summary>
internal sealed class OpenAiStreamChunk
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("choices")]
    public IReadOnlyList<OpenAiStreamChoice>? Choices { get; set; }

    [JsonPropertyName("usage")]
    public OpenAiUsage? Usage { get; set; }

    [JsonPropertyName("error")]
    public OpenAiError? Error { get; set; }
}

/// <summary>One candidate's increment.</summary>
internal sealed class OpenAiStreamChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("delta")]
    public OpenAiDelta? Delta { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

/// <summary>The pieces of one increment: text, reasoning, tool-call fragments.</summary>
internal sealed class OpenAiDelta
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    /// <summary>Gets or sets DeepSeek's separate reasoning channel.</summary>
    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; set; }

    /// <summary>Gets or sets the reasoning channel some other compatible servers use.</summary>
    [JsonPropertyName("reasoning")]
    public string? Reasoning { get; set; }

    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<OpenAiToolCallDelta>? ToolCalls { get; set; }
}

/// <summary>A fragment of one tool call; fragments with the same index belong together.</summary>
internal sealed class OpenAiToolCallDelta
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("function")]
    public OpenAiFunctionCall? Function { get; set; }
}

/// <summary>Token counts, present only when the server decided to report them.</summary>
internal sealed class OpenAiUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int? PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int? CompletionTokens { get; set; }
}

/// <summary>The body of a failed request.</summary>
internal sealed class OpenAiErrorEnvelope
{
    [JsonPropertyName("error")]
    public OpenAiError? Error { get; set; }
}

/// <summary>The provider's explanation of a failure.</summary>
internal sealed class OpenAiError
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }
}
