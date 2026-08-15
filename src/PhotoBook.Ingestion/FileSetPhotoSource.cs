using System.Runtime.CompilerServices;
using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;
using PhotoBook.Ingestion.Probing;

namespace PhotoBook.Ingestion;

/// <summary>
/// An explicit list of local files as a photo source — what drag-and-drop onto the Photos tab funnels
/// into (doc 05: "Drag-and-drop of files/folders onto the Photos tab funnels into the same importer").
/// Folders in the dropped set are expanded recursively, so a mixed drop of files and folders is one
/// source and one Import Report.
/// <para>Behaviorally identical to <see cref="FolderPhotoSource"/> — same copy-in, same hash identity,
/// same excluded-stays-excluded rule — it just starts from a list instead of a tree.</para>
/// </summary>
public sealed class FileSetPhotoSource : IPhotoSource, ISkipReportingPhotoSource
{
    private readonly List<SkippedSourceFile> _skipped = [];
    private readonly Lock _skippedGate = new();
    private readonly string[] _paths;
    private readonly bool _recursive;

    /// <summary>Binds the source to a set of dropped paths.</summary>
    /// <param name="paths">Files and/or folders; folders are expanded.</param>
    /// <param name="displayName">What to call this source in the UI; null builds one from the paths.</param>
    /// <param name="recursive">Expand dropped folders recursively.</param>
    public FileSetPhotoSource(IEnumerable<string> paths, string? displayName = null, bool recursive = true)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _recursive = recursive;
        DisplayName = displayName ?? (_paths.Length == 1
            ? _paths[0]
            : $"{_paths.Length} dropped items");
    }

    /// <inheritdoc/>
    public BookSourceKind Kind => BookSourceKind.LocalFolder;

    /// <inheritdoc/>
    public string DisplayName { get; }

    /// <inheritdoc/>
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

        foreach (var path in Expand())
        {
            ct.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(path);
            if (!SupportedImageFormats.IsSupported(fileName))
            {
                lock (_skippedGate) _skipped.Add(new SkippedSourceFile(path, "Unsupported file type."));
                continue;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists) continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lock (_skippedGate) _skipped.Add(new SkippedSourceFile(path, ex.Message));
                continue;
            }

            yield return new SourcePhoto(path, fileName, info.Length, info.LastWriteTimeUtc);
            await Task.Yield();
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

    private List<string> Expand()
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = _recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };

        var files = new List<string>();
        foreach (var path in _paths)
        {
            if (Directory.Exists(path)) files.AddRange(Directory.EnumerateFiles(path, "*", options));
            else if (File.Exists(path)) files.Add(path);
            else
            {
                lock (_skippedGate) _skipped.Add(new SkippedSourceFile(path, "The file no longer exists."));
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }
}
