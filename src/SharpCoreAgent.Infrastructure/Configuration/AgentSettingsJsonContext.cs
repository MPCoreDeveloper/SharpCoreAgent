using System.Text.Json.Serialization;

namespace SharpCoreAgent.Infrastructure.Configuration;

/// <summary>
/// Source-generated metadata for the settings document.
/// </summary>
/// <remarks>
/// The document is a flat object of string values, so there is no model type to describe and nothing
/// for a naming policy to do: a key is the name <c>config set</c> accepted and is written verbatim.
/// Keys are sorted by the caller before they get here, which is what makes two saves of the same
/// settings byte-identical.
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(SortedDictionary<string, string>))]
internal sealed partial class AgentSettingsJsonContext : JsonSerializerContext
{
}
