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
    [JsonConverter(typeof(OpenAiContentConverter))]
    public OpenAiContent Content { get; set; } = OpenAiContent.Empty;

    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<OpenAiToolCallRequest>? ToolCalls { get; set; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }
}

/// <summary>
/// A message's <c>content</c>, which the protocol types as either a string or an array of parts.
/// </summary>
/// <remarks>
/// The string form is what every provider accepted first and what a text-only transcript still sends, so it
/// stays the form for a message with no image: an array of one text part is legal and is not what a
/// text-only server is guaranteed to parse. The implicit conversion keeps the text path unchanged in
/// <see cref="OpenAiCompatibleTransport"/>.
/// </remarks>
internal sealed class OpenAiContent
{
    /// <summary>Gets a content value with no text and no images.</summary>
    public static OpenAiContent Empty { get; } = new();

    /// <summary>Gets the message text.</summary>
    public string? Text { get; init; }

    /// <summary>Gets the images to send as <c>image_url</c> parts, as <c>data:</c> URIs.</summary>
    public IReadOnlyList<string> ImageDataUris { get; init; } = [];

    /// <summary>Converts text into a text-only content value.</summary>
    /// <param name="text">The message text.</param>
    public static implicit operator OpenAiContent(string text) => new() { Text = text };
}

/// <summary>Writes <see cref="OpenAiContent"/> as a string, or as an array of parts when it carries images.</summary>
internal sealed class OpenAiContentConverter : JsonConverter<OpenAiContent>
{
    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: this client only writes requests in this shape.</exception>
    public override OpenAiContent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("A chat-completions message is written, never read, by this client.");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, OpenAiContent value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        if (value.ImageDataUris.Count == 0)
        {
            writer.WriteStringValue(value.Text ?? string.Empty);
            return;
        }

        writer.WriteStartArray();

        if (!string.IsNullOrEmpty(value.Text))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", value.Text);
            writer.WriteEndObject();
        }

        foreach (var uri in value.ImageDataUris)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "image_url");
            writer.WriteStartObject("image_url");
            writer.WriteString("url", uri);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
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
