using System.Globalization;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Rendering.Skia;

namespace SharpCoreAgent.Infrastructure.Images;

/// <summary>
/// Renders one PDF page as a PNG through PdfPig's Skia renderer.
/// </summary>
/// <remarks>
/// The renderer is managed and cross-platform: PdfPig parses the document and SkiaSharp draws the page, so this does not
/// start Ghostscript or PDFium. The page keeps its own dimensions at scale 1 and is reduced only when either side would
/// exceed <see cref="SvgImage.MaxDimension"/>; the rendered PNG then obeys the same 4 MiB image ceiling as every other
/// door. A document that cannot be parsed, a page outside its range and a render that produces no bytes are refusals
/// with a sentence, never a partial image.
/// </remarks>
internal static class PdfImage
{
    /// <summary>
    /// Renders one PDF page.
    /// </summary>
    /// <param name="pdf">The document's bytes.</param>
    /// <param name="pageNumber">The one-based page to render.</param>
    /// <param name="maxBytes">The ceiling the rendered PNG must fit inside.</param>
    /// <param name="maxDimension">The largest width or height a page may have.</param>
    /// <param name="png">The rendered PNG, on success.</param>
    /// <param name="width">The rendered width in pixels.</param>
    /// <param name="height">The rendered height in pixels.</param>
    /// <param name="error">One sentence explaining a refusal, on failure.</param>
    /// <returns><see langword="true"/> when the page rendered inside the ceilings.</returns>
    public static bool TryRender(
        ReadOnlySpan<byte> pdf,
        int pageNumber,
        long maxBytes,
        int maxDimension,
        out byte[] png,
        out int width,
        out int height,
        out string error)
    {
        png = [];
        width = 0;
        height = 0;

        if (pageNumber < 1)
        {
            error = $"the page number must be 1 or greater, not {pageNumber}";
            return false;
        }

        try
        {
            using var stream = new MemoryStream(pdf.ToArray(), writable: false);
            using var document = PdfDocument.Open(stream, SkiaRenderingParsingOptions.Instance);
            if (pageNumber > document.NumberOfPages)
            {
                error = string.Create(
                    CultureInfo.InvariantCulture,
                    $"the PDF has {document.NumberOfPages} page(s); page {pageNumber} is outside it");
                return false;
            }

            document.AddSkiaPageFactory();
            var page = document.GetPage(pageNumber);
            var pageWidth = page.Width;
            var pageHeight = page.Height;
            if (pageWidth <= 0 || pageHeight <= 0)
            {
                error = "the PDF page has no usable size";
                return false;
            }

            var scale = Math.Min(1d, maxDimension / Math.Max(pageWidth, pageHeight));
            using var rendered = document.GetPageAsPng(
                pageNumber,
                (float)scale,
                unchecked((int)0xFFFFFFFF));
            if (rendered.Length == 0)
            {
                error = "the PDF page could not be rendered";
                return false;
            }

            png = rendered.ToArray();
            if (png.LongLength > maxBytes)
            {
                error = string.Create(
                    CultureInfo.InvariantCulture,
                    $"the PDF page rendering is {png.LongLength} bytes; the picture ceiling is {maxBytes} bytes");
                png = [];
                return false;
            }

            using var decoded = SKBitmap.Decode(png)
                ?? throw new InvalidOperationException("the PDF renderer returned bytes that are not a PNG");
            width = decoded.Width;
            height = decoded.Height;
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = $"the PDF could not be rendered: {ex.Message}";
            png = [];
            width = 0;
            height = 0;
            return false;
        }
    }
}
