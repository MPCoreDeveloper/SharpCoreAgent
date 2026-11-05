using SkiaSharp;
using SharpCoreAgent.Domain.Conversation;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Tools;

/// <summary>
/// <c>view_image</c> is the only path by which a picture enters a conversation, so the tests pin what makes it
/// safe to offer: the file is read only when the model can see it, the extension decides the media type, and
/// nothing over the ceiling, outside the workspace or of the wrong kind is opened at all.
/// </summary>
/// <remarks>
/// The capability gate is the one assertion worth reading twice. A refused call must not carry an image, because
/// a tool result that quietly carries bytes is exactly how a text-only model ends up describing a picture — and
/// the message it is given has to say "do not describe" in as many words, since a model fills silence with an
/// invented description.
/// </remarks>
public sealed class ViewImageToolTests
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03];

    [Fact]
    public async Task HandsTheFileToTheLoopAsAnImage()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.WriteBytes("shots/shot.png", PngBytes);

        var result = await new ViewImageTool().ExecuteAsync(
            ToolTestWorkspace.Call("view_image", """{"path":"shots/shot.png"}"""),
            ReadingImages(sandbox),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.False(result.IsTruncated);
        Assert.Equal(
            "'shots/shot.png' (image/png · 11 bytes) is attached to the next message.",
            result.Content);

        var image = Assert.Single(result.Images);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(Convert.ToBase64String(PngBytes), image.Base64);
        Assert.Equal(new ToolEffect(ToolEffectKind.Read, "shots/shot.png"), Assert.Single(result.Effects));
    }

    [Theory]
    [InlineData("a.png", "image/png")]
    [InlineData("a.PNG", "image/png")]
    [InlineData("a.jpg", "image/jpeg")]
    [InlineData("a.jpeg", "image/jpeg")]
    [InlineData("a.gif", "image/gif")]
    [InlineData("a.webp", "image/webp")]
    public async Task TheExtensionDecidesTheMediaTypeTheProviderIsTold(string name, string mediaType)
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.WriteBytes(name, PngBytes);

        var result = await new ViewImageTool().ExecuteAsync(
            ToolTestWorkspace.Call("view_image", ToolTestWorkspace.Args(new { path = name })),
            ReadingImages(sandbox),
            CancellationToken.None);

        Assert.Equal(mediaType, Assert.Single(result.Images).MediaType);
    }

    [Fact]
    public async Task AFileThatIsNotAnImageIsAnsweredWithTheListOfKinds()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("notes.txt", "hello");

        var result = await new ViewImageTool().ExecuteAsync(
            ToolTestWorkspace.Call("view_image", """{"path":"notes.txt"}"""),
            ReadingImages(sandbox),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Empty(result.Images);
        Assert.Contains(".png, .jpg, .jpeg, .gif, .webp, .svg or .pdf", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARequestedMaxDimensionResizesTheAttachment()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.WriteBytes("shots/wide.png", Png(200, 100));

        var result = await new ViewImageTool().ExecuteAsync(
            ToolTestWorkspace.Call("view_image", """{"path":"shots/wide.png","max_dimension":64}"""),
            ReadingImages(sandbox),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("resized 64 × 32 PNG", result.Content, StringComparison.Ordinal);

        var image = Assert.Single(result.Images);
        Assert.Equal("image/png", image.MediaType);

        using var decoded = SKBitmap.Decode(Convert.FromBase64String(image.Base64))
            ?? throw new InvalidOperationException("the attached image did not decode");
        Assert.Equal(64, decoded.Width);
        Assert.Equal(32, decoded.Height);
    }

    [Fact]
    public async Task ARequestedRotationRotatesTheAttachment()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.WriteBytes("shots/wide.png", Png(200, 100));

        var result = await new ViewImageTool().ExecuteAsync(
            ToolTestWorkspace.Call("view_image", """{"path":"shots/wide.png","rotation":90}"""),
            ReadingImages(sandbox),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("rotated 90° to 100 × 200", result.Content, StringComparison.Ordinal);

        var image = Assert.Single(result.Images);
        using var decoded = SKBitmap.Decode(Convert.FromBase64String(image.Base64))
            ?? throw new InvalidOperationException("the attached image did not decode");
        Assert.Equal(100, decoded.Width);
        Assert.Equal(200, decoded.Height);
    }

    [Fact]
    public async Task APdfPageIsRenderedToPng()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.WriteBytes("docs/report.pdf", Pdf("Hello PDF"));

        var result = await new ViewImageTool().ExecuteAsync(
            ToolTestWorkspace.Call("view_image", """{"path":"docs/report.pdf"}"""),
            ReadingImages(sandbox),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("application/pdf", result.Content, StringComparison.Ordinal);
        Assert.Contains("page 1 rendered", result.Content, StringComparison.Ordinal);

        var image = Assert.Single(result.Images);
        Assert.Equal("image/png", image.MediaType);
        using var decoded = SKBitmap.Decode(Convert.FromBase64String(image.Base64))
            ?? throw new InvalidOperationException("the PDF render did not decode");
        Assert.True(decoded.Width > 0);
        Assert.True(decoded.Height > 0);
    }

    [Fact]
    public async Task APdfPageOutsideTheDocumentIsRefused()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.WriteBytes("docs/report.pdf", Pdf("one page"));

        var result = await new ViewImageTool().ExecuteAsync(
            ToolTestWorkspace.Call("view_image", """{"path":"docs/report.pdf","page":2}"""),
            ReadingImages(sandbox),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Empty(result.Images);
        Assert.Contains("1 page(s)", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnSvgIsRenderedToPngAtItsOwnSize()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("diagrams/flow.svg", Svg);

        var result = await new ViewImageTool().ExecuteAsync(
            ToolTestWorkspace.Call("view_image", """{"path":"diagrams/flow.svg"}"""),
            ReadingImages(sandbox),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("image/svg+xml", result.Content, StringComparison.Ordinal);
        Assert.Contains("rendered 120 × 40 PNG", result.Content, StringComparison.Ordinal);

        var image = Assert.Single(result.Images);
        Assert.Equal("image/png", image.MediaType);

        var bytes = Convert.FromBase64String(image.Base64);
        Assert.True(bytes.Length > 8);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], bytes[..8]);
        Assert.Equal(new ToolEffect(ToolEffectKind.Read, "diagrams/flow.svg"), Assert.Single(result.Effects));
    }

    [Fact]
    public async Task AnSvgThatCannotBeParsedIsRefused()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("broken.svg", "<svg><not-closed>");

        var result = await new ViewImageTool().ExecuteAsync(
            ToolTestWorkspace.Call("view_image", """{"path":"broken.svg"}"""),
            ReadingImages(sandbox),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Empty(result.Images);
        Assert.Contains("could not be rendered", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATextOnlyModelIsRefusedAndToldNotToDescribeWhatItCannotSee()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.WriteBytes("shot.png", PngBytes);

        var result = await new ViewImageTool().ExecuteAsync(
            ToolTestWorkspace.Call("view_image", """{"path":"shot.png"}"""),
            TextOnly(sandbox),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Empty(result.Images);
        Assert.Empty(result.Effects);
        Assert.Contains("deepseek:deepseek-chat", result.Content, StringComparison.Ordinal);
        Assert.Contains("text-only", result.Content, StringComparison.Ordinal);
        Assert.Contains("Do not describe what the image contains", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASessionThatNamedNoModelIsRefusedRatherThanAssumedToSee()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.WriteBytes("shot.png", PngBytes);

        var result = await new ViewImageTool().ExecuteAsync(
            ToolTestWorkspace.Call("view_image", """{"path":"shot.png"}"""),
            sandbox.Context,
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Empty(result.Images);
        Assert.Contains("the session's model", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyFileIsNothingToShow()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.WriteBytes("shot.png", []);

        var result = await new ViewImageTool().ExecuteAsync(
            ToolTestWorkspace.Call("view_image", """{"path":"shot.png"}"""),
            ReadingImages(sandbox),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Empty(result.Images);
        Assert.Contains("is empty (0 bytes)", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileOverTheCeilingNamesTheLimitAndTheSize()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.WriteBytes("huge.png", new byte[TextLimits.MaxImageBytes + 1]);

        var result = await new ViewImageTool().ExecuteAsync(
            ToolTestWorkspace.Call("view_image", """{"path":"huge.png"}"""),
            ReadingImages(sandbox),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Empty(result.Images);
        Assert.Contains(
            $"is {TextLimits.MaxImageBytes + 1} bytes; view_image shows at most {TextLimits.MaxImageBytes} bytes",
            result.Content,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task APathOutsideTheWorkspaceIsRefused()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var outside = Path.Combine(Path.GetDirectoryName(sandbox.Root)!, "outside.png");
        await File.WriteAllBytesAsync(outside, PngBytes, TestContext.Current.CancellationToken);

        var result = await new ViewImageTool().ExecuteAsync(
            ToolTestWorkspace.Call("view_image", """{"path":"../outside.png"}"""),
            ReadingImages(sandbox),
            CancellationToken.None);

        Assert.True(result.IsRefused);
        Assert.Empty(result.Images);
        Assert.Contains("outside the workspace root", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnAttachedRootIsReadableAndNamedRelativeToItsOwnRoot()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var shared = sandbox.AddAttachment("shared");
        var full = ToolTestWorkspace.WriteUnder(shared, "logo.png", "placeholder");
        await File.WriteAllBytesAsync(full, PngBytes, TestContext.Current.CancellationToken);

        var result = await new ViewImageTool().ExecuteAsync(
            ToolTestWorkspace.Call("view_image", ToolTestWorkspace.Args(new { path = full })),
            ReadingImages(sandbox),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Single(result.Images);
        Assert.Equal(new ToolEffect(ToolEffectKind.Read, "logo.png"), Assert.Single(result.Effects));
        Assert.StartsWith("'logo.png' (image/png", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADirectoryIsReportedAsOne()
    {
        using var sandbox = ToolTestWorkspace.Create();
        Directory.CreateDirectory(sandbox.FullPath("shots.png"));

        var result = await new ViewImageTool().ExecuteAsync(
            ToolTestWorkspace.Call("view_image", """{"path":"shots.png"}"""),
            ReadingImages(sandbox),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("is a directory", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingFileIsReportedByItsRelativeName()
    {
        using var sandbox = ToolTestWorkspace.Create();

        var result = await new ViewImageTool().ExecuteAsync(
            ToolTestWorkspace.Call("view_image", """{"path":"gone.png"}"""),
            ReadingImages(sandbox),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("no file at 'gone.png'", result.Content);
    }

    [Fact]
    public async Task ATurnWithNoBudgetLeftRefusesThePictureAndNamesBothNumbers()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.WriteBytes("shots/shot.png", PngBytes);

        var result = await new ViewImageTool().ExecuteAsync(
            ToolTestWorkspace.Call("view_image", """{"path":"shots/shot.png"}"""),
            ReadingImages(sandbox) with { ImageBytesRemaining = 0 },
            CancellationToken.None);

        // A refusal rather than an image: the turn is what a provider charges for, and a picture it has no room for
        // fails somewhere further away — as a request the provider rejects for a reason it does not explain.
        Assert.True(result.IsError);
        Assert.Empty(result.Images);
        Assert.Contains("was not attached", result.Content, StringComparison.Ordinal);
        Assert.Contains("0 B of image budget left", result.Content, StringComparison.Ordinal);
        Assert.Contains("the picture is 11 B", result.Content, StringComparison.Ordinal);

        // And the way out, because the ceiling is per turn: asking again in this one cannot work.
        Assert.Contains("a new turn starts with a fresh budget", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APictureThatExactlyFitsWhatTheTurnHasLeftIsShown()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.WriteBytes("shots/shot.png", PngBytes);

        var result = await new ViewImageTool().ExecuteAsync(
            ToolTestWorkspace.Call("view_image", """{"path":"shots/shot.png"}"""),
            ReadingImages(sandbox) with { ImageBytesRemaining = PngBytes.Length },
            CancellationToken.None);

        // The boundary is inclusive: a budget equal to the picture spends it exactly and shows the picture.
        Assert.False(result.IsError);
        Assert.Single(result.Images);

        // Nothing stays out of the ceiling by accident: an unstated budget still attaches, which is what every test
        // written before the ceiling existed already proves.
        var unstated = await new ViewImageTool().ExecuteAsync(
            ToolTestWorkspace.Call("view_image", """{"path":"shots/shot.png"}"""),
            ReadingImages(sandbox),
            CancellationToken.None);
        Assert.Single(unstated.Images);
    }

    [Fact]
    public async Task APictureTheConversationAlreadyCarriesIsNotAttachedAgain()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.WriteBytes("shots/shot.png", PngBytes);

        var first = await new ViewImageTool().ExecuteAsync(
            ToolTestWorkspace.Call("view_image", """{"path":"shots/shot.png"}"""),
            ReadingImages(sandbox),
            CancellationToken.None);
        var shown = Assert.Single(first.Images);

        var second = await new ViewImageTool().ExecuteAsync(
            ToolTestWorkspace.Call("view_image", """{"path":"shots/shot.png"}"""),
            ReadingImages(sandbox) with { ImagesAlreadyShown = new HashSet<ImageContent> { shown } },
            CancellationToken.None);

        // A success, not a refusal: the picture is exactly where the model asked for it to be — one message earlier,
        // and attached there once. A refusal would read as "you cannot see it", which is the opposite of the truth.
        Assert.False(second.IsError);
        Assert.Empty(second.Images);
        Assert.Contains("is not attached again", second.Content, StringComparison.Ordinal);
        Assert.Contains("the same picture is already in this conversation", second.Content, StringComparison.Ordinal);

        // The file was opened to recognise it, so the effect still says so, even though nothing travelled.
        Assert.Equal(new ToolEffect(ToolEffectKind.Read, "shots/shot.png"), Assert.Single(second.Effects));
    }

    [Fact]
    public async Task APictureTheConversationDoesNotCarryStillTravels()
    {
        using var sandbox = ToolTestWorkspace.Create();
        byte[] edited = [.. PngBytes, 0x04];
        sandbox.WriteBytes("shots/shot.png", edited);

        var result = await new ViewImageTool().ExecuteAsync(
            ToolTestWorkspace.Call("view_image", """{"path":"shots/shot.png"}"""),
            ReadingImages(sandbox) with
            {
                // The same path, and different bytes: a file rewritten between two calls is a new picture, and identity
                // is what the provider would be charged for rather than the name it arrived under.
                ImagesAlreadyShown = new HashSet<ImageContent>
                {
                    new ImageContent { MediaType = "image/png", Base64 = Convert.ToBase64String(PngBytes) },
                },
            },
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(Convert.ToBase64String(edited), Assert.Single(result.Images).Base64);
    }

    [Fact]
    public async Task ADuplicateIsAnsweredAsShownEvenWhenTheTurnHasNoBudgetLeft()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.WriteBytes("shots/shot.png", PngBytes);
        var picture = new ImageContent { MediaType = "image/png", Base64 = Convert.ToBase64String(PngBytes) };

        var result = await new ViewImageTool().ExecuteAsync(
            ToolTestWorkspace.Call("view_image", """{"path":"shots/shot.png"}"""),
            ReadingImages(sandbox) with
            {
                ImagesAlreadyShown = new HashSet<ImageContent> { picture },
                ImageBytesRemaining = 0,
            },
            CancellationToken.None);

        // A duplicate spends nothing, so the budget is not what stopped anything and is not named: the reason a model
        // reads has to be the reason that applies.
        Assert.False(result.IsError);
        Assert.Empty(result.Images);
        Assert.Contains("is not attached again", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("budget", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void IsReadOnlyAndAsksForOnePath()
    {
        var tool = new ViewImageTool();

        Assert.Equal(ViewImageTool.Name, tool.Definition.Name);
        Assert.Equal("view_image", tool.Definition.Name);
        Assert.Equal(ToolSafety.ReadOnly, tool.Safety);
        Assert.Contains("\"required\":[\"path\"]", tool.Definition.ParametersSchemaJson, StringComparison.Ordinal);
        Assert.Contains("\"additionalProperties\":false", tool.Definition.ParametersSchemaJson, StringComparison.Ordinal);
        Assert.Contains("never modified", tool.Definition.Description, StringComparison.Ordinal);
    }

    private static ToolInvocationContext ReadingImages(ToolTestWorkspace sandbox) =>
        new(sandbox.Workspace) { Model = new ModelDescriptor("grok", "grok-4", "Grok 4", SupportsImages: true) };

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Green);
        canvas.Flush();
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] Pdf(string text)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(595, 842);
        page.AddText(text, 24, new PdfPoint(50, 750), font);
        return builder.Build();
    }

    private const string Svg =
        """
        <svg xmlns="http://www.w3.org/2000/svg" width="120" height="40" viewBox="0 0 120 40">
          <rect width="120" height="40" fill="#0f0"/>
          <text x="4" y="28" font-size="20" fill="#000">SVG</text>
        </svg>
        """;

    private static ToolInvocationContext TextOnly(ToolTestWorkspace sandbox) =>
        new(sandbox.Workspace) { Model = new ModelDescriptor("deepseek", "deepseek-chat", "DeepSeek Chat") };
}
