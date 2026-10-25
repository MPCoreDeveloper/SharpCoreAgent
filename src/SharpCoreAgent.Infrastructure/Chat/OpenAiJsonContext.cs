using System.Text.Json.Serialization;

namespace SharpCoreAgent.Infrastructure.Chat;

/// <summary>
/// Source-generated metadata for the wire types.
/// </summary>
/// <remarks>
/// Reflection-based serialization is avoided on purpose: it is the first thing a trimmed or
/// ahead-of-time-compiled build loses, and the DTO set is small enough that the generator costs
/// nothing in source size.
/// </remarks>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(OpenAiChatRequest))]
[JsonSerializable(typeof(OpenAiStreamChunk))]
[JsonSerializable(typeof(OpenAiErrorEnvelope))]
internal sealed partial class OpenAiJsonContext : JsonSerializerContext
{
}
