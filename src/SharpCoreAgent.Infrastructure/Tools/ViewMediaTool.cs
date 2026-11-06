using System.Globalization;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Images;
using SharpCoreAgent.Infrastructure.Media;
using SharpCoreAgent.Infrastructure.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Reads an audio/video file, and shows a video frame as a picture when the model can see one.
/// </summary>
/// <remarks>
/// The media runtime is an explicit FFmpeg dependency, not a shell process: FFMediaToolkit decodes through the LGPL
/// FFmpeg libraries shipped beside this assembly. Audio has metadata only; a video frame is offered the same
/// capability, duplicate and per-turn budget checks as every other image door. macOS has no runtime package here and
/// receives a sentence saying so rather than a mislabelled file.
/// </remarks>
public sealed class ViewMediaTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "view_media";

    private const string Schema =
        """{"type":"object","properties":{"path":{"type":"string","description":"Audio or video path relative to the workspace root."},"at":{"type":"integer","minimum":0,"maximum":86400,"description":"Video timestamp in whole seconds; default 0."},"max_dimension":{"type":"integer","minimum":64,"maximum":4096,"description":"Downscale the extracted frame to this largest width or height; omit to keep the frame's size."}},"required":["path"],"additionalProperties":false}""";

    private const string Description =
        "Read metadata from an audio or video file, and for a video show one decoded frame as an image. Use it for an .mp4, .mov, .webm, .mkv, .avi, .mp3, .wav, .m4a, .ogg, .flac or .aac file. Audio returns duration, codecs and channels but no picture. A video frame is attached to the next message only when the current model can see images; otherwise the metadata is returned and the frame is deliberately not attached. Pass at to choose a whole-second timestamp, and max_dimension when the frame is too large to attach.";

    private const long MaxMediaBytes = 1024L * 1024 * 1024;

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(Name, Description, Schema);

    /// <inheritdoc />
    public ToolSafety Safety => ToolSafety.ReadOnly;
 
    /// <inheritdoc />
    public async ValueTask<ToolResult> ExecuteAsync(
        ToolCall toolCall,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(context);

        using var arguments = ToolArgumentReader.Parse(Name, toolCall.ArgumentsJson);
        var path = arguments.GetRequiredString("path");
        var at = arguments.GetOptionalInt("at", fallback: 0, minimum: 0, maximum: 86_400);
        var maxDimension = arguments.GetOptionalInt(
            "max_dimension",
            fallback: 0,
            minimum: ImageResize.MinimumDimension,
            maximum: ImageResize.MaximumDimension);

        if (!PathSafety.TryResolveRead(context.Workspace, path, out var fullPath, out var owner, out var error))
        {
            return ToolResult.Refused(toolCall.Id, Name, error!);
        }

        var relative = (owner ?? context.Workspace).ToRelative(fullPath);
        if (Directory.Exists(fullPath))
        {
            return ToolResult.Failure(toolCall.Id, Name, $"'{relative}' is a directory, not a media file");
        }

        if (!File.Exists(fullPath))
        {
            return ToolResult.Failure(toolCall.Id, Name, $"no file at '{relative}'");
        }

        if (!IsMediaExtension(fullPath))
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                $"'{relative}' is not audio/video this tool reads. It accepts {string.Join(", ", MediaExtensions)}; use view_image for a still picture.");
        }

        var length = new FileInfo(fullPath).Length;
        if (length == 0)
        {
            return ToolResult.Failure(toolCall.Id, Name, $"'{relative}' is empty (0 bytes)");
        }

        if (length > MaxMediaBytes)
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{relative}' is {length} bytes; view_media reads at most {MaxMediaBytes} bytes"));
        }

        if (!MediaInspector.TryDescribe(fullPath, out var metadata, out var hasVideo, out _, out var describeError))
        {
            return ToolResult.Failure(toolCall.Id, Name, $"'{relative}' could not be read: {describeError}");
        }

        if (!hasVideo || context.Model is not { SupportsImages: true })
        {
            var note = hasVideo
                ? $"\nThe video frame was not attached because {context.Model?.QualifiedName ?? "the session's model"} is declared text-only."
                : "\nThis audio file has no video frame to attach.";
            return ToolResult.Success(toolCall.Id, Name, $"'{relative}' ({metadata}){note}")
                with { Effects = [new ToolEffect(ToolEffectKind.Read, relative)] };
        }

        if (!MediaInspector.TryFrame(
                fullPath,
                at,
                maxDimension,
                TextLimits.MaxImageBytes,
                out var png,
                out var width,
                out var height,
                out var frameError))
        {
            return ToolResult.Failure(toolCall.Id, Name, $"'{relative}' frame at {at}s could not be decoded: {frameError}");
        }

        var image = new ImageContent
        {
            MediaType = "image/png",
            Base64 = Convert.ToBase64String(png),
        };

        var description = string.Create(
            CultureInfo.InvariantCulture,
            $"video frame at {at}s · {width} × {height} PNG · {png.Length} bytes");

        if (ImageShown.Already(context.ImagesAlreadyShown, image))
        {
            return ToolResult.Success(
                toolCall.Id,
                Name,
                $"'{relative}' ({description}) is not attached again: {ImageShown.Clause}.")
                with { Effects = [new ToolEffect(ToolEffectKind.Read, relative)] };
        }

        if (context.ImageBytesRemaining is { } left && !ImageBudget.Fits(png.Length, left))
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                $"'{relative}' ({description}) was not attached: {ImageBudget.Spent(png.Length, left)}.");
        }

        return ToolResult.Image(
            toolCall.Id,
            Name,
            $"'{relative}' ({metadata}\n{description}) is attached to the next message.",
            image) with { Effects = [new ToolEffect(ToolEffectKind.Read, relative)] };
    }

 
    private static readonly string[] MediaExtensions =
    [
        ".mp4", ".m4v", ".mov", ".webm", ".mkv", ".avi",
        ".mp3", ".wav", ".m4a", ".ogg", ".flac", ".aac",
    ];

    private static bool IsMediaExtension(string path) =>
        MediaExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}

