using System.Globalization;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Infrastructure.Tools;

namespace SharpCoreAgent.Infrastructure.Images;

/// <summary>
/// Loads a picture named by a command line (or a host) through the same media-type, size and workspace rules the
/// tools use.
/// </summary>
/// <remarks>
/// The extension names the media type; the bytes are not sniffed, because a provider is told what it is receiving and a
/// mislabelled payload must fail before the request. A path is resolved through <see cref="PathSafety.TryResolveRead"/>,
/// so an attached read-only root is allowed and a path outside every root is refused.
/// </remarks>
public static class ImageFile
{
    /// <summary>Gets the file extensions this helper accepts.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".pdf"];

    /// <summary>Loads one image file.</summary>
    /// <param name="workspace">The workspace and its read-only attachments.</param>
    /// <param name="path">The path as the developer wrote it.</param>
    /// <param name="image">The attachable image, on success.</param>
    /// <param name="error">One sentence explaining a refusal, on failure.</param>
    /// <returns><see langword="true"/> when the file may be attached.</returns>
    public static bool TryLoad(
        Workspace workspace,
        string path,
        out ImageContent image,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        image = null!;
        if (!PathSafety.TryResolveRead(workspace, path, out var fullPath, out _, out var resolutionError))
        {
            error = resolutionError ?? $"'{path}' could not be resolved in this workspace";
            return false;
        }

        var mediaType = MediaTypeFor(fullPath);
        var isSvg = IsSvg(fullPath);
        var isPdf = IsPdf(fullPath);
        if (mediaType is null && !isSvg && !isPdf)
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"'{workspace.ToRelative(fullPath)}' is not one of the picture types a provider accepts ({string.Join(", ", Extensions)}); SVG and PDF are rendered to PNG before they are attached");
            return false;
        }

        long length;
        try
        {
            length = new FileInfo(fullPath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"'{workspace.ToRelative(fullPath)}' could not be read: {ex.Message}";
            return false;
        }

        if (length > TextLimits.MaxImageBytes)
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"'{workspace.ToRelative(fullPath)}' is {length} bytes; an attached image is at most {TextLimits.MaxImageBytes} bytes (4 MiB)");
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"'{workspace.ToRelative(fullPath)}' could not be read: {ex.Message}";
            return false;
        }

        if (isSvg)
        {
            if (!SvgImage.TryRender(bytes, TextLimits.MaxImageBytes, out var png, out _, out _, out error))
            {
                image = null!;
                return false;
            }

            return ImagePayload.TryCreate(
                "image/png",
                Convert.ToBase64String(png),
                TextLimits.MaxImageBytes,
                out image,
                out error);
        }

        if (isPdf)
        {
            if (!PdfImage.TryRender(
                    bytes,
                    1,
                    TextLimits.MaxImageBytes,
                    SvgImage.MaxDimension,
                    out var png,
                    out _,
                    out _,
                    out error))
            {
                image = null!;
                return false;
            }

            return ImagePayload.TryCreate(
                "image/png",
                Convert.ToBase64String(png),
                TextLimits.MaxImageBytes,
                out image,
                out error);
        }

        return ImagePayload.TryCreate(
            mediaType,
            Convert.ToBase64String(bytes),
            TextLimits.MaxImageBytes,
            out image,
            out error);
    }

    /// <summary>Loads one image from a stream, for <c>--image -</c>.</summary>
    /// <param name="input">The stream, normally standard input.</param>
    /// <param name="image">The attachable image, on success.</param>
    /// <param name="error">One sentence explaining a refusal, on failure.</param>
    /// <returns><see langword="true"/> when the bytes are an accepted picture type.</returns>
    /// <remarks>
    /// There is no path to read an extension from, so the four signatures identify the type: PNG, JPEG, GIF and WebP.
    /// The read is bounded to one byte over the per-file ceiling, so a pipeline cannot make the process allocate an
    /// unbounded buffer before the refusal is known.
    /// </remarks>
    public static bool TryLoadStdin(Stream input, out ImageContent image, out string error)
    {
        ArgumentNullException.ThrowIfNull(input);

        image = null!;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = input.Read(chunk, 0, chunk.Length);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > TextLimits.MaxImageBytes)
            {
                error = string.Create(
                    CultureInfo.InvariantCulture,
                    $"the image on stdin is over the {TextLimits.MaxImageBytes}-byte (4 MiB) ceiling");
                return false;
            }

            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();
        var mediaType = Sniff(bytes);
        if (mediaType is null)
        {
            error = "the bytes on stdin are not a PNG, JPEG, GIF or WebP image";
            return false;
        }

        return ImagePayload.TryCreate(
            mediaType,
            Convert.ToBase64String(bytes),
            TextLimits.MaxImageBytes,
            out image,
            out error);
    }

    private static string? Sniff(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8
            && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return "image/png";
        }

        if (bytes.Length >= 3 && bytes[..3].SequenceEqual(new byte[] { 0xFF, 0xD8, 0xFF }))
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 6
            && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
        {
            return "image/gif";
        }

        if (bytes.Length >= 12
            && bytes[..4].SequenceEqual("RIFF"u8)
            && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        return null;
    }

    private static bool IsSvg(string fullPath) =>
        string.Equals(Path.GetExtension(fullPath), ".svg", StringComparison.OrdinalIgnoreCase);

    private static bool IsPdf(string fullPath) =>
        string.Equals(Path.GetExtension(fullPath), ".pdf", StringComparison.OrdinalIgnoreCase);

    private static string? MediaTypeFor(string fullPath) =>
        Path.GetExtension(fullPath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => null,
        };
}

