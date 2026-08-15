using System.Runtime.CompilerServices;
using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;
using PhotoBook.Ingestion.Probing;

namespace PhotoBook.Ingestion;

/// <summary>
/// The local-folder photo source (doc 05, "Local-folder import"): the no-cloud path of R1 and the
/// offline fallback that always works. It recursively enumerates a folder for the supported
/// extensions and hands bytes to <see cref="PhotoImporter"/>, which owns hashing, copying and
/// cataloguing — a source never writes anything, anywhere (<see cref="IPhotoSource"/> contract).
/// <para>
/// Identity across re-scans is the content hash: a local folder has no stable item id, so a moved or
/// renamed file is recognized as the same photo by its bytes, and re-scanning is safe by
/// construction (doc 04 §7).
/// </para>
/// </summary>
public sealed class FolderPhotoSource : IPhotoSource, ISkipReportingPhotoSource
{
    private readonly List<SkippedSourceFile> _skipped = [];
    private readonly Lock _skippedGate = new();

    /// <summary>Binds the source to a folder.</summary>
    /// <param name="folderPath">The folder to scan; it must exist when enumeration runs.</param>
    /// <param name="recursive">Scan subfolders too — the default, with a toggle in the picker (doc 05).</param>
    public FolderPhotoSource(string folderPath, bool recursive = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        FolderPath = Path.GetFullPath(folderPath);
        Recursive = recursive;
    }

    /// <summary>The absolute folder being scanned.</summary>
    public string FolderPath { get; }

    /// <summary>Whether subfolders are included.</summary>
    public bool Recursive { get; }

    /// <inheritdoc/>
    public BookSourceKind Kind => BookSourceKind.LocalFolder;

    /// <inheritdoc/>
    public string DisplayName => FolderPath;

    /// <summary>
    /// Files that were passed over during the most recent enumeration because their extension is not
    /// in <see cref="SupportedImageFormats.Extensions"/>. The importer copies these into the Import
    /// Report so a skipped RAW or MOV is visible rather than silently missing (doc 05).
    /// </summary>
    public IReadOnlyList<SkippedSourceFile> SkippedFiles
    {
        get
        {
            lock (_skippedGate) return _skipped.ToArray();
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<SourcePhoto> EnumerateAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        lock (_skippedGate) _skipped.Clear();

        if (!Directory.Exists(FolderPath))
            throw new DirectoryNotFoundException($"The photo folder '{FolderPath}' does not exist.");

        // Paths are materialized and sorted so a re-scan visits files in the same order every time;
        // stat calls stay lazy, so enumeration still streams for the caller.
        var paths = EnumerateCandidatePaths();

        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(path);
            if (!SupportedImageFormats.IsSupported(fileName))
            {
                lock (_skippedGate) _skipped.Add(new SkippedSourceFile(path, "Unsupported file type."));
                continue;
            }

            SourcePhoto photo;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;                       // vanished between listing and stat
                photo = new SourcePhoto(
                    SourceId: path,
                    FileName: fileName,
                    SizeBytes: info.Length,
                    LastModifiedUtc: info.LastWriteTimeUtc);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lock (_skippedGate) _skipped.Add(new SkippedSourceFile(path, ex.Message));
                continue;
            }

            yield return photo;
            await Task.Yield();                                   // keep the UI responsive on huge folders
        }
    }

    /// <inheritdoc/>
    public Task<Stream> OpenReadAsync(SourcePhoto photo, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ct.ThrowIfCancellationRequested();

        Stream stream = new FileStream(photo.SourceId, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    private List<string> EnumerateCandidatePaths()
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = Recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        var paths = new List<string>();
        foreach (var path in Directory.EnumerateFiles(FolderPath, "*", options))
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith('.')) continue;                   // dot-files are never photos
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            {
                continue;                                         // transient siblings of an atomic write
            }

            paths.Add(path);
        }

        paths.Sort(StringComparer.OrdinalIgnoreCase);
        return paths;
    }
}
