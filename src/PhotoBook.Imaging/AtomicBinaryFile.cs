using PhotoBook.Core.Persistence;

namespace PhotoBook.Imaging;

/// <summary>
/// The binary counterpart of <see cref="AtomicFile"/>: write a temp file, flush it, then rename it into
/// place, so a reader can never observe a half-written thumbnail and a crash mid-build leaves a missing
/// key rather than a corrupt one (kernel §5, doc 05).
/// <para>
/// Unlike the project-JSON writer there is no <c>.bak</c>: cache artifacts are 100% regenerable, so the
/// previous version of a thumbnail is worth exactly nothing. The temp name carries a unique token, so
/// two writers racing on the same key each write their own temp and the rename picks a winner —
/// whichever wins, the bytes are complete and identical.
/// </para>
/// </summary>
internal static class AtomicBinaryFile
{
    /// <summary>Writes bytes to <paramref name="path"/> atomically, creating the directory if needed.</summary>
    /// <param name="path">Destination file.</param>
    /// <param name="bytes">Content to write.</param>
    /// <param name="ct">Cancellation token; a cancelled write leaves no partial file at the destination.</param>
    internal static async Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bytes);

        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = $"{full}.{Guid.NewGuid():N}{ProjectPaths.TempSuffix}";
        try
        {
            await using (var stream = new FileStream(
                             temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Deletes a file, swallowing the "someone else got there first" races.</summary>
    /// <param name="path">The file to delete.</param>
    /// <returns>True when the file no longer exists.</returns>
    internal static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return false;
        }
    }
}
