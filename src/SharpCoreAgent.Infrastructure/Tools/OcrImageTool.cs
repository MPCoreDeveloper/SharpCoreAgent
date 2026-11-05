using System.Globalization;
using RapidOcrNet;
using SkiaSharp;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Images;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Reads the text out of a picture with a local ONNX OCR engine.
/// </summary>
/// <remarks>
/// This is the capability a text-only model needs: <c>view_image</c> refuses to show a picture to a model that cannot
/// see it, while OCR turns the pixels into text the model can read. The engine is RapidOcrNet's PP-OCRv5 Latin model,
/// bundled with the application, and the file goes through <see cref="ImageFile"/> first so the same path safety,
/// extension table, SVG/PDF rendering and 4 MiB ceiling apply. The tool never sends the picture anywhere.
/// </remarks>
public sealed class OcrImageTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "ocr_image";

    private const string Schema =
        """{"type":"object","properties":{"path":{"type":"string","description":"Image, SVG or PDF path relative to the workspace root; PDF page 1 is used."}},"required":["path"],"additionalProperties":false}""";

    private const string Description =
        "Read the text in a picture with a local OCR engine: a PNG, JPEG, GIF, WebP, SVG or PDF from the workspace or a read-only attachment, up to 4 MiB. Use it when the answer is the text a screenshot or scan contains, especially when the session's model cannot see images. It returns the recognized text, never describes the picture, and never sends the file anywhere.";

    private static readonly SemaphoreSlim EngineGate = new(1, 1);
    private static readonly Lazy<RapidOcr> Engine = new(CreateEngine, LazyThreadSafetyMode.ExecutionAndPublication);

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

        if (!PathSafety.TryResolveRead(context.Workspace, path, out var fullPath, out var owner, out var resolveError))
        {
            return ToolResult.Refused(toolCall.Id, Name, resolveError ?? "the path could not be resolved");
        }

        var relative = (owner ?? context.Workspace).ToRelative(fullPath);
        if (!ImageFile.TryLoad(context.Workspace, path, out var image, out var error))
        {
            return ToolResult.Failure(toolCall.Id, Name, error);
        }

        var bytes = Convert.FromBase64String(image.Base64);
        using var bitmap = SKBitmap.Decode(bytes);
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                $"'{relative}' could not be decoded for OCR.");
        }

        OcrResult result;
        await EngineGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            result = await Engine.Value
                .DetectAsync(bitmap, RapidOcrOptions.Default, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                $"'{relative}' could not be read by OCR: {ex.Message}");
        }
        finally
        {
            EngineGate.Release();
        }

        var text = result.StrRes.Trim();
        if (text.Length == 0)
        {
            return ToolResult.Success(
                toolCall.Id,
                Name,
                $"'{relative}' contains no recognizable text.") with
            {
                Effects = [new ToolEffect(ToolEffectKind.Read, relative)],
            };
        }

        var bounded = TextLimits.Bound(text, TextLimits.MaxOcrChars, out var truncated);
        return ToolResult.Success(
            toolCall.Id,
            Name,
            string.Create(CultureInfo.InvariantCulture, $"'{relative}':\n{bounded}")) with
        {
            IsTruncated = truncated,
            Effects = [new ToolEffect(ToolEffectKind.Read, relative)],
        };
    }

    private static RapidOcr CreateEngine()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "models", "v5");
        var detector = Path.Combine(root, "ch_PP-OCRv5_mobile_det.onnx");
        var classifier = Path.Combine(root, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
        var recognizer = Path.Combine(root, "latin_PP-OCRv5_rec_mobile_infer.onnx");
        var keys = Path.Combine(root, "ppocrv5_latin_dict.txt");
        if (!File.Exists(detector) || !File.Exists(classifier) || !File.Exists(recognizer) || !File.Exists(keys))
        {
            throw new FileNotFoundException(
                string.Create(CultureInfo.InvariantCulture, $"the OCR models are not installed under '{root}'"));
        }

        var engine = new RapidOcr();
        engine.InitModels(detector, classifier, recognizer, keys);
        return engine;
    }
}
