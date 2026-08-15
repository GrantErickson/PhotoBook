using System.Text;

namespace PhotoBook.Core.Persistence;

/// <summary>
/// The atomic save protocol of doc 04 §6: write a temp file, flush it to disk, then swap it in so a
/// reader can never observe a torn file. The previous good version is demoted to a rolling
/// <c>.bak</c> in the same step, so every project file always has its last-known-good predecessor
/// beside it.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/> atomically, leaving the previous
    /// version as <c>path.bak</c>.
    /// </summary>
    /// <param name="path">The destination file.</param>
    /// <param name="contents">The text to write, UTF-8 without BOM.</param>
    /// <param name="ct">Cancellation token; a cancelled write leaves the destination untouched.</param>
    public static async Task WriteAllTextAsync(string path, string contents, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = ProjectPaths.TempPathFor(path);
        var backup = ProjectPaths.BackupPathFor(path);

        var bytes = Utf8NoBom.GetBytes(contents);
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        Swap(temp, path, backup);
    }

    /// <summary>Synchronous counterpart of <see cref="WriteAllTextAsync"/>, for shutdown paths that cannot await.</summary>
    public static void WriteAllText(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = ProjectPaths.TempPathFor(path);
        var backup = ProjectPaths.BackupPathFor(path);

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = Utf8NoBom.GetBytes(contents);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        Swap(temp, path, backup);
    }

    /// <summary>Reads a file as UTF-8 text.</summary>
    public static Task<string> ReadAllTextAsync(string path, CancellationToken ct = default) =>
        File.ReadAllTextAsync(path, Utf8NoBom, ct);

    /// <summary>
    /// Deletes stray <c>*.tmp</c> siblings in a folder. A temp file is by definition an incomplete
    /// write, so project open discards them before reading anything (doc 04 §6).
    /// </summary>
    /// <param name="folder">The folder to sweep; missing folders are ignored.</param>
    /// <param name="recursive">Whether to sweep subfolders too.</param>
    /// <returns>The paths that were deleted.</returns>
    public static IReadOnlyList<string> DeleteStrayTempFiles(string folder, bool recursive = false)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return Array.Empty<string>();

        var deleted = new List<string>();
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var file in Directory.EnumerateFiles(folder, "*" + ProjectPaths.TempSuffix, option))
        {
            try
            {
                File.Delete(file);
                deleted.Add(file);
            }
            catch (IOException)
            {
                // A temp file still held open by another process is not ours to clean up.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return deleted;
    }

    private static void Swap(string temp, string path, string backup)
    {
        if (!File.Exists(path))
        {
            File.Move(temp, path, overwrite: true);
            return;
        }

        try
        {
            // NTFS swaps atomically and demotes the previous good version to .bak in one step.
            File.Replace(temp, path, backup, ignoreMetadataErrors: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Some filesystems (and sync engines holding a handle) refuse Replace; fall back to
            // copy-then-move, which keeps the same .bak guarantee with one extra copy.
            File.Copy(path, backup, overwrite: true);
            File.Move(temp, path, overwrite: true);
        }
    }
}
