using System.Globalization;
using System.Text.Json;
using SharpCoreAgent.Infrastructure.Images;

namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// The answer to one <c>tools/call</c>, in the shape the adapter hands to the model.
/// </summary>
/// <param name="IsError">Whether the server said the call failed.</param>
/// <param name="Text">The flattened content, or the reason the call could not be made.</param>
public sealed record McpCallResult(bool IsError, string Text)
{
    /// <summary>Gets the image parts the server returned, in the order it returned them.</summary>
    /// <remarks>
    /// Every <c>image</c> entry with data, including the ones no provider would accept: the decision about what can be
    /// attached belongs to the tool, which is the only place that knows which model is reading. Empty on every failure
    /// this client reports, because there is nothing to attach to a call that did not happen.
    /// </remarks>
    public IReadOnlyList<McpImage> Images { get; init; } = [];

    /// <summary>Creates a failure that this client, rather than the server, is reporting.</summary>
    /// <param name="message">Why the call could not be made, phrased for the model.</param>
    /// <returns>A result with <see cref="IsError"/> set.</returns>
    public static McpCallResult Failure(string message) => new(true, message);
}

/// <summary>
/// One <c>image</c> part as the server sent it: the type it claimed, and the payload undecoded.
/// </summary>
/// <param name="MimeType">The part's <c>mimeType</c>, or the empty string when it named none.</param>
/// <param name="Base64">The part's <c>data</c>, exactly as the server sent it.</param>
/// <remarks>
/// Not an <see cref="Domain.Conversation.ImageContent"/>, because nothing has decided yet that this payload is going
/// anywhere: a <c>image/bmp</c>, an oversized capture and a part from a session whose model cannot see all stop here.
/// </remarks>
public sealed record McpImage(string MimeType, string Base64);

/// <summary>
/// One flattened answer: the text, and the pictures that arrived beside it.
/// </summary>
/// <param name="Text">The entries joined by a blank line, or a sentence saying the server sent nothing.</param>
/// <param name="Images">The <c>image</c> parts with data, in the order the server returned them.</param>
internal sealed record McpFlattened(string Text, IReadOnlyList<McpImage> Images);

/// <summary>
/// One binary entry a server returned, offered to a sink that can make it durable.
/// </summary>
/// <param name="Kind">The entry's <c>type</c>: <c>image</c> or <c>resource</c>.</param>
/// <param name="MimeType">The entry's MIME type, which is what decides whether it can be saved at all.</param>
/// <param name="Base64">The undecoded payload, exactly as the server sent it.</param>
/// <param name="Ordinal">The entry's position among the saveable artefacts of this call, counting from one.</param>
/// <param name="Description">The sentence this entry would have been flattened to without a sink.</param>
/// <remarks>
/// The description travels with the artefact so a refusal can keep it verbatim and still be one sentence:
/// the sink replaces, extends or returns it, and <see cref="McpContent"/> never has to guess the wording.
/// </remarks>
internal sealed record McpArtefact(
    string Kind,
    string MimeType,
    string Base64,
    int Ordinal,
    string Description);

/// <summary>
/// Turns a server's <c>content[]</c> into text a model can read.
/// </summary>
/// <remarks>
/// Every entry is described rather than dropped: a screenshot the model cannot see is still a fact worth
/// knowing about, and a base64 blob pasted into the transcript is neither readable nor affordable. The
/// descriptions name the type, the size and the location, which is what a developer needs to fetch the
/// artefact by another route.
/// <para>
/// A caller that can make binaries durable passes a <c>save</c> delegate: an image entry then becomes
/// whatever the sink says it became — a path and a size, or the description with the reason it was not
/// saved. Nothing is ever dropped either way.
/// </para>
/// </remarks>
internal static class McpContent
{
    /// <summary>
    /// Flattens the <c>content</c> member of a <c>tools/call</c> result.
    /// </summary>
    /// <param name="result">The whole <c>result</c> object.</param>
    /// <param name="serverName">The server's prefix, used in the empty-case sentence.</param>
    /// <returns>The entries joined by a blank line, or a sentence saying the server sent nothing.</returns>
    public static string Flatten(JsonElement result, string serverName) => Flatten(result, serverName, save: null);

    /// <summary>
    /// Flattens the <c>content</c> member, letting a sink make the binary entries durable.
    /// </summary>
    /// <param name="result">The whole <c>result</c> object.</param>
    /// <param name="serverName">The server's prefix, used in the empty-case sentence.</param>
    /// <param name="save">
    /// Turns a binary entry into the sentence that replaces its description, or <see langword="null"/> to
    /// describe binaries exactly as <see cref="Flatten(JsonElement, string)"/> does.
    /// </param>
    /// <returns>The entries joined by a blank line, or a sentence saying the server sent nothing.</returns>
    public static string Flatten(JsonElement result, string serverName, Func<McpArtefact, string>? save) =>
        FlattenWithImages(result, serverName, save).Text;

