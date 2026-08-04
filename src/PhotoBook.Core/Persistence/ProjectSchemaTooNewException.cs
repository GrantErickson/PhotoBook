namespace PhotoBook.Core.Persistence;

/// <summary>
/// A newer PhotoBook wrote this project. The app must open it read-only and say so rather than
/// guessing forward (doc 04 §5).
/// </summary>
public sealed class ProjectSchemaTooNewException : ProjectFormatException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="fileName">The file that is too new.</param>
    /// <param name="fileVersion">The version found in the file.</param>
    /// <param name="supportedVersion">The highest version this build understands.</param>
    public ProjectSchemaTooNewException(string fileName, int fileVersion, int supportedVersion)
        : base(fileName, $"schemaVersion {fileVersion} was written by a newer PhotoBook; this build understands up to {supportedVersion}. Open the project read-only.")
    {
        FileVersion = fileVersion;
        SupportedVersion = supportedVersion;
    }

    /// <summary>The version found in the file.</summary>
    public int FileVersion { get; }

    /// <summary>The highest version this build understands.</summary>
    public int SupportedVersion { get; }
}
