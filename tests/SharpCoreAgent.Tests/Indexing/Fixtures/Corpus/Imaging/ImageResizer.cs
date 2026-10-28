namespace Demo.Imaging;

/// Downscales an image with bilinear interpolation between the four nearest source pixels.
public static class ImageResizer
{
    public static PixelBuffer Downscale(PixelBuffer source, int targetWidth, int targetHeight)
    {
        var target = new PixelBuffer(targetWidth, targetHeight);
        var xRatio = (double)(source.Width - 1) / targetWidth;
        var yRatio = (double)(source.Height - 1) / targetHeight;

        for (var y = 0; y < targetHeight; y++)
        {
            for (var x = 0; x < targetWidth; x++)
            {
                target[x, y] = Sample(source, x * xRatio, y * yRatio);
            }
        }

        return target;
    }

    private static uint Sample(PixelBuffer source, double x, double y)
    {
        var left = (int)Math.Floor(x);
        var top = (int)Math.Floor(y);
        var right = Math.Min(left + 1, source.Width - 1);
        var bottom = Math.Min(top + 1, source.Height - 1);
        var upper = Blend(source[left, top], source[right, top], x - left);
        var lower = Blend(source[left, bottom], source[right, bottom], x - left);
        return Blend(upper, lower, y - top);
    }

    private static uint Blend(uint from, uint to, double weight) =>
        (uint)((from * (1 - weight)) + (to * weight));
}
