namespace PhotoBook.Ingestion.Probing;

/// <summary>
/// The file types a photo source may hand to the importer (doc 05, "Local-folder import"):
/// <c>.jpg .jpeg .png .heic .heif .webp .tif .tiff .bmp</c>. Anything else is listed in the Import
/// Report as skipped rather than silently ignored.
/// </summary>
public static class SupportedImageFormats
{
    /// <summary>The accepted extensions, lowercase and dot-prefixed.</summary>
    public static IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".webp", ".tif", ".tiff", ".bmp",
    };

    /// <summary>True when the file name carries one of the <see cref="Extensions"/>.</summary>
    public static bool IsSupported(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var ext = Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(ext) && Extensions.Contains(ext);
    }

    /// <summary>The extension in the canonical lowercase form, or an empty string.</summary>
    public static string NormalizedExtension(string fileName) =>
        string.IsNullOrWhiteSpace(fileName) ? string.Empty : Path.GetExtension(fileName).ToLowerInvariant();
}
