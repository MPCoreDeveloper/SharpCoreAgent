using SkiaSharp;
using SharpCoreAgent.Infrastructure.Images;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The explicit resize helper: no work unless a caller asks, aspect ratio preserved, and an undecodable or
/// out-of-range request refused rather than guessed at.
/// </summary>
public sealed class ImageResizeTests
{
    [Fact]
    public void APictureThatAlreadyFitsIsReturnedUnchanged()
    {
        var source = Png(20, 10);

        Assert.True(
            ImageResize.TryResize(source, 64, out var png, out var width, out var height, out var changed, out var error),
            error);

        Assert.False(changed);
        Assert.Equal(20, width);
        Assert.Equal(10, height);
        Assert.Equal(source, png);
    }

    [Fact]
    public void AnOversizedPictureIsScaledDownAndReencodedAsPng()
    {
        var source = Png(200, 100);

        Assert.True(
            ImageResize.TryResize(source, 64, out var png, out var width, out var height, out var changed, out var error),
            error);

        Assert.True(changed);
        Assert.Equal(64, width);
        Assert.Equal(32, height);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);
    }

    [Fact]
    public void AnUndecodableFileIsRefused()
    {
        Assert.False(ImageResize.TryResize("not a picture"u8, 64, out _, out _, out _, out _, out var error));
        Assert.Contains("could not be decoded", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ADimensionOutsideTheRangeIsRefused()
    {
        Assert.False(ImageResize.TryResize(Png(20, 10), 32, out _, out _, out _, out _, out var error));
        Assert.Contains("between 64 and 4096", error, StringComparison.Ordinal);
    }

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
}
