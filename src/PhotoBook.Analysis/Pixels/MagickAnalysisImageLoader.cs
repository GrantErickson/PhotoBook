using ImageMagick;

namespace PhotoBook.Analysis.Pixels;

/// <summary>
/// The default <see cref="IAnalysisImageLoader"/>: Magick.NET-Q8 decode of the analysis copy, since
/// every pixel in the product goes through Magick.NET (kernel §10, ADR-0004). This is a package
/// dependency, not a project reference — Analysis still references only Core (doc 02 §2).
/// <para>
/// The analysis copy is written pre-adjustment, EXIF-oriented and sRGB by the thumbnail cache, so
/// the orientation and colorspace steps here are defensive no-ops for it; they matter only when a
/// caller points the loader at some other file.
/// </para>
/// </summary>
public sealed class MagickAnalysisImageLoader : IAnalysisImageLoader
{
    /// <summary>A shared instance; the loader is stateless and thread-safe.</summary>
    public static MagickAnalysisImageLoader Instance { get; } = new();

    /// <inheritdoc/>
    public Task<AnalysisImage> LoadAsync(string path, int maxLongEdge, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ct.ThrowIfCancellationRequested();
        return Task.Run(() => Load(path, maxLongEdge), ct);
    }

    /// <summary>Synchronous decode, for callers already on a worker thread.</summary>
    public static AnalysisImage Load(string path, int maxLongEdge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) throw new FileNotFoundException("The analysis copy does not exist.", path);

        using var image = new MagickImage(path);
        image.AutoOrient();
        if (image.ColorSpace != ColorSpace.sRGB) image.ColorSpace = ColorSpace.sRGB;
        if (image.HasAlpha)
        {
            // Page background is black in v1 (kernel §3), so composite transparency onto black.
            image.BackgroundColor = MagickColors.Black;
            image.Alpha(AlphaOption.Remove);
        }

        if (maxLongEdge > 0 && Math.Max(image.Width, image.Height) > (uint)maxLongEdge)
        {
            var geometry = new MagickGeometry((uint)maxLongEdge, (uint)maxLongEdge) { IgnoreAspectRatio = false };
            image.Resize(geometry);
        }

        var width = (int)image.Width;
        var height = (int)image.Height;
        using var pixels = image.GetPixels();
        var rgb = pixels.ToByteArray(PixelMapping.RGB)
                  ?? throw new InvalidOperationException($"Magick.NET returned no RGB pixels for '{path}'.");

        return new AnalysisImage(width, height, rgb);
    }
}
