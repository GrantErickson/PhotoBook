using ImageMagick;

namespace PhotoBook.Imaging;

/// <summary>
/// The source formats the decode pipeline accepts (doc 05 "Local-folder import", R1). Format support
/// is a capability of this module, not of the domain model (doc 01) — adding a format touches this
/// file and nothing else.
/// <para>
/// HEIC/HEIF/AVIF work with no OS codec packs because Magick.NET-Q8 bundles libheif (ADR-0004).
/// </para>
/// </summary>
public static class ImageFormats
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".png", ".webp", ".heic", ".heif", ".avif", ".tif", ".tiff", ".bmp", ".gif",
    };

    /// <summary>Every accepted file extension, lowercase and dot-prefixed.</summary>
    public static IReadOnlyCollection<string> SupportedExtensions { get; } =
        Extensions.OrderBy(e => e, StringComparer.Ordinal).ToArray();

    /// <summary>True when the extension is one the decoder accepts. Case-insensitive.</summary>
    /// <param name="pathOrExtension">A file path or a bare extension, with or without the leading dot.</param>
    public static bool IsSupported(string? pathOrExtension)
    {
        if (string.IsNullOrWhiteSpace(pathOrExtension)) return false;
        var extension = pathOrExtension.Contains('.', StringComparison.Ordinal) &&
                        !pathOrExtension.StartsWith('.')
            ? Path.GetExtension(pathOrExtension)
            : pathOrExtension;
        if (string.IsNullOrEmpty(extension)) return false;
        if (!extension.StartsWith('.')) extension = "." + extension;
        return Extensions.Contains(extension);
    }

    /// <summary>
    /// The ImageMagick coder implied by a file extension, or <see cref="MagickFormat.Unknown"/> when the
    /// extension is not one of ours. Only a hint: the decoder still sniffs the bytes, which is what
    /// saves photos whose extension lies (a <c>.jpg</c> that is really a HEIC).
    /// </summary>
    public static MagickFormat FormatHint(string? pathOrExtension) =>
        (Path.GetExtension(pathOrExtension ?? string.Empty) is { Length: > 0 } ext ? ext : pathOrExtension ?? string.Empty)
        .ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".jpe" => MagickFormat.Jpeg,
            ".png" => MagickFormat.Png,
            ".webp" => MagickFormat.WebP,
            ".heic" => MagickFormat.Heic,
            ".heif" => MagickFormat.Heif,
            ".avif" => MagickFormat.Avif,
            ".tif" or ".tiff" => MagickFormat.Tiff,
            ".bmp" => MagickFormat.Bmp,
            ".gif" => MagickFormat.Gif,
            _ => MagickFormat.Unknown,
        };
}
