using SkiaSharp;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Tools;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Tools;

/// <summary>
/// OCR reads the text a text-only model cannot see, through the same path and file rules as <c>view_image</c>.
/// </summary>
public sealed class OcrImageToolTests
{
    [Fact]
    public async Task ItReadsTheTextFromAPicture()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.WriteBytes("shots/note.png", Png("HELLO OCR"));

        var result = await new OcrImageTool().ExecuteAsync(
            ToolTestWorkspace.Call("ocr_image", """{"path":"shots/note.png"}"""),
            new ToolInvocationContext(sandbox.Workspace),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.Contains("HELLO", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new ToolEffect(ToolEffectKind.Read, "shots/note.png"), Assert.Single(result.Effects));
    }

    [Fact]
    public async Task APictureWithoutTextIsAnEmptyAnswer()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.WriteBytes("shots/blank.png", Png(string.Empty));

        var result = await new OcrImageTool().ExecuteAsync(
            ToolTestWorkspace.Call("ocr_image", """{"path":"shots/blank.png"}"""),
            new ToolInvocationContext(sandbox.Workspace),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.Contains("no recognizable text", result.Content, StringComparison.Ordinal);
    }

    private static byte[] Png(string text)
    {
        using var bitmap = new SKBitmap(320, 96);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            if (text.Length > 0)
            {
                using var typeface = SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;
                using var font = new SKFont(typeface, 40);
                using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
                canvas.DrawText(text, 12, 60, SKTextAlign.Left, font, paint);
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
