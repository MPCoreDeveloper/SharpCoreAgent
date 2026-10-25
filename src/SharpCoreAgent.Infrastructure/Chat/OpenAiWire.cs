using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpCoreAgent.Infrastructure.Chat;

// The OpenAI-compatible wire shapes. They are internal and deliberately dumb: mapping to and from the
// domain happens in OpenAiCompatibleTransport, so a provider quirk never leaks past this file.
//
// Naming: the JSON names are attributes rather than a policy, because the wire mixes snake_case
// (max_tokens) with single words (tools) and a global policy would have to be contradicted per member
// anyway.

/// <summary>One <c>POST {base}/chat/completions</c> body.</summary>
internal sealed class OpenAiChatRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public IReadOnlyList<OpenAiChatMessage> Messages { get; set; } = [];

    [JsonPropertyName("tools")]
    public IReadOnlyList<OpenAiTool>? Tools { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = true;

    [JsonPropertyName("stream_options")]
    public OpenAiStreamOptions? StreamOptions { get; set; }
}

/// <summary>One transcript entry on the wire.</summary>
internal sealed class OpenAiChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<OpenAiToolCallRequest>? ToolCalls { get; set; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }
}

/// <summary>An assistant message's request to call a tool, as sent back on the next request.</summary>
internal sealed class OpenAiToolCallRequest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public OpenAiFunctionCall Function { get; set; } = new();
}

/// <summary>A function name and its raw argument JSON.</summary>
internal sealed class OpenAiFunctionCall
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = "{}";
}

/// <summary>One advertised tool.</summary>
internal sealed class OpenAiTool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public OpenAiFunctionDefinition Function { get; set; } = new();
}

/// <summary>One advertised tool's declaration.</summary>
internal sealed class OpenAiFunctionDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the JSON Schema, forwarded as-is from <c>ToolDefinition.ParametersSchemaJson</c>.</summary>
    [JsonPropertyName("parameters")]
    public JsonElement Parameters { get; set; }
}

/// <summary>The request to report usage on the final streamed chunk.</summary>
internal sealed class OpenAiStreamOptions
{
    [JsonPropertyName("include_usage")]
    public bool IncludeUsage { get; set; } = true;
}
