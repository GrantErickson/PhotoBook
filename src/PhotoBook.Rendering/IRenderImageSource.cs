using SkiaSharp;

namespace PhotoBook.Rendering;

/// <summary>
/// A request for a photo's pixels at a resolution.
/// </summary>
/// <param name="PhotoId">The <see cref="PhotoBook.Core.Model.Photo.Id"/> to fetch.</param>
/// <param name="MinLongEdgePx">
/// The smallest long edge that still prints at full quality for this placement, computed by the
/// renderer from the destination size and the crop window. <c>0</c> means "whatever tier is natural
/// for the target". A source must never upsample to satisfy it (doc 12 "Image handling" step 3).
/// </param>
/// <param name="Target">Which output is being drawn — the screen preview tier or the export tier.</param>
public readonly record struct RenderImageRequest(string PhotoId, int MinLongEdgePx, RenderTarget Target);

/// <summary>
/// Pixels for one photo, plus the dimensions of the original it came from.
/// <para>
/// The <see cref="Image"/> is owned by the <see cref="IRenderImageSource"/> that produced it and must
/// not be disposed by the renderer — sources cache and reuse decoded images across pages.
/// </para>
/// </summary>
/// <param name="Image">The decoded pixels, already EXIF-oriented and adjustment-applied.</param>
/// <param name="SourcePixelWidth">Width of the full-resolution original, for effective-DPI reporting.</param>
/// <param name="SourcePixelHeight">Height of the full-resolution original.</param>
public sealed record RenderImage(SKImage Image, int SourcePixelWidth, int SourcePixelHeight)
{
    /// <summary>Width of the pixels actually supplied.</summary>
    public int PixelWidth => Image.Width;

    /// <summary>Height of the pixels actually supplied.</summary>
    public int PixelHeight => Image.Height;

    /// <summary>
    /// How much smaller the supplied pixels are than the original — <c>1.0</c> for a full-resolution
    /// decode, <c>&lt; 1</c> for a thumbnail tier.
    /// </summary>
    public double TierScale => SourcePixelWidth <= 0 ? 1.0 : (double)PixelWidth / SourcePixelWidth;
}

/// <summary>
/// How the renderer gets pixels for a photo id — the seam that lets one draw path serve both outputs
/// (ADR-0003). The screen preview hands back the 1024 px layout tier (kernel §10) while PDF export
/// hands back a full-resolution, adjustment-applied decode; the drawing code above cannot tell the
/// difference, because crop math is expressed in ratios (kernel §4).
/// <para>
/// Implementations are called from the render loop and are therefore <b>synchronous</b>. Use
/// <see cref="PrepareAsync"/> to warm everything a page needs before rendering it, so the draw itself
/// never blocks on I/O.
/// </para>
/// </summary>
public interface IRenderImageSource
{
    /// <summary>
    /// The pixels for a photo, or <c>null</c> when the photo is unknown or cannot be decoded — in
    /// which case the renderer records a diagnostic and leaves the slot showing the page background.
    /// </summary>
    RenderImage? GetImage(RenderImageRequest request);

    /// <summary>Warms the given requests so a subsequent <see cref="GetImage"/> is a cache hit.</summary>
    Task PrepareAsync(IEnumerable<RenderImageRequest> requests, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// An image source that supplies nothing — every slot renders as an empty slot. Useful for geometry
/// tests, for preflight passes that need no pixels, and as a safe default.
/// </summary>
public sealed class EmptyRenderImageSource : IRenderImageSource
{
    /// <summary>The shared instance.</summary>
    public static EmptyRenderImageSource Instance { get; } = new();

    /// <inheritdoc/>
    public RenderImage? GetImage(RenderImageRequest request) => null;
}
