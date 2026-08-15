using PhotoBook.Core.Model;

namespace PhotoBook.Imaging;

/// <summary>
/// The decode half of <c>PhotoBook.Imaging</c> (doc 02: registered as a singleton alongside
/// <see cref="Core.Abstractions.IThumbnailCache"/>). Two operations, matching the two things callers
/// actually need: cheap header-only metadata during import, and pixels.
/// <para>
/// Every implementation guarantees the doc 05 contract: EXIF orientation applied exactly once at
/// decode, colors normalized to sRGB, originals opened read-only.
/// </para>
/// </summary>
public interface IImageDecoder
{
    /// <summary>The file extensions this decoder accepts, lowercase and dot-prefixed.</summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }

    /// <summary>True when the path's extension is one this decoder accepts.</summary>
    /// <param name="path">A file path.</param>
    bool CanDecode(string path);

    /// <summary>
    /// Reads dimensions, EXIF capture time, camera make/model and orientation from the file header
    /// without decoding pixels — what the importer needs to catalog a photo and run the date chain.
    /// </summary>
    /// <param name="path">Absolute path of the image file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ImageDecodeException">The file is unreadable or not a supported image.</exception>
    Task<ImageMetadata> ProbeAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Decodes to BGRA8888 pixels: oriented, sRGB, no adjustments applied. This is the analysis and
    /// "source of truth" view of a photo.
    /// </summary>
    /// <param name="path">Absolute path of the image file.</param>
    /// <param name="maxLongEdge">Downsample so the long edge is at most this; 0 decodes at native size.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ImageDecodeException">The file could not be decoded.</exception>
    Task<DecodedImage> DecodeAsync(string path, int maxLongEdge = 0, CancellationToken ct = default);

    /// <summary>
    /// Decodes and applies the photo's non-destructive edits in one pass — the viewable pixels of
    /// doc 05 step 5.
    /// </summary>
    /// <param name="path">Absolute path of the image file.</param>
    /// <param name="adjustments">The photo's edits; identity behaves exactly like <see cref="DecodeAsync"/>.</param>
    /// <param name="maxLongEdge">Downsample so the long edge is at most this; 0 decodes at native size.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ImageDecodeException">The file could not be decoded.</exception>
    Task<DecodedImage> DecodeAdjustedAsync(
        string path, ImageAdjustments? adjustments, int maxLongEdge = 0, CancellationToken ct = default);

    /// <summary>Convenience overload taking the catalog's persisted stack.</summary>
    /// <param name="path">Absolute path of the image file.</param>
    /// <param name="stack">The photo's stored adjustments.</param>
    /// <param name="maxLongEdge">Downsample so the long edge is at most this; 0 decodes at native size.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DecodedImage> DecodeAdjustedAsync(
        string path, AdjustmentStack? stack, int maxLongEdge = 0, CancellationToken ct = default);
}
