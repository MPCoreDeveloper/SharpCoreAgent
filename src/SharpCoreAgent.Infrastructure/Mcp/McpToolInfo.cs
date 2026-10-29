using System.Text.Json;

namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// One entry of a server's <c>tools/list</c>, as much of it as this client uses.
/// </summary>
/// <param name="Name">The name the server gave the tool.</param>
/// <param name="Description">What the server says the tool does.</param>
/// <param name="SchemaJson">The tool's JSON Schema, verbatim.</param>
/// <param name="ReadOnly">Whether the server's <c>readOnlyHint</c> said it changes nothing.</param>
public sealed record McpToolInfo(string Name, string? Description, string SchemaJson, bool ReadOnly)
{
    /// <summary>Gets the schema used when a server sent none: an object with no required member.</summary>
    public const string FallbackSchema = """{"type":"object","properties":{}}""";

    /// <summary>Gets the longest description this client keeps.</summary>
    /// <remarks>
    /// A description is prose the model reads on every request, and a server that pastes a README into it
    /// would spend the context of every turn in every session. The cut keeps the first paragraph.
    /// </remarks>
    private const int MaxDescriptionChars = 1_024;

    /// <summary>
    /// Reads one entry, or reports why it cannot be used.
    /// </summary>
    /// <param name="element">The entry from <c>tools/list</c>.</param>
    /// <param name="info">The parsed tool, when the entry names one.</param>
    /// <returns><see langword="true"/> when the entry has a usable name.</returns>
    public static bool TryRead(JsonElement element, out McpToolInfo info)
    {
        info = default!;

        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("name", out var name) ||
            name.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(name.GetString()))
        {
            return false;
        }

        info = new McpToolInfo(
            name.GetString()!,
            ReadDescription(element),
            ReadSchema(element),
            ReadOnlyHint(element));

        return true;
    }

    private static string? ReadDescription(JsonElement element)
    {
        if (!element.TryGetProperty("description", out var description) ||
            description.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = description.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        return text.Length <= MaxDescriptionChars ? text : text[..MaxDescriptionChars] + "…";
    }

    private static string ReadSchema(JsonElement element)
    {
        // Verbatim, because the schema is the server's contract with the model: re-serialising it would
        // drop annotations this client does not understand, and they are exactly what constrains generation.
        if (element.TryGetProperty("inputSchema", out var schema) && schema.ValueKind == JsonValueKind.Object)
        {
            return schema.GetRawText();
        }

        return FallbackSchema;
    }

    private static bool ReadOnlyHint(JsonElement element)
    {
        // Only an explicit `true` is read-only. A tool that says nothing is treated as a write, because the
        // cost of a needless prompt is one keystroke and the cost of a silent write is the file.
        return element.TryGetProperty("annotations", out var annotations) &&
               annotations.ValueKind == JsonValueKind.Object &&
               annotations.TryGetProperty("readOnlyHint", out var hint) &&
               hint.ValueKind == JsonValueKind.True;
    }
}
