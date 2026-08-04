using PhotoBook.Imaging;
using SkiaSharp;

namespace PhotoBook.Rendering;

/// <summary>
/// A rendered page preview: the pixels, the diagnostics, and the geometry they were drawn with.
/// </summary>
/// <param name="Image">
/// Tightly packed BGRA8888 pixels — the same layout as <c>PhotoBook.Imaging</c>'s
/// <see cref="DecodedImage"/>, so the WPF shell can blit straight into a <c>WriteableBitmap</c> of
/// format <c>Bgra32</c> with a single <c>WritePixels</c> call.
/// </param>
/// <param name="Result">Diagnostics and the device rects of every slot, for hit testing.</param>
/// <param name="Geometry">The mapper the page was drawn with — the editor hit-tests against this.</param>
public sealed record PagePreview(DecodedImage Image, PageRenderResult Result, PageGeometryMapper Geometry);

/// <summary>
/// Renders a page into an off-screen buffer for the editor.
/// <para>
/// This is <b>not</b> a second renderer. It creates a raster surface, hands the canvas to the one
/// <see cref="PageRenderer"/>, and copies the result out — the identical draw code that produces the
/// PDF (ADR-0003). A preview and the corresponding PDF page differ only in resolution and in the
/// editor-only amber flags.
/// </para>
/// </summary>
public static class PagePreviewRenderer
{
    /// <summary>
    /// Renders a page scaled to fit a buffer of the given pixel size.
    /// </summary>
    /// <param name="request">
    /// The page to draw. Its <see cref="PageRenderRequest.Geometry"/> supplies the print profile, the
    /// page size and the surface; the scale and origin are recomputed to fit the buffer.
    /// </param>
    /// <param name="pixelWidth">Buffer width in pixels.</param>
    /// <param name="pixelHeight">Buffer height in pixels.</param>
    /// <param name="background">
    /// What to clear the buffer to before drawing — the editor's canvas color behind the sheet.
    /// Transparent by default so the shell can composite the page over its own background.
    /// </param>
    public static PagePreview Render(
        PageRenderRequest request, int pixelWidth, int pixelHeight, SKColor background = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelHeight, 1);

        var geometry = PageGeometryMapper.Fit(
            request.Geometry.Profile,
            request.Geometry.PageSize.Id,
            SKRect.Create(0, 0, pixelWidth, pixelHeight),
            request.Geometry.Surface);

        return RenderExact(request with { Geometry = geometry }, pixelWidth, pixelHeight, background);
    }

    /// <summary>
    /// Renders at exactly the scale and origin the request's mapper already carries — for callers
    /// that manage their own zoom and scroll offsets.
    /// </summary>
    /// <param name="request">The page to draw, with the geometry to draw it at.</param>
    /// <param name="pixelWidth">Buffer width in pixels.</param>
    /// <param name="pixelHeight">Buffer height in pixels.</param>
    /// <param name="background">Buffer clear color.</param>
    public static PagePreview RenderExact(
        PageRenderRequest request, int pixelWidth, int pixelHeight, SKColor background = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelHeight, 1);

        var opaque = background.Alpha == 255;
        var info = new SKImageInfo(
            pixelWidth, pixelHeight, SKColorType.Bgra8888,
            opaque ? SKAlphaType.Opaque : SKAlphaType.Premul,
            SKColorSpace.CreateSrgb());

        // Drawing straight into a bitmap keeps the buffer tightly packed at width × 4 bytes per row,
        // which is exactly what DecodedImage and a Bgra32 WriteableBitmap both expect.
        using var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(background);
        var result = PageRenderer.Render(canvas, request);
        canvas.Flush();

        if (bitmap.RowBytes != pixelWidth * DecodedImage.BytesPerPixel)
            throw new InvalidOperationException("Skia allocated a padded bitmap; the preview buffer would not be tightly packed.");

        var decoded = new DecodedImage(bitmap.Bytes, pixelWidth, pixelHeight, opaque, alphaIsPremultiplied: true);
        return new PagePreview(decoded, result, request.Geometry);
    }
}
