namespace PhotoBook.Ingestion.Probing;

/// <summary>
/// What ingestion needs to know about an image file before it can write a catalog row: the oriented
/// pixel dimensions and the metadata that feeds the date chain (doc 05, "Decode pipeline" step 4 —
/// metadata is extracted <em>before</em> any transform).
/// <para>
/// This is deliberately a metadata-only shape: ingestion never needs pixels, so probing does not
/// force a full decode. A Magick.NET-backed probe from <c>PhotoBook.Imaging</c> can implement
/// <see cref="IImageProbe"/> and return the same record.
/// </para>
/// </summary>
/// <param name="Succeeded">False when the file could not be parsed at all ⇒ <c>decodeFailed</c>.</param>
/// <param name="Width">Pixel width <b>after</b> EXIF orientation is applied; 0 when unknown.</param>
/// <param name="Height">Pixel height <b>after</b> EXIF orientation is applied; 0 when unknown.</param>
/// <param name="DateTimeOriginal">EXIF <c>DateTimeOriginal</c> as unzoned local wall-clock time; null when absent.</param>
/// <param name="SubSecTimeOriginal">EXIF <c>SubSecTimeOriginal</c>, used only for intra-second ordering.</param>
/// <param name="CameraMake">EXIF <c>Make</c>, when present.</param>
/// <param name="CameraModel">EXIF <c>Model</c>, when present.</param>
/// <param name="Orientation">The raw EXIF orientation tag (1–8); 0 when absent. Already applied to the dimensions.</param>
/// <param name="FailureReason">Why the probe failed; null on success.</param>
public sealed record ImageProbeResult(
    bool Succeeded,
    int Width,
    int Height,
    DateTime? DateTimeOriginal = null,
    string? SubSecTimeOriginal = null,
    string? CameraMake = null,
    string? CameraModel = null,
    int Orientation = 0,
    string? FailureReason = null)
{
    /// <summary>A probe that could not read the file — the caller marks the photo <c>decodeFailed</c>.</summary>
    public static ImageProbeResult Failure(string reason) => new(false, 0, 0, FailureReason: reason);

    /// <summary>True when both dimensions are known and positive.</summary>
    public bool HasDimensions => Width > 0 && Height > 0;
}
