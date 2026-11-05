using SharpCoreAgent.Infrastructure.Images;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The PDF renderer: one page becomes a PNG through PdfPig and SkiaSharp, and a bad document or page is a sentence
/// rather than an exception.
/// </summary>
public sealed class PdfImageTests
{
    [Fact]
    public void APageRendersAsPng()
    {
        Assert.True(
            PdfImage.TryRender(Pdf("Hello PDF"), 1, 4 * 1024 * 1024, 4096, out var png, out var width, out var height, out var error),
            error);

        Assert.True(width > 0);
        Assert.True(height > 0);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);
    }

    [Fact]
    public void APageOutsideTheDocumentIsRefused()
    {
        Assert.False(PdfImage.TryRender(Pdf("one page"), 2, 4 * 1024 * 1024, 4096, out _, out _, out _, out var error));
        Assert.Contains("1 page(s)", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInvalidDocumentIsRefused()
    {
        Assert.False(PdfImage.TryRender("not a pdf"u8, 1, 4 * 1024 * 1024, 4096, out _, out _, out _, out var error));
        Assert.Contains("could not be rendered", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePageNumberMustBeOneOrGreater()
    {
        Assert.False(PdfImage.TryRender(Pdf("one page"), 0, 4 * 1024 * 1024, 4096, out _, out _, out _, out var error));
        Assert.Contains("1 or greater", error, StringComparison.Ordinal);
    }

    private static byte[] Pdf(string text)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(595, 842);
        page.AddText(text, 24, new PdfPoint(50, 750), font);
        return builder.Build();
    }
}
