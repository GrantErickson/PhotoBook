using System.Globalization;
using ImageMagick;
using PhotoBook.Core.Model;

namespace PhotoBook.Imaging;

/// <summary>
/// The Magick.NET decode pipeline of doc 05: read → auto-orient → sRGB → (adjust) → pixels.
///
/// <para>
/// jpg, png, webp, heic, heif, avif, tiff, bmp and gif all decode here, on a clean Windows box with no
/// OS codec packs, because Magick.NET-Q8 bundles libheif (ADR-0004). Formats are sniffed from the
/// bytes, so a HEIC that someone renamed to <c>.jpg</c> still opens.
/// </para>
///
/// <para>
/// Every method opens the file read-only and never writes: originals are immutable from the moment
/// they land in <c>originals/</c> (kernel §5).
/// </para>
///
/// <para>
/// The methods are async for their file IO but the decode itself is CPU-bound native work that runs on
/// the calling thread. Call them from the background job queue (doc 09), which is where the app's
/// ingestion, thumbnail and analysis work already lives.
/// </para>
/// </summary>
public sealed class ImageDecoder : IImageDecoder
{
    private readonly AdjustmentPipeline _adjustments;

    /// <summary>Creates a decoder using the shared adjustment pipeline.</summary>
    public ImageDecoder() : this(AdjustmentPipeline.Default)
    {
    }

    /// <summary>Creates a decoder with an explicit adjustment pipeline.</summary>
    /// <param name="adjustments">The pipeline used by <see cref="DecodeAdjustedAsync(string, ImageAdjustments, int, CancellationToken)"/>.</param>
    public ImageDecoder(AdjustmentPipeline adjustments)
    {
        ArgumentNullException.ThrowIfNull(adjustments);
        _adjustments = adjustments;
        MagickRuntime.Ensure();
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<string> SupportedExtensions => ImageFormats.SupportedExtensions;

    /// <inheritdoc/>
    public bool CanDecode(string path) => ImageFormats.IsSupported(path);

    /// <inheritdoc/>
    public async Task<ImageMetadata> ProbeAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        MagickRuntime.Ensure();
        ct.ThrowIfCancellationRequested();

        try
        {
            using var image = new MagickImage();

            // Ping reads the header and metadata only — no raster is ever allocated, which is what makes
            // cataloging 2,000 photos cheap.
            await image.PingAsync(path, ct).ConfigureAwait(false);
            return ReadMetadata(image);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException)
        {
            throw new ImageDecodeException(path, $"Could not read image metadata from '{path}'.", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<DecodedImage> DecodeAsync(string path, int maxLongEdge = 0, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ct.ThrowIfCancellationRequested();

        try
        {
            using var image = await MagickPipeline.LoadOrientedAsync(path, maxLongEdge, ct).ConfigureAwait(false);
            return MagickPipeline.ToDecodedImage(image);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException)
        {
            throw new ImageDecodeException(path, $"Could not decode '{path}'.", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<DecodedImage> DecodeAdjustedAsync(
        string path, ImageAdjustments? adjustments, int maxLongEdge = 0, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ct.ThrowIfCancellationRequested();

        try
        {
            return await _adjustments.ApplyToFileAsync(path, adjustments, maxLongEdge, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException)
        {
            throw new ImageDecodeException(path, $"Could not decode '{path}'.", ex);
        }
    }

    /// <inheritdoc/>
    public Task<DecodedImage> DecodeAdjustedAsync(
        string path, AdjustmentStack? stack, int maxLongEdge = 0, CancellationToken ct = default) =>
        DecodeAdjustedAsync(path, ImageAdjustments.From(stack), maxLongEdge, ct);

    private static ImageMetadata ReadMetadata(MagickImage image)
    {
        var storedWidth = (int)image.Width;
        var storedHeight = (int)image.Height;

        var exif = image.GetExifProfile();

        // The tag, else what the coder itself reported; 0 means "undefined", which is not an orientation.
        var orientation = Orientation(exif?.GetValue(ExifTag.Orientation)?.Value) ?? Orientation((int)image.Orientation);

        // The catalog stores oriented dimensions, because that is what every downstream consumer sees
        // once AutoOrient has run at decode (doc 05 step 2).
        var swaps = orientation is 5 or 6 or 7 or 8;
        var width = swaps ? storedHeight : storedWidth;
        var height = swaps ? storedWidth : storedHeight;

        var make = Clean(exif?.GetValue(ExifTag.Make)?.Value);
        var model = Clean(exif?.GetValue(ExifTag.Model)?.Value);
        var taken = ParseExifDateTime(exif?.GetValue(ExifTag.DateTimeOriginal)?.Value)
                    ?? ParseExifDateTime(exif?.GetValue(ExifTag.DateTimeDigitized)?.Value);
        var subSecond = taken is null ? null : ParseSubSeconds(exif?.GetValue(ExifTag.SubsecTimeOriginal)?.Value);

        var hasProfile = image.GetProfile("icc") is not null || image.GetProfile("icm") is not null;

        return new ImageMetadata(
            width, height, storedWidth, storedHeight, orientation,
            taken, subSecond, make, model, image.Format.ToString(), hasProfile);
    }

    private static int? Orientation(int? value) => value is > 0 and <= 8 ? value : null;

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim().Trim('\0');
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// Parses the EXIF <c>yyyy:MM:dd HH:mm:ss</c> form as unzoned local wall-clock time. EXIF carries no
    /// timezone and a family book cares about the date on the calendar where the photo was taken
    /// (doc 05), so no zone math happens here or anywhere else.
    /// </summary>
    internal static DateTime? ParseExifDateTime(string? value)
    {
        var text = Clean(value);
        if (text is null) return null;

        string[] formats =
        [
            "yyyy:MM:dd HH:mm:ss",
            "yyyy:MM:dd HH:mm:ss.FFF",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy:MM:dd",
        ];

        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None | DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            // A camera that never had its clock set writes 0000:00:00; treat that as "no date".
            return parsed.Year < 1900 ? null : parsed;
        }

        return null;
    }

    /// <summary>
    /// Normalizes EXIF <c>SubsecTimeOriginal</c> (a fraction of a second written as digits) to
    /// milliseconds: "12" is 120 ms, "007" is 7 ms. Burst shots share a second, and this is what orders
    /// them.
    /// </summary>
    internal static int? ParseSubSeconds(string? value)
    {
        var text = Clean(value);
        if (text is null) return null;

        Span<char> digits = stackalloc char[3];
        var count = 0;
        foreach (var c in text)
        {
            if (!char.IsAsciiDigit(c)) break;
            if (count == digits.Length) break;
            digits[count++] = c;
        }

        if (count == 0) return null;
        for (var i = count; i < digits.Length; i++) digits[i] = '0';
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var ms) ? ms : null;
    }
}
