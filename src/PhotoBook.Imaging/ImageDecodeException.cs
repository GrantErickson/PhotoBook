namespace PhotoBook.Imaging;

/// <summary>
/// A file could not be read as an image: truncated, an unsupported subformat, or not an image at all.
/// <para>
/// Callers are expected to catch this per photo, mark the catalog entry <c>decodeFailed: true</c> and
/// list it in the Import Report — "one bad file never aborts an import batch" (doc 05).
/// </para>
/// </summary>
public sealed class ImageDecodeException : Exception
{
    /// <summary>Creates the exception for a file.</summary>
    /// <param name="path">The file that failed.</param>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The underlying decoder failure, if any.</param>
    public ImageDecodeException(string path, string message, Exception? innerException = null)
        : base(message, innerException) => Path = path;

    /// <summary>The file that failed to decode.</summary>
    public string Path { get; }
}
