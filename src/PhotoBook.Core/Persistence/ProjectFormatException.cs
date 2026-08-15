namespace PhotoBook.Core.Persistence;

/// <summary>
/// A project file could not be understood: malformed JSON, a missing root object, or a broken
/// migration chain. The loader never opens a guessed project — it fails with the file name.
/// </summary>
public class ProjectFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="fileName">The file that could not be read.</param>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The underlying parse error, when there is one.</param>
    public ProjectFormatException(string fileName, string message, Exception? innerException = null)
        : base($"{fileName}: {message}", innerException) => FileName = fileName;

    /// <summary>The file that could not be read.</summary>
    public string FileName { get; }
}
