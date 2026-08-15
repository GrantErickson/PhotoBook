namespace PhotoBook.Imaging;

/// <summary>
/// What a header-only probe can tell the importer about a file, without decoding a single pixel
/// (doc 05 "Decode pipeline" step 4). Metadata is read <em>before</em> any transform, which is why the
/// raw EXIF <see cref="Orientation"/> is reported alongside the already-corrected
/// <see cref="Width"/>/<see cref="Height"/>.
/// </summary>
/// <param name="Width">Pixel width <b>after</b> EXIF orientation — the value that belongs in <c>photos.json</c>.</param>
/// <param name="Height">Pixel height <b>after</b> EXIF orientation.</param>
/// <param name="StoredWidth">Pixel width as stored in the file, before orientation.</param>
/// <param name="StoredHeight">Pixel height as stored in the file, before orientation.</param>
/// <param name="Orientation">The raw EXIF orientation tag (1–8), or null when absent.</param>
/// <param name="DateTimeOriginal">
/// EXIF <c>DateTimeOriginal</c> as unzoned local wall-clock time — priority 2 of the date chain
/// (kernel §10). Null when the tag is missing or unparseable.
/// </param>
/// <param name="SubSecondOriginal">
/// EXIF <c>SubsecTimeOriginal</c> normalized to milliseconds, for intra-second ordering of burst
/// shots. Null when absent.
/// </param>
/// <param name="CameraMake">EXIF <c>Make</c>, trimmed; null when absent.</param>
/// <param name="CameraModel">EXIF <c>Model</c>, trimmed; null when absent.</param>
/// <param name="Format">The coder that recognized the bytes, e.g. <c>Jpeg</c>, <c>Heic</c>.</param>
/// <param name="HasColorProfile">True when an embedded ICC profile is present (Display P3 HEICs, CMYK JPEGs).</param>
public sealed record ImageMetadata(
    int Width,
    int Height,
    int StoredWidth,
    int StoredHeight,
    int? Orientation,
    DateTime? DateTimeOriginal,
    int? SubSecondOriginal,
    string? CameraMake,
    string? CameraModel,
    string Format,
    bool HasColorProfile)
{
    /// <summary>True when the EXIF orientation tag implies a 90° or 270° rotation, so the axes swap.</summary>
    public bool OrientationSwapsAxes => Orientation is 5 or 6 or 7 or 8;

    /// <summary>
    /// <see cref="DateTimeOriginal"/> refined with <see cref="SubSecondOriginal"/>, which is what the
    /// importer should store as <c>takenAt</c> when EXIF wins the date chain.
    /// </summary>
    public DateTime? CaptureTimestamp => DateTimeOriginal is { } d && SubSecondOriginal is { } ms
        ? d.AddMilliseconds(ms)
        : DateTimeOriginal;

    /// <summary>Native aspect ratio (width / height) of the oriented image; NaN when unknown.</summary>
    public double Aspect => Height <= 0 ? double.NaN : (double)Width / Height;
}
