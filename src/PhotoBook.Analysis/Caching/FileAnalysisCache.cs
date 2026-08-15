using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Persistence;

namespace PhotoBook.Analysis.Caching;

/// <summary>
/// The file-backed <see cref="IAnalysisCache"/> over <c>cache/analysis/</c>. Writes go through
/// <see cref="AtomicFile"/> and serialization through <see cref="ProjectJson"/>, so cache entries
/// obey the same atomic-write and camelCase rules as every other project file (kernel §5).
/// <para>
/// Reads are forgiving by design: a missing, truncated or unparseable entry is simply a cache miss.
/// The cache can always be recomputed, so it never surfaces an error and never blocks analysis.
/// </para>
/// </summary>
public sealed class FileAnalysisCache : IAnalysisCache
{
    private readonly string _folder;

    /// <summary>Creates a cache over an explicit folder.</summary>
    /// <param name="analysisCacheFolder">Absolute path of <c>cache/analysis/</c>.</param>
    public FileAnalysisCache(string analysisCacheFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(analysisCacheFolder);
        _folder = Path.GetFullPath(analysisCacheFolder);
    }

    /// <summary>Creates a cache over a project's <c>cache/analysis/</c> folder.</summary>
    /// <param name="paths">The open project's path set.</param>
    public FileAnalysisCache(ProjectPaths paths)
        : this((paths ?? throw new ArgumentNullException(nameof(paths))).AnalysisCacheFolder)
    {
    }

    /// <summary>The folder entries are stored in.</summary>
    public string Folder => _folder;

    /// <summary>The absolute path of one entry, whether or not it exists.</summary>
    /// <param name="contentHash">Full SHA-256 of the photo's original bytes.</param>
    /// <param name="analyzerId">The analyzer's stable id.</param>
    public string PathFor(string contentHash, string analyzerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(analyzerId);
        return Path.Combine(_folder, $"{contentHash.Trim().ToLowerInvariant()}.{analyzerId}.json");
    }

    /// <inheritdoc/>
    public async Task<AnalysisResult?> TryReadAsync(string contentHash, string analyzerId, string analyzerVersion, CancellationToken ct = default)
    {
        var path = PathFor(contentHash, analyzerId);
        if (!File.Exists(path)) return null;

        try
        {
            var json = await AtomicFile.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var document = ProjectJson.Deserialize<AnalysisCacheDocument>(json, Path.GetFileName(path));
            if (!string.Equals(document.AnalyzerVersion, analyzerVersion, StringComparison.Ordinal)) return null;
            return document.ToResult();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A damaged cache entry is a cache miss, never an error: the whole folder is regenerable.
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task WriteAsync(string contentHash, string analyzerId, string analyzerVersion, AnalysisResult result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var path = PathFor(contentHash, analyzerId);
        var document = AnalysisCacheDocument.From(analyzerId, analyzerVersion, result);
        var json = ProjectJson.Serialize(document);

        Directory.CreateDirectory(_folder);
        await AtomicFile.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task InvalidateAsync(string contentHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        if (!Directory.Exists(_folder)) return Task.CompletedTask;

        var prefix = contentHash.Trim().ToLowerInvariant() + ".";
        foreach (var file in Directory.EnumerateFiles(_folder, "*.json"))
        {
            var name = Path.GetFileName(file);
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            TryDelete(file);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<int> CollectOrphansAsync(IReadOnlyCollection<string> liveContentHashes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(liveContentHashes);
        if (!Directory.Exists(_folder)) return Task.FromResult(0);

        var live = new HashSet<string>(liveContentHashes.Select(h => h.Trim().ToLowerInvariant()), StringComparer.Ordinal);
        var removed = 0;

        foreach (var file in Directory.EnumerateFiles(_folder, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            if (ProjectPaths.IsTransient(file)) continue;

            var name = Path.GetFileName(file);
            var dot = name.IndexOf('.');
            if (dot <= 0) continue;

            var hash = name[..dot].ToLowerInvariant();
            if (live.Contains(hash)) continue;
            if (TryDelete(file)) removed++;
        }

        return Task.FromResult(removed);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
