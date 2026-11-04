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
    public static IReadOnlyList<string> Extensions { get; } = [".png", ".jpg", ".jpeg", ".gif", ".webp"];

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

        var mediaType = Path.GetExtension(fullPath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => null,
        };
        if (mediaType is null)
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"'{workspace.ToRelative(fullPath)}' is not one of the picture types a provider accepts ({string.Join(", ", Extensions)})");
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

        return ImagePayload.TryCreate(
            mediaType,
            Convert.ToBase64String(bytes),
            TextLimits.MaxImageBytes,
            out image,
            out error);
    }
}
