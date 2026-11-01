using System.Globalization;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Infrastructure.Tools;

namespace SharpCoreAgent.Infrastructure.Images;

/// <summary>
/// The single rule for turning a payload somebody else produced into an <see cref="ImageContent"/> a provider accepts.
/// </summary>
/// <remarks>
/// Two doors hand this build a picture — an MCP server's <c>image</c> part and an HTTP response whose type is a picture
/// — and both have to answer the same three questions before the loop can attach anything: is this a type a provider
/// accepts, how big are the bytes, and is that inside the ceiling. Answering them twice would let the two doors drift,
/// which is why they are answered here and nowhere else.
/// <para>
/// The table is closed on purpose. A provider rejects a media type it does not carry, and a payload relabelled to fit
/// would only move the failure into the request body, where the message is a 400 rather than a sentence. Five
/// spellings map onto four types because <c>image/jpg</c> is what servers and sites actually send and
/// <c>image/jpeg</c> is the only spelling the providers document.
/// </para>
/// </remarks>
internal static class ImagePayload
{
    /// <summary>
    /// The media types a provider accepts, by the spelling a payload may arrive with.
    /// </summary>
    /// <remarks>
    /// <c>image/bmp</c> is deliberately absent although the artefact sink saves it: saving is a file for a developer,
    /// attaching is a payload for a provider, and the two tables answer different questions.
    /// </remarks>
    private static readonly Dictionary<string, string> Accepted = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = "image/png",
        ["image/jpeg"] = "image/jpeg",
        ["image/jpg"] = "image/jpeg",
        ["image/gif"] = "image/gif",
        ["image/webp"] = "image/webp",
    };

    /// <summary>Gets the four types an image may be attached as, in the order a refusal names them.</summary>
    public const string AcceptedList = "image/png, image/jpeg, image/gif, image/webp";

    /// <summary>
    /// Determines whether a claimed media type is an image at all.
    /// </summary>
    /// <param name="mimeType">The type a server or a site claimed.</param>
    /// <returns><see langword="true"/> for anything under <c>image/</c>.</returns>
    public static bool IsImage(string? mimeType) =>
        mimeType is not null && mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Normalises a claimed media type to the spelling a provider expects.
    /// </summary>
    /// <param name="mimeType">The type a server or a site claimed, with or without parameters.</param>
    /// <param name="mediaType">The normalised type, or the empty string when the type is not accepted.</param>
    /// <returns><see langword="true"/> when a provider accepts this type.</returns>
    public static bool TryMediaType(string? mimeType, out string mediaType)
    {
        // A parameter after the type ("; charset=binary") is not part of what a provider matches on.
        var bare = mimeType?.Split(';')[0].Trim();
        if (bare is not null && Accepted.TryGetValue(bare, out var normalised))
        {
            mediaType = normalised;
            return true;
        }

        mediaType = string.Empty;
        return false;
    }

    /// <summary>
    /// Computes the decoded size of a base64 string without decoding it.
    /// </summary>
    /// <param name="base64">The payload as it arrived.</param>
    /// <returns>The number of bytes the string decodes to, or an upper bound when it will not decode.</returns>
    /// <remarks>
    /// <c>Length / 4 * 3</c> minus the visible <c>=</c> padding bounds the decoded size from above for well-formed
    /// base64, so an oversized payload is refused before its bytes exist in memory. The padding is subtracted because
    /// it is visible before the decode: without it a payload of exactly the ceiling size would be refused as two bytes
    /// over it. This is the estimate the artefact sink has used since round 4, extracted here rather than copied, so
    /// the two ceilings cannot drift apart.
    /// </remarks>
    public static long EstimateBytes(string base64)
    {
        ArgumentNullException.ThrowIfNull(base64);

        var padding = 0;
        for (var index = base64.Length - 1; index >= 0 && padding < 2 && base64[index] == '='; index--)
        {
            padding++;
        }

        return ((long)base64.Length / 4 * 3) - padding;
    }

    /// <summary>
    /// Builds the attachable image, or explains why the payload cannot be attached.
    /// </summary>
    /// <param name="mimeType">The type a server or a site claimed.</param>
    /// <param name="data">The base64 payload, as it arrived.</param>
    /// <param name="maxBytes">The ceiling the decoded payload must fit inside.</param>
    /// <param name="image">The image, when the payload qualifies.</param>
    /// <param name="reason">Why it does not, phrased as the end of a sentence a model reads.</param>
    /// <returns><see langword="true"/> when the payload may be attached.</returns>
    public static bool TryCreate(
        string? mimeType,
        string? data,
        long maxBytes,
        out ImageContent image,
        out string reason)
    {
        if (!TryMediaType(mimeType, out var mediaType))
        {
            image = null!;
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"{mimeType ?? "an unnamed type"} is not one of the picture types a provider accepts ({AcceptedList})");
            return false;
        }

        if (string.IsNullOrWhiteSpace(data))
        {
            image = null!;
            reason = "the part carried no data";
            return false;
        }

        var bytes = EstimateBytes(data);
        if (bytes > maxBytes)
        {
            image = null!;
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"{CodeIndexFormat.Size(bytes)} decoded is over the {CodeIndexFormat.Size(maxBytes)} ceiling");
            return false;
        }

        image = new ImageContent { MediaType = mediaType, Base64 = data };
        reason = string.Empty;
        return true;
    }
}
