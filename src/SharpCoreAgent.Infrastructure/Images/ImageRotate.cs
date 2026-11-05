using System.Globalization;
using SkiaSharp;

namespace SharpCoreAgent.Infrastructure.Images;

/// <summary>
/// Rotates a raster picture by one of the four quarter turns a caller can name.
/// </summary>
/// <remarks>
/// Like <see cref="ImageResize"/>, this runs only when the caller asked for it. The rotation is clockwise around the
/// picture's centre; 90 and 270 swap the dimensions, and the result is re-encoded as PNG. Arbitrary angles are not
/// accepted: a photograph turned by 17 degrees is a new picture, not a correction, and the caller has to ask for that
/// separately in a future contract.
/// </remarks>
internal static class ImageRotate
{
    /// <summary>
    /// Rotates one raster image.
    /// </summary>
    /// <param name="source">The original bytes.</param>
    /// <param name="degrees">One of 90, 180 or 270.</param>
    /// <param name="png">The rotated PNG.</param>
    /// <param name="width">The rotated width.</param>
    /// <param name="height">The rotated height.</param>
    /// <param name="error">One sentence explaining a refusal, on failure.</param>
    /// <returns><see langword="true"/> when the picture was rotated.</returns>
    public static bool TryRotate(
        ReadOnlySpan<byte> source,
        int degrees,
        out byte[] png,
        out int width,
        out int height,
        out string error)
    {
        png = [];
        width = 0;
        height = 0;

        if (degrees is not (90 or 180 or 270))
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"rotation must be 90, 180 or 270, not {degrees}");
            return false;
        }

        try
        {
            SKBitmap? bitmap;
            try
            {
                bitmap = SKBitmap.Decode(source.ToArray());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                error = $"the image could not be decoded: {ex.Message}";
                return false;
            }

            if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                error = "the image could not be decoded";
                return false;
            }

            using (bitmap)
            {
                width = degrees == 180 ? bitmap.Width : bitmap.Height;
                height = degrees == 180 ? bitmap.Height : bitmap.Width;
                using var rotated = new SKBitmap(width, height);
                using var canvas = new SKCanvas(rotated);
                canvas.Translate(width / 2f, height / 2f);
                canvas.RotateDegrees(degrees);
                canvas.Translate(-bitmap.Width / 2f, -bitmap.Height / 2f);
                canvas.DrawBitmap(
                    bitmap,
                    0,
                    0,
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                canvas.Flush();

                using var image = SKImage.FromBitmap(rotated);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                if (data is null)
                {
                    error = "the rotated image could not be encoded";
                    return false;
                }

                png = data.ToArray();
                error = string.Empty;
                return true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = $"the image could not be rotated: {ex.Message}";
            png = [];
            width = 0;
            height = 0;
            return false;
        }
    }
}
