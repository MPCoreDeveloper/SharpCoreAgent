using System.Globalization;
using SkiaSharp;
using Svg.Skia;

namespace SharpCoreAgent.Infrastructure.Images;

/// <summary>
/// Renders an SVG into a PNG, because no provider accepts a vector file as an attached picture.
/// </summary>
/// <remarks>
/// This is rasterisation, not resizing: an SVG has no pixels of its own until a size is chosen, and the size this class
/// chooses is the document's own <c>width</c>/<c>height</c> or its <c>viewBox</c>. A document larger than
/// <see cref="MaxDimension"/> is scaled down uniformly so one enormous artboard cannot allocate a gigabyte; the
/// downscale is reported rather than hidden. JavaScript and external JavaScript are disabled before the document is
/// parsed: a picture is data, and evaluating it would turn a screenshot into a program.
/// <para>
/// A malformed document, a zero-sized one, or a render over the same 4 MiB ceiling every other image door uses is a
/// refusal with a sentence, never a partially written picture.
/// </para>
/// </remarks>
internal static class SvgImage
{
    /// <summary>Gets the largest width or height a rendered SVG may have.</summary>
    public const int MaxDimension = 4096;

    /// <summary>
    /// Renders SVG bytes to PNG bytes.
    /// </summary>
    /// <param name="svg">The document as it arrived.</param>
    /// <param name="maxBytes">The ceiling the rendered PNG must fit inside.</param>
    /// <param name="png">The rendered PNG, on success.</param>
    /// <param name="width">The rendered width in pixels, on success.</param>
    /// <param name="height">The rendered height in pixels, on success.</param>
    /// <param name="error">One sentence explaining a refusal, on failure.</param>
    /// <param name="maxDimension">The largest width or height the caller asked for; defaults to <see cref="MaxDimension"/>.</param>
    /// <returns><see langword="true"/> when the document rendered inside the ceilings.</returns>
    public static bool TryRender(
        ReadOnlySpan<byte> svg,
        long maxBytes,
        out byte[] png,
        out int width,
        out int height,
        out string error,
        int maxDimension = MaxDimension)
    {
        png = [];
        width = 0;
        height = 0;

        if (svg.IsEmpty)
        {
            error = "the SVG is empty";
            return false;
        }

        try
        {
            using var document = new SKSvg();
            document.Settings.EnableJavaScript = false;
            document.Settings.EnableExternalJavaScript = false;

            using var input = new MemoryStream(svg.ToArray(), writable: false);
            var picture = document.Load(input);
            if (picture is null)
            {
                error = "the SVG could not be parsed";
                return false;
            }

            var cull = picture.CullRect;
            if (!float.IsFinite(cull.Width) || !float.IsFinite(cull.Height) || cull.Width <= 0 || cull.Height <= 0)
            {
                error = string.Create(
                    CultureInfo.InvariantCulture,
                    $"the SVG has no usable size ({cull.Width:0.#} × {cull.Height:0.#})");
                return false;
            }

            var scale = Math.Min(1d, Math.Min(MaxDimension, maxDimension) / Math.Max(cull.Width, cull.Height));
            width = Math.Max(1, (int)Math.Ceiling(cull.Width * scale));
            height = Math.Max(1, (int)Math.Ceiling(cull.Height * scale));

            using var output = new MemoryStream();
            if (!document.Save(output, SKColors.Transparent, SKEncodedImageFormat.Png, 100, (float)scale, (float)scale))
            {
                error = "the SVG could not be rendered";
                return false;
            }

            png = output.ToArray();
            if (png.LongLength > maxBytes)
            {
                error = string.Create(
                    CultureInfo.InvariantCulture,
                    $"the {width} × {height} rendering is {png.LongLength} bytes; the picture ceiling is {maxBytes} bytes");
                png = [];
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = $"the SVG could not be rendered: {ex.Message}";
            png = [];
            width = 0;
            height = 0;
            return false;
        }
    }
}