    /// <summary>
    /// Flattens the <c>content</c> member, and keeps the picture parts beside the text that describes them.
    /// </summary>
    /// <param name="result">The whole <c>result</c> object.</param>
    /// <param name="serverName">The server's prefix, used in the empty-case sentence.</param>
    /// <param name="save">
    /// Turns a binary entry into the sentence that replaces its description, or <see langword="null"/> to describe
    /// binaries the way <see cref="Flatten(JsonElement, string)"/> does.
    /// </param>
    /// <returns>The text, and every <c>image</c> part that carried data.</returns>
    /// <remarks>
    /// The text is identical to what <see cref="Flatten(JsonElement, string, Func{McpArtefact, string})"/> returns:
    /// collecting a part changes nothing about the sentence the sink writes or the description a text-only model reads.
    /// Whether the payload can actually be attached is decided by the caller, because only the caller knows which model
    /// is reading and what a result may spend.
    /// </remarks>
    public static McpFlattened FlattenWithImages(
        JsonElement result,
        string serverName,
        Func<McpArtefact, string>? save)
    {
        var images = new List<McpImage>();

        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return new McpFlattened(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"(the {serverName} server answered without content)"),
                images);
        }

        var parts = new List<string>();
        var ordinal = 0;
        foreach (var entry in content.EnumerateArray())
        {
            var part = Describe(entry, save, images, ref ordinal);
            if (!string.IsNullOrWhiteSpace(part))
            {
                parts.Add(part);
            }
        }

        return new McpFlattened(
            parts.Count == 0
                ? string.Create(CultureInfo.InvariantCulture, $"(the {serverName} server returned no content)")
                : string.Join("\n\n", parts),
            images);
    }

    private static string Describe(JsonElement entry, Func<McpArtefact, string>? save, List<McpImage> images, ref int ordinal)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return string.Create(CultureInfo.InvariantCulture, $"[content that is not an object: {entry.ValueKind}]");
        }

        var type = String(entry, "type");

        return type switch
        {
            "text" => String(entry, "text") ?? "[empty text]",
            "image" => Saveable(type, entry, save, images, ref ordinal),
            "audio" => Binary(type, entry),
            "resource" => Resource(entry, save, ref ordinal),
            "resource_link" => Link(entry),
            null => "[an entry with no type]",
            _ => Unknown(type, entry),
        };
    }

    /// <summary>
    /// Describes a <c>data</c>-carrying entry, offering an image to the sink when there is one.
    /// </summary>
    /// <param name="type">The entry type, which is the first word of the description.</param>
    /// <param name="entry">The entry.</param>
    /// <param name="save">The sink, or null when nothing can be written.</param>
    /// <param name="images">The collection the image parts are added to, in the order the server returned them.</param>
    /// <param name="ordinal">Counts the saveable entries of the call.</param>
    /// <returns>The sink's sentence, or the description unchanged.</returns>
    /// <remarks>
    /// Only an <c>image/*</c> entry with data is offered: a screenshot is the reason this seam exists, and
    /// audio is described because saving it would be a claim the client cannot keep. The ordinal advances
    /// for every offered entry, saved or not, so the same answer names the same files on every call.
    /// </remarks>
    private static string Saveable(
        string type,
        JsonElement entry,
        Func<McpArtefact, string>? save,
        List<McpImage> images,
        ref int ordinal)
    {
        // Collected before any decision about it: every image part with data is offered to the caller, which is the
        // only place that knows whether a model can read it and whether the result has room for another picture.
        if (String(entry, "mimeType") is { } mime && ImagePayload.IsImage(mime) && String(entry, "data") is { } payload)
        {
            images.Add(new McpImage(mime, payload));
        }

        if (save is null || !IsSaveable(String(entry, "mimeType")) || String(entry, "data") is not { } data)
        {
            return Binary(type, entry);
        }

        ordinal++;
        return save(new McpArtefact(type, String(entry, "mimeType")!, data, ordinal, Binary(type, entry)));
    }

    private static string Binary(string type, JsonElement entry)
    {
        var mime = String(entry, "mimeType") ?? "unknown type";
        var size = String(entry, "data") is { } data ? DataSize(data) : "no data";
        return string.Create(CultureInfo.InvariantCulture, $"[{type}: {mime} · {size}]");
    }

    private static string Resource(JsonElement entry, Func<McpArtefact, string>? save, ref int ordinal)
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

        if (String(resource, "blob") is not { } blob)
        {
            return string.Create(CultureInfo.InvariantCulture, $"[resource: {uri} · {mime} · no content]");
        }

        var description = string.Create(CultureInfo.InvariantCulture, $"[resource: {uri} · {mime} · {DataSize(blob)}]");
        if (save is null || !IsSaveable(String(resource, "mimeType")))
        {
            return description;
        }

        ordinal++;
        return save(new McpArtefact("resource", String(resource, "mimeType")!, blob, ordinal, description));
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

    private static bool IsImage(string? mimeType) => ImagePayload.IsImage(mimeType);

    private static bool IsSaveable(string? mimeType) =>
        IsImage(mimeType)
        || string.Equals(mimeType, "application/pdf", StringComparison.OrdinalIgnoreCase)
        || (mimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ?? false)
        || (mimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ?? false);

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

