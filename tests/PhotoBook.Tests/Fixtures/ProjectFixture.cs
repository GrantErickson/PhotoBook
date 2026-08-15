using PhotoBook.Core.Model;
using PhotoBook.Core.Persistence;
using PhotoBook.Imaging;

namespace PhotoBook.Tests.Fixtures;

/// <summary>
/// A real project folder on disk — <c>book.json</c>-less but with the doc 04 §2 skeleton and archived
/// originals — plus the <see cref="ThumbnailSourceResolver"/> wiring the imaging layer expects. Tests
/// mutate <see cref="Adjustments"/> to stand in for the user moving a slider in the editor.
/// </summary>
public sealed class ProjectFixture : IDisposable
{
    private readonly TempWorkspace _workspace;
    private readonly Dictionary<string, string> _originals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageAdjustments> _adjustments = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates an empty project folder.</summary>
    /// <param name="name">A short label folded into the temp folder name.</param>
    public ProjectFixture(string name = "project")
    {
        _workspace = new TempWorkspace(name);
        Paths = new ProjectPaths(_workspace.Root);
        Paths.EnsureFolders();
    }

    /// <summary>The project's path set.</summary>
    public ProjectPaths Paths { get; }

    /// <summary>The workspace the project lives in, for scratch files outside the project folder.</summary>
    public TempWorkspace Workspace => _workspace;

    /// <summary>The resolver to hand <see cref="ThumbnailCache"/>: content hash → original plus edits.</summary>
    public ThumbnailSourceResolver Resolver => hash =>
        _originals.TryGetValue(hash, out var path)
            ? new ThumbnailSource(path, _adjustments.TryGetValue(hash, out var adj) ? adj : ImageAdjustments.Identity)
            : null;

    /// <summary>
    /// Copies an image into <c>originals/</c> under its content-hash-prefixed immutable name (doc 04 §7)
    /// and registers it with the resolver.
    /// </summary>
    /// <param name="sourcePath">The file to archive.</param>
    /// <returns>The full content hash — the cache key of everything derived from it.</returns>
    public async Task<string> ArchiveAsync(string sourcePath)
    {
        var hash = await ContentHash.OfFileAsync(sourcePath);
        var relative = ProjectPaths.OriginalRelativePath(hash, Path.GetFileName(sourcePath));
        var destination = Paths.OriginalFile(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(sourcePath, destination, overwrite: true);

        _originals[hash] = destination;
        _adjustments[hash] = ImageAdjustments.Identity;
        return hash;
    }

    /// <summary>The absolute path of an archived original.</summary>
    public string OriginalOf(string contentHash) => _originals[contentHash];

    /// <summary>Sets the photo's current edits, exactly as saving <c>photos.json</c> would.</summary>
    public void SetAdjustments(string contentHash, ImageAdjustments adjustments) =>
        _adjustments[contentHash] = adjustments;

    /// <summary>Sets the photo's current edits from the persisted Core stack.</summary>
    public void SetAdjustments(string contentHash, AdjustmentStack stack) =>
        _adjustments[contentHash] = ImageAdjustments.From(stack);

    /// <summary>Forgets a photo, which is how a hash becomes an orphan for the cache sweep.</summary>
    public void Forget(string contentHash)
    {
        _originals.Remove(contentHash);
        _adjustments.Remove(contentHash);
    }

    /// <inheritdoc/>
    public void Dispose() => _workspace.Dispose();
}
