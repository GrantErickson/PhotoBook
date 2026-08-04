using PhotoBook.Core.Abstractions;

namespace PhotoBook.Analysis.Caching;

/// <summary>
/// The regenerable half of analysis output: derived regions and raw signals under
/// <c>cache/analysis/{contentHash}.{analyzerId}.json</c> (doc 06).
/// <para>
/// What is cached: what an analyzer measured — expensive to compute, safe to lose. What is
/// <b>never</b> cached: fused focus regions the user edited, suppressions, <c>tier</c>,
/// <c>userTierOverride</c> and person tags. Those are user intent and live in <c>photos.json</c>
/// (kernel §5: user intent never lives in cache). Deleting <c>cache/</c> costs one background
/// re-analysis pass and loses nothing the user did.
/// </para>
/// </summary>
public interface IAnalysisCache
{
    /// <summary>
    /// Reads a cached result, or null when it is absent or stale. Staleness is by key mismatch:
    /// the analyzer id is in the file name and the analyzer version is inside the file, so shipping
    /// new models invalidates entries without any bookkeeping.
    /// </summary>
    /// <param name="contentHash">Full SHA-256 of the photo's original bytes.</param>
    /// <param name="analyzerId">The analyzer's stable id.</param>
    /// <param name="analyzerVersion">The analyzer's version.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<AnalysisResult?> TryReadAsync(string contentHash, string analyzerId, string analyzerVersion, CancellationToken ct = default);

    /// <summary>Writes a result atomically (temp file plus rename), like every other project write.</summary>
    /// <param name="contentHash">Full SHA-256 of the photo's original bytes.</param>
    /// <param name="analyzerId">The analyzer's stable id.</param>
    /// <param name="analyzerVersion">The analyzer's version.</param>
    /// <param name="result">What the analyzer returned.</param>
    /// <param name="ct">Cancellation token; a cancelled write leaves no partial file.</param>
    Task WriteAsync(string contentHash, string analyzerId, string analyzerVersion, AnalysisResult result, CancellationToken ct = default);

    /// <summary>Deletes every cached result for one photo — the re-analysis side of doc 05's invalidation matrix.</summary>
    /// <param name="contentHash">Full SHA-256 of the photo's original bytes.</param>
    Task InvalidateAsync(string contentHash);

    /// <summary>Deletes entries whose content hash is no longer in the catalog (the project-open orphan sweep).</summary>
    /// <param name="liveContentHashes">Every content hash still present in <c>photos.json</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of files removed.</returns>
    Task<int> CollectOrphansAsync(IReadOnlyCollection<string> liveContentHashes, CancellationToken ct = default);
}
