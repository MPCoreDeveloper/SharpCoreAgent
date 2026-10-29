using System.Globalization;
using System.Text.Json;

namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// The answer to one <c>tools/call</c>, in the shape the adapter hands to the model.
/// </summary>
/// <param name="IsError">Whether the server said the call failed.</param>
/// <param name="Text">The flattened content, or the reason the call could not be made.</param>
public sealed record McpCallResult(bool IsError, string Text)
{
    /// <summary>Creates a failure that this client, rather than the server, is reporting.</summary>
    /// <param name="message">Why the call could not be made, phrased for the model.</param>
    /// <returns>A result with <see cref="IsError"/> set.</returns>
    public static McpCallResult Failure(string message) => new(true, message);
}

/// <summary>
/// Turns a server's <c>content[]</c> into text a model can read.
/// </summary>
/// <remarks>
/// Every entry is described rather than dropped: a screenshot the model cannot see is still a fact worth
/// knowing about, and a base64 blob pasted into the transcript is neither readable nor affordable. The
/// descriptions name the type, the size and the location, which is what a developer needs to fetch the
/// artefact by another route.
/// </remarks>
internal static class McpContent
{
    /// <summary>
    /// Flattens the <c>content</c> member of a <c>tools/call</c> result.
    /// </summary>
    /// <param name="result">The whole <c>result</c> object.</param>
    /// <param name="serverName">The server's prefix, used in the empty-case sentence.</param>
    /// <returns>The entries joined by a blank line, or a sentence saying the server sent nothing.</returns>
    public static string Flatten(JsonElement result, string serverName)
    {
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"(the {serverName} server answered without content)");
        }

        var parts = new List<string>();
        foreach (var entry in content.EnumerateArray())
        {
            var part = Describe(entry);
            if (!string.IsNullOrWhiteSpace(part))
            {
                parts.Add(part);
            }
        }

        return parts.Count == 0
            ? string.Create(CultureInfo.InvariantCulture, $"(the {serverName} server returned no content)")
            : string.Join("\n\n", parts);
    }

    private static string Describe(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return string.Create(CultureInfo.InvariantCulture, $"[content that is not an object: {entry.ValueKind}]");
        }

        var type = String(entry, "type");

        return type switch
        {
            "text" => String(entry, "text") ?? "[empty text]",
            "image" or "audio" => Binary(type, entry),
            "resource" => Resource(entry),
            "resource_link" => Link(entry),
            null => "[an entry with no type]",
            _ => Unknown(type, entry),
        };
    }

    private static string Binary(string type, JsonElement entry)
    {
        var mime = String(entry, "mimeType") ?? "unknown type";
        var size = String(entry, "data") is { } data ? DataSize(data) : "no data";
        return string.Create(CultureInfo.InvariantCulture, $"[{type}: {mime} · {size}]");
    }

    private static string Resource(JsonElement entry)
    {
        if (!entry.TryGetProperty("resource", out var resource) || resource.ValueKind != JsonValueKind.Object)
        {
            return "[resource that names no resource]";
        }

        var uri = String(resource, "uri") ?? "no uri";
        var mime = String(resource, "mimeType") ?? "unknown type";

        if (String(resource, "text") is { } text)
        {
            return string.Create(CultureInfo.InvariantCulture, $"[resource: {uri}]\n{text}");
        }

        return String(resource, "blob") is { } blob
            ? string.Create(CultureInfo.InvariantCulture, $"[resource: {uri} · {mime} · {DataSize(blob)}]")
            : string.Create(CultureInfo.InvariantCulture, $"[resource: {uri} · {mime} · no content]");
    }

    private static string Link(JsonElement entry)
    {
        var uri = String(entry, "uri") ?? "no uri";
        var name = String(entry, "name") ?? "unnamed";
        return string.Create(CultureInfo.InvariantCulture, $"[link: {name} · {uri}]");
    }

    private static string Unknown(string type, JsonElement entry)
    {
        // A type this release did not know about: name it, and hand over whatever text it carried, so a
        // newer content type degrades to something readable instead of disappearing.
        return String(entry, "text") is { } text
            ? string.Create(CultureInfo.InvariantCulture, $"[{type}]\n{text}")
            : string.Create(CultureInfo.InvariantCulture, $"[{type}]");
    }

    private static string DataSize(string base64) =>
        string.Create(CultureInfo.InvariantCulture, $"{base64.Length} base64 characters");

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

