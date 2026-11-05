using SharpCoreAgent.Infrastructure.Images;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The SVG renderer is the only place a vector becomes pixels, so its two boundaries are pinned here: the document's
/// own size is honoured, and an artboard wider than the renderer ceiling is scaled down rather than allocated whole.
/// </summary>
public sealed class SvgImageTests
{
    [Fact]
    public void AnSvgIsRenderedAtItsOwnSize()
    {
        Assert.True(
            SvgImage.TryRender(Svg(120, 40), 4 * 1024 * 1024, out var png, out var width, out var height, out var error),
            error);

        Assert.Equal(120, width);
        Assert.Equal(40, height);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);
    }

    [Fact]
    public void AnArtboardOverTheCeilingIsScaledDownUniformly()
    {
        Assert.True(
            SvgImage.TryRender(Svg(5000, 100), 4 * 1024 * 1024, out _, out var width, out var height, out var error),
            error);

        Assert.Equal(SvgImage.MaxDimension, width);
        Assert.True(height < 100);
    }

    [Fact]
    public void AMalformedDocumentIsRefused()
    {
        Assert.False(SvgImage.TryRender("<svg><not-closed>"u8, 4 * 1024 * 1024, out _, out _, out _, out var error));
        Assert.Contains("could not be rendered", error, StringComparison.Ordinal);
    }

    private static byte[] Svg(int width, int height) =>
        System.Text.Encoding.UTF8.GetBytes(
            $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">
              <rect width="{width}" height="{height}" fill="#0f0"/>
            </svg>
            """);
}
