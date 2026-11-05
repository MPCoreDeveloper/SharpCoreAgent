using System.Globalization;
using SkiaSharp;

namespace SharpCoreAgent.Infrastructure.Images;

/// <summary>
/// Explicitly downscales a raster picture when a caller asks for a smaller copy.
/// </summary>
/// <remarks>
/// Nothing calls this by default: `view_image` shows the developer's file byte for byte unless it was given a
/// <c>max_dimension</c>. The resize preserves the aspect ratio, uses linear sampling, and writes PNG because the result
/// is a new image rather than the original bytes. It is not a rotation tool: a request that needs one is refused by the
/// caller's schema rather than silently transformed here.
/// </remarks>
internal static class ImageResize
{
    /// <summary>Gets the smallest dimension a caller may request.</summary>
    public const int MinimumDimension = 64;

    /// <summary>Gets the largest dimension a caller may request.</summary>
    public const int MaximumDimension = 4096;

    /// <summary>
    /// Downscales one raster image to fit inside a square of <paramref name="maxDimension"/>.
    /// </summary>
    /// <param name="source">The original bytes.</param>
    /// <param name="maxDimension">The largest width or height the result may have.</param>
    /// <param name="png">The PNG to attach; the original bytes when no scaling was needed.</param>
    /// <param name="width">The original or resized width.</param>
    /// <param name="height">The original or resized height.</param>
    /// <param name="changed">Whether the bytes are a new rendering.</param>
    /// <param name="error">One sentence explaining a refusal, on failure.</param>
    /// <returns><see langword="true"/> when the picture is usable.</returns>
    public static bool TryResize(
        ReadOnlySpan<byte> source,
        int maxDimension,
        out byte[] png,
        out int width,
        out int height,
        out bool changed,
        out string error)
    {
        png = [];
        width = 0;
        height = 0;
        changed = false;

        if (maxDimension is < MinimumDimension or > MaximumDimension)
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"max_dimension must be between {MinimumDimension} and {MaximumDimension}, not {maxDimension}");
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

            if (bitmap is null)
            {
                error = "the image could not be decoded";
                return false;
            }

            using (bitmap)
            {
                width = bitmap.Width;
                height = bitmap.Height;
                if (width <= 0 || height <= 0)
                {
                    error = "the image could not be decoded";
                    return false;
                }

                if (width <= maxDimension && height <= maxDimension)
                {
                    png = source.ToArray();
                    error = string.Empty;
                    return true;
                }

                var scale = Math.Min(
                    (double)maxDimension / width,
                    (double)maxDimension / height);
                var resizedWidth = Math.Max(1, (int)Math.Round(width * scale));
                var resizedHeight = Math.Max(1, (int)Math.Round(height * scale));

                using var resized = bitmap.Resize(
                    new SKImageInfo(resizedWidth, resizedHeight, SKColorType.Rgba8888, SKAlphaType.Premul),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                if (resized is null)
                {
                    error = "the image could not be resized";
                    return false;
                }

                using var image = SKImage.FromBitmap(resized);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                if (data is null)
                {
                    error = "the resized image could not be encoded";
                    return false;
                }

                png = data.ToArray();
                width = resizedWidth;
                height = resizedHeight;
                changed = true;
                error = string.Empty;
                return true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = $"the image could not be resized: {ex.Message}";
            png = [];
            width = 0;
            height = 0;
            return false;
        }
    }
}
