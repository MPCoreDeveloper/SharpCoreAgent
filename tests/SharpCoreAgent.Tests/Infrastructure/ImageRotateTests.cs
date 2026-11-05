using SkiaSharp;
using SharpCoreAgent.Infrastructure.Images;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The explicit quarter-turn helper: clockwise 90/180/270, dimensions swapped only where they should be, and an
/// angle outside the four quarter turns refused.
/// </summary>
public sealed class ImageRotateTests
{
    [Fact]
    public void AQuarterTurnSwapsTheDimensions()
    {
        Assert.True(
            ImageRotate.TryRotate(Png(4, 2), 90, out var png, out var width, out var height, out var error),
            error);

        Assert.Equal(2, width);
        Assert.Equal(4, height);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);
    }

    [Fact]
    public void AHalfTurnKeepsTheDimensions()
    {
        Assert.True(
            ImageRotate.TryRotate(Png(4, 2), 180, out _, out var width, out var height, out var error),
            error);

        Assert.Equal(4, width);
        Assert.Equal(2, height);
    }

    [Fact]
    public void AnAngleOutsideTheQuarterTurnsIsRefused()
    {
        Assert.False(ImageRotate.TryRotate(Png(4, 2), 45, out _, out _, out _, out var error));
        Assert.Contains("must be 90, 180 or 270", error, StringComparison.Ordinal);
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
