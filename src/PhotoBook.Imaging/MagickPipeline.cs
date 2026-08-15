using ImageMagick;

namespace PhotoBook.Imaging;

/// <summary>
/// The single decode path of doc 05 — "every pixel in the app flows through one function". Read,
/// auto-orient, normalize to sRGB, optionally downsample. Everything public in this assembly is built
/// on these four steps, so orientation and color are handled in exactly one place.
/// <para>
/// Kept internal on purpose: <see cref="MagickImage"/> holds native memory and must never escape into
/// the rest of the app, which sees <see cref="DecodedImage"/> and cache file paths instead
/// (ADR-0004: "exactly one conversion seam to the renderer").
/// </para>
/// </summary>
internal static class MagickPipeline
{
    /// <summary>Resampling filter for every downsample in the app — thumbnails, previews and export alike.</summary>
    internal const FilterType ResampleFilter = FilterType.Lanczos;

    /// <summary>
    /// Reads a file into an oriented, sRGB <see cref="MagickImage"/>. The caller owns and must dispose
    /// the result.
    /// </summary>
    /// <param name="path">Absolute path of the image file.</param>
    /// <param name="maxLongEdge">
    /// When positive, a decode-time size hint plus a Lanczos downsample so the result's long edge is at
    /// most this many pixels. Zero decodes at native size.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    internal static async Task<MagickImage> LoadOrientedAsync(string path, int maxLongEdge, CancellationToken ct)
    {
        MagickRuntime.Ensure();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ct.ThrowIfCancellationRequested();

        var image = new MagickImage();
        try
        {
            await image.ReadAsync(path, ReadSettingsFor(maxLongEdge), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // Orientation is applied exactly once, here, and reset — nothing downstream rotates again
            // (kernel §10). AutoOrient clears the tag as part of the operation.
            image.AutoOrient();

            NormalizeToSrgb(image);
            ct.ThrowIfCancellationRequested();

            if (maxLongEdge > 0) ResizeToLongEdge(image, maxLongEdge);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Transforms embedded-profile pixels into sRGB and drops the profile, so the rest of the pipeline —
    /// screen, thumbnails, PDF — is unconditionally sRGB (doc 05 step 3). Profile-less images are
    /// assumed sRGB, which is the correct guess for consumer photos.
    /// </summary>
    internal static void NormalizeToSrgb(MagickImage image)
    {
        var profile = image.GetColorProfile();
        if (profile is not null)
        {
            if (profile.ColorSpace != ColorSpace.sRGB) image.TransformColorSpace(ColorProfiles.SRGB);
        }
        else if (image.ColorSpace == ColorSpace.CMYK)
        {
            // A CMYK JPEG with no profile: assume the usual press profile rather than emitting inverted
            // garbage. Rare, but "one bad file never aborts an import batch" (doc 05).
            image.TransformColorSpace(ColorProfiles.USWebCoatedSWOP, ColorProfiles.SRGB);
        }

        if (image.ColorSpace != ColorSpace.sRGB) image.ColorSpace = ColorSpace.sRGB;

        // Metadata is read before this ever runs (ImageDecoder.ProbeAsync), so stripping here loses
        // nothing and keeps cache JPEGs small and free of a now-wrong orientation tag.
        image.Strip();
    }

    /// <summary>Downsamples so the long edge is at most <paramref name="longEdge"/>. Never upscales.</summary>
    internal static void ResizeToLongEdge(MagickImage image, int longEdge)
    {
        if (longEdge <= 0) return;
        var currentLongEdge = Math.Max(image.Width, image.Height);
        if (currentLongEdge <= (uint)longEdge) return;

        image.FilterType = ResampleFilter;
        var geometry = new MagickGeometry((uint)longEdge, (uint)longEdge) { Greater = true };
        image.Resize(geometry);
    }

    /// <summary>
    /// The one conversion seam to the renderer: native pixels to a tightly packed BGRA8888 buffer
    /// (ADR-0004).
    /// </summary>
    internal static DecodedImage ToDecodedImage(MagickImage image)
    {
        var opaque = !image.HasAlpha;
        if (opaque)
        {
            // BGRA extraction needs a real alpha channel; make it explicit and fully opaque.
            image.Alpha(AlphaOption.Opaque);
        }

        using var pixels = image.GetPixels();
        var buffer = pixels.ToByteArray(PixelMapping.BGRA)
                     ?? throw new InvalidOperationException("ImageMagick returned no pixels for a decoded image.");

        return new DecodedImage(buffer, (int)image.Width, (int)image.Height, opaque);
    }

    /// <summary>
    /// The inverse seam: wraps an existing BGRA8888 buffer as a <see cref="MagickImage"/> so the
    /// adjustment pipeline can re-run on an already-decoded preview without touching the disk.
    /// </summary>
    internal static MagickImage FromDecodedImage(DecodedImage source)
    {
        MagickRuntime.Ensure();
        ArgumentNullException.ThrowIfNull(source);

        var settings = new PixelReadSettings(
            (uint)source.Width, (uint)source.Height, StorageType.Char, PixelMapping.BGRA);
        var image = new MagickImage();
        try
        {
            image.ReadPixels(source.Pixels, settings);
            image.ColorSpace = ColorSpace.sRGB;

            // Reading BGRA always materializes an alpha channel. Dropping it again for an opaque photo
            // keeps the round trip lossless in both directions and lets operators that treat alpha as a
            // blend mask (vibrance) take their fast path.
            if (source.IsOpaque) image.Alpha(AlphaOption.Off);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private static MagickReadSettings? ReadSettingsFor(int maxLongEdge)
    {
        if (maxLongEdge <= 0) return null;

        var settings = new MagickReadSettings();

        // DCT-scaled JPEG decode: ImageMagick picks the cheapest 1/2, 1/4 or 1/8 scale that still
        // covers the requested box, which is the difference between minutes and seconds when
        // thumbnailing a full year of photos. The Lanczos resize afterwards does the exact fit.
        var box = $"{maxLongEdge}x{maxLongEdge}";
        settings.SetDefine(MagickFormat.Jpeg, "size", box);
        settings.SetDefine(MagickFormat.Heic, "size", box);
        settings.SetDefine(MagickFormat.Heif, "size", box);
        return settings;
    }
}
