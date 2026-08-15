using PhotoBook.Imaging;

namespace PhotoBook.Ingestion.Probing;

/// <summary>
/// The default <see cref="IImageProbe"/>: <c>PhotoBook.Imaging</c>'s Magick.NET header probe
/// (ADR-0004 — one decode backend for the whole app, libheif bundled so HEIC needs no OS codecs),
/// with <see cref="ImageMetadataProbe"/> as a fallback.
/// <para>
/// The fallback exists because import must never be blocked by the imaging backend: if the native
/// Magick runtime cannot start, or a file trips the decoder, ingestion still reads dimensions and
/// EXIF <c>DateTimeOriginal</c> from the container itself and the photo lands in the catalog with an
/// honest date instead of a <c>decodeFailed</c> flag.
/// </para>
/// </summary>
public sealed class ImageDecoderProbe : IImageProbe
{
    private readonly Lazy<IImageDecoder?> _decoder;
    private readonly IImageProbe _fallback;

    /// <summary>Creates a probe that lazily constructs the shared Magick.NET decoder.</summary>
    public ImageDecoderProbe() : this(null, null)
    {
    }

    /// <summary>Creates a probe over an explicit decoder.</summary>
    /// <param name="decoder">The decoder to probe with; null constructs the default one on first use.</param>
    /// <param name="fallback">The probe used when the decoder is unavailable or fails; defaults to <see cref="ImageMetadataProbe"/>.</param>
    public ImageDecoderProbe(IImageDecoder? decoder, IImageProbe? fallback = null)
    {
        _fallback = fallback ?? ImageMetadataProbe.Instance;
        _decoder = decoder is not null
            ? new Lazy<IImageDecoder?>(() => decoder)
            : new Lazy<IImageDecoder?>(TryCreateDecoder, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>A shared instance; probing is stateless and thread-safe.</summary>
    public static ImageDecoderProbe Default { get; } = new();

    /// <inheritdoc/>
    public ImageProbeResult Probe(string filePath, string originalFileName)
    {
        var decoder = _decoder.Value;
        if (decoder is not null)
        {
            try
            {
                var metadata = decoder.ProbeAsync(filePath).GetAwaiter().GetResult();
                if (metadata.Width > 0 && metadata.Height > 0) return Convert(metadata);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fall through: a file the decoder rejects may still have a readable container header,
                // and one bad file must never abort an import batch (doc 05).
                _ = ex;
            }
        }

        return _fallback.Probe(filePath, originalFileName);
    }

    private static ImageProbeResult Convert(ImageMetadata metadata) => new(
        Succeeded: true,
        Width: metadata.Width,
        Height: metadata.Height,
        DateTimeOriginal: metadata.CaptureTimestamp,
        SubSecTimeOriginal: metadata.SubSecondOriginal?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        CameraMake: metadata.CameraMake,
        CameraModel: metadata.CameraModel,
        Orientation: metadata.Orientation ?? 0);

    private static IImageDecoder? TryCreateDecoder()
    {
        try
        {
            return new ImageDecoder();
        }
        catch (Exception)
        {
            // No native Magick runtime on this machine: the container probe carries the import.
            return null;
        }
    }
}
