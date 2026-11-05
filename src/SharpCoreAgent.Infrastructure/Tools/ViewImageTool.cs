using System.Globalization;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Images;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Shows the model an image file: a PNG, JPEG, GIF, WebP or SVG from the workspace or a read-only attachment.
/// </summary>
/// <remarks>
/// This is the half of reading a picture that no other tool can do. <c>read_file</c> refuses a binary file because
/// its answer is a numbered listing of text; a screenshot answered that way is a screenful of mojibake that says
/// nothing. So this tool answers with <see cref="ImageContent"/> instead, and the loop hands the bytes to the model
/// on the message that follows.
/// <para>
/// Two rules keep it honest. The extension decides the media type, because a provider is told what the bytes
/// are and will reject a mislabelled payload — sniffing would only move the failure later. SVG is the one
/// exception because no provider accepts it: <see cref="SvgImage"/> renders it to PNG at the document's own size,
/// and the answer names both the source and the rendering. And the model's own
/// <see cref="SharpCoreAgent.Domain.Model.ModelDescriptor.SupportsImages"/> decides whether the file is read at
/// all: a text-only model that is handed a path answers with an invented description of a picture it never saw,
/// which is the one failure mode this tool exists to prevent.
/// </para>
/// <para>
/// A third rule belongs to the turn rather than to the tool. Every picture a turn attaches is re-sent on every request
/// that follows it, so the loop counts what has travelled and puts what is left in
/// <see cref="ToolInvocationContext.ImageBytesRemaining"/>; a picture that does not fit is refused with both numbers
/// instead of being sent and failing somewhere further away.
/// </para>
/// <para>
/// A fourth belongs to the conversation: a picture it already carries is not attached a second time. The same
/// screenshot asked for on ten rounds would otherwise be paid for on every request after the tenth and tell the model
/// nothing new, so the loop says which pictures are already in the transcript and this tool answers with a sentence
/// instead of a second copy. That answer is a success, not a refusal — the picture is exactly where the model asked for
/// it to be.
/// </para>
/// </remarks>
public sealed class ViewImageTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "view_image";

    private readonly bool _allowWithoutModel;

    /// <summary>Initializes a new instance for a turn whose model is known.</summary>
    public ViewImageTool()
        : this(allowWithoutModel: false)
    {
    }

    /// <summary>
    /// Initializes a new instance, optionally allowing a caller with no model descriptor.
    /// </summary>
    /// <param name="allowWithoutModel">
    /// <see langword="true"/> only for an MCP server, which is a conduit rather than the model that reads the picture:
    /// it returns the image and lets the client decide. A normal turn keeps the capability gate.
    /// </param>
    public ViewImageTool(bool allowWithoutModel) => _allowWithoutModel = allowWithoutModel;

    private const string Schema =
        """{"type":"object","properties":{"path":{"type":"string","description":"Image path relative to the workspace root."}},"required":["path"],"additionalProperties":false}""";

    private const string Description =
        "Show an image file to the model: a PNG, JPEG, GIF, WebP or SVG from the workspace or a read-only attachment, up to 4 MiB. An SVG is rendered to PNG at its own size. Use it when the answer depends on what the picture contains — a screenshot, a diagram, a chart, a photograph — instead of guessing from its name or the text around it. The image arrives attached to the next message; the file itself is never modified. A turn has one budget for the pictures it shows, so a picture refused for it is one to ask for again in a new turn. A picture the conversation already carries is not attached a second time: the answer says so, and the earlier message is the copy to read.";

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

        if (!PathSafety.TryResolveRead(context.Workspace, path, out var fullPath, out var owner, out var error))
        {
            return ToolResult.Refused(toolCall.Id, Name, error!);
        }

        var relative = (owner ?? context.Workspace).ToRelative(fullPath);

        if (Directory.Exists(fullPath))
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                $"'{relative}' is a directory. Use list_directory to see what it contains.");
        }

        if (!File.Exists(fullPath))
        {
            return ToolResult.Failure(toolCall.Id, Name, $"no file at '{relative}'");
        }

        var mediaType = MediaTypeFor(fullPath);
        var isSvg = string.Equals(Path.GetExtension(fullPath), ".svg", StringComparison.OrdinalIgnoreCase);
        if (mediaType is null && !isSvg)
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                $"'{relative}' is not an image this tool can show. It reads files named .png, .jpg, .jpeg, .gif, .webp or .svg; use read_file for text.");
        }

        var length = new FileInfo(fullPath).Length;
        if (length == 0)
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                $"'{relative}' is empty (0 bytes), so there is nothing to show.");
        }

        if (length > TextLimits.MaxImageBytes)
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{relative}' is {length} bytes; view_image shows at most {TextLimits.MaxImageBytes} bytes (4 MiB). Ask the developer to shrink it, or to save a smaller copy."));
        }

        if (!_allowWithoutModel && context.Model is not { SupportsImages: true })
        {
            // Refused before a byte is read, and phrased so the model has nothing to fill in by guessing: a model
            // that cannot see an image will describe one anyway if the only thing it was told is that a file exists.
            var name = context.Model?.QualifiedName ?? "the session's model";
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                $"'{relative}' was not opened: {name} is declared text-only in this build and cannot read images. Do not describe what the image contains — say that you cannot see it, and let the developer switch to a model that reads images.");
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Failure(toolCall.Id, Name, $"'{relative}' could not be read: {ex.Message}");
        }

        var sourceBytes = bytes;
        var displayType = mediaType ?? "image/svg+xml";
        var renderedWidth = 0;
        var renderedHeight = 0;
        if (isSvg)
        {
            if (!SvgImage.TryRender(sourceBytes, TextLimits.MaxImageBytes, out var png, out renderedWidth, out renderedHeight, out var renderError))
            {
                return ToolResult.Failure(
                    toolCall.Id,
                    Name,
                    $"'{relative}' ({sourceBytes.Length} bytes) could not be rendered: {renderError}");
            }

            bytes = png;
            mediaType = "image/png";
        }

        var image = new ImageContent
        {
            MediaType = mediaType!,
            Base64 = Convert.ToBase64String(bytes),
        };

        var description = isSvg
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{displayType} · {sourceBytes.Length} bytes, rendered {renderedWidth} × {renderedHeight} PNG")
            : string.Create(CultureInfo.InvariantCulture, $"{displayType} · {sourceBytes.Length} bytes");

        // Before the budget, and after the read, and both are deliberate: a duplicate can only be recognised by its
        // bytes, and once it is recognised there is nothing left to spend — the picture the turn would have paid for is
        // already in the transcript, so a budget refusal here would name a ceiling that stopped nothing. The answer is a
        // success because the request is met: the model can see the picture, one message earlier.
        if (ImageShown.Already(context.ImagesAlreadyShown, image))
        {
            return ToolResult.Success(
                toolCall.Id,
                Name,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{relative}' ({description}) is not attached again: {ImageShown.Clause}. That earlier message is the copy to read — asking for the same file twice shows nothing new."))
                with
            {
                Effects = [new ToolEffect(ToolEffectKind.Read, relative)],
            };
        }

        if (context.ImageBytesRemaining is { } left && !ImageBudget.Fits(bytes.Length, left))
        {
            // Before the picture is put on the wire: the refusal names the size and what was left, so a model reads it
            // as a budget rather than as a broken file, and learns that a new turn is the way out.
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{relative}' ({description}) was not attached: {ImageBudget.Spent(bytes.Length, left)}."));
        }

        var note = string.Create(
            CultureInfo.InvariantCulture,
            $"'{relative}' ({description}) is attached to the next message.");

        return ToolResult.Image(toolCall.Id, Name, note, image) with
        {
            Effects = [new ToolEffect(ToolEffectKind.Read, relative)],
        };
    }

    /// <summary>
    /// Maps a file extension to the media type a provider is told.
    /// </summary>
    /// <param name="fullPath">The absolute path of the file.</param>
    /// <returns>The media type, or <see langword="null"/> when the extension is not one this tool shows.</returns>
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
