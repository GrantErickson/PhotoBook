using ImageMagick;

namespace PhotoBook.Imaging;

/// <summary>
/// Encodes decoded pixels back to bytes. Two formats, because two things need them: JPEG for the
/// thumbnail cache (q80 at 256 px, q85 at 1024 px, doc 05) and for export (q90 into the PDF,
/// kernel §3), and PNG for the lossless cases.
/// <para>
/// File writes go through <see cref="AtomicBinaryFile"/>, so nothing downstream can read a torn image.
/// </para>
/// </summary>
public interface IImageEncoder
{
    /// <summary>Encodes to JPEG bytes.</summary>
    /// <param name="image">The pixels to encode.</param>
    /// <param name="quality">JPEG quality, 1–100.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<byte[]> EncodeJpegAsync(DecodedImage image, int quality = ImageEncoder.DefaultJpegQuality, CancellationToken ct = default);

    /// <summary>Encodes to PNG bytes.</summary>
    /// <param name="image">The pixels to encode.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<byte[]> EncodePngAsync(DecodedImage image, CancellationToken ct = default);

    /// <summary>Encodes to JPEG and writes it atomically.</summary>
    /// <param name="image">The pixels to encode.</param>
    /// <param name="path">Destination file.</param>
    /// <param name="quality">JPEG quality, 1–100.</param>
    /// <param name="ct">Cancellation token.</param>
    Task WriteJpegAsync(DecodedImage image, string path, int quality = ImageEncoder.DefaultJpegQuality, CancellationToken ct = default);

    /// <summary>Encodes to PNG and writes it atomically.</summary>
    /// <param name="image">The pixels to encode.</param>
    /// <param name="path">Destination file.</param>
    /// <param name="ct">Cancellation token.</param>
    Task WritePngAsync(DecodedImage image, string path, CancellationToken ct = default);
}

/// <inheritdoc cref="IImageEncoder"/>
public sealed class ImageEncoder : IImageEncoder
{
    /// <summary>Export quality: JPEG-in-PDF at 90 (kernel §3).</summary>
    public const int DefaultJpegQuality = 90;

    /// <summary>Quality of the 256 px grid tier (doc 05).</summary>
    public const int GridThumbnailQuality = 80;

    /// <summary>Quality of the 1024 px layout-preview tier (doc 05).</summary>
    public const int PreviewThumbnailQuality = 85;

    /// <summary>A shared instance; the encoder is stateless and thread-safe.</summary>
    public static ImageEncoder Default { get; } = new();

    /// <summary>Creates an encoder.</summary>
    public ImageEncoder() => MagickRuntime.Ensure();

    /// <inheritdoc/>
    public Task<byte[]> EncodeJpegAsync(DecodedImage image, int quality = DefaultJpegQuality, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ct.ThrowIfCancellationRequested();
        using var magick = MagickPipeline.FromDecodedImage(image);
        return Task.FromResult(EncodeJpeg(magick, quality));
    }

    /// <inheritdoc/>
    public Task<byte[]> EncodePngAsync(DecodedImage image, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ct.ThrowIfCancellationRequested();
        using var magick = MagickPipeline.FromDecodedImage(image);
        return Task.FromResult(EncodePng(magick));
    }

    /// <inheritdoc/>
    public async Task WriteJpegAsync(
        DecodedImage image, string path, int quality = DefaultJpegQuality, CancellationToken ct = default)
    {
        var bytes = await EncodeJpegAsync(image, quality, ct).ConfigureAwait(false);
        await AtomicBinaryFile.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task WritePngAsync(DecodedImage image, string path, CancellationToken ct = default)
    {
        var bytes = await EncodePngAsync(image, ct).ConfigureAwait(false);
        await AtomicBinaryFile.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Encodes a live Magick image to JPEG, skipping the round trip through a managed pixel buffer.
    /// Used by the thumbnail cache, which never needs the pixels in .NET.
    /// </summary>
    internal static byte[] EncodeJpeg(MagickImage image, int quality)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(quality, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quality, 100);

        // JPEG has no alpha: flatten onto the book's black page background (R21) rather than letting
        // the coder invent a color.
        if (image.HasAlpha) image.ColorAlpha(MagickColors.Black);

        image.Strip();
        image.ColorSpace = ColorSpace.sRGB;
        image.Quality = (uint)quality;
        image.Settings.Interlace = Interlace.NoInterlace;

        // Full chroma above 90 (export), 4:2:0 below (cache thumbnails, where nobody can tell).
        image.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", quality >= 90 ? "1x1,1x1,1x1" : "2x2,1x1,1x1");

        return image.ToByteArray(MagickFormat.Jpeg);
    }

    /// <summary>Encodes a live Magick image to PNG, preserving alpha.</summary>
    internal static byte[] EncodePng(MagickImage image)
    {
        image.Strip();
        image.Settings.SetDefine(MagickFormat.Png, "compression-level", "6");
        return image.ToByteArray(MagickFormat.Png);
    }
}
