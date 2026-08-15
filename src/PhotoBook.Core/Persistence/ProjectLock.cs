namespace PhotoBook.Core.Persistence;

/// <summary>
/// The single-instance guard of doc 04 §9: an exclusive handle on <c>cache/.lock</c>. A second
/// instance opening the same project gets a clear "project is open elsewhere" message; a stale lock
/// after a crash is reclaimed silently, because the handle died with the process.
/// </summary>
public sealed class ProjectLock : IDisposable
{
    private FileStream? _handle;

    private ProjectLock(FileStream handle, string path)
    {
        _handle = handle;
        Path = path;
    }

    /// <summary>The lock file path.</summary>
    public string Path { get; }

    /// <summary>
    /// Takes the project lock, or returns null when another process already holds it.
    /// </summary>
    /// <param name="paths">The project folder layout; its <c>cache/</c> folder is created if missing.</param>
    public static ProjectLock? TryAcquire(ProjectPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.CacheFolder);
        try
        {
            var handle = new FileStream(paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
            return new ProjectLock(handle, paths.LockFile);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Releases the lock.</summary>
    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }
}
