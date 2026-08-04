namespace PhotoBook.Core.Abstractions;

/// <summary>
/// Derived pixels under <c>cache/</c> (doc 05, doc 04 §8). Everything this interface owns is
/// <b>100% regenerable</b>: deleting <c>cache/</c> must lose nothing, and no implementation may ever
/// store a user decision here — user intent never lives in cache.
/// <para>Contract rules:</para>
/// <list type="bullet">
/// <item><description>Every artifact is keyed by the photo's full content hash plus the tier, so a
/// re-imported photo gets clean caches by construction rather than by bookkeeping.</description></item>
/// <item><description>Writes are atomic (temp file plus rename) like every other project write; a
/// crash mid-rebuild leaves a missing key, and missing keys regenerate lazily.</description></item>
/// <item><description>Implementations are thread-safe: the background job queue drives them at
/// parallelism, and two requests for the same key must produce one build, not two files.</description></item>
/// <item><description>Export never reads this cache — it decodes originals at full resolution (doc 12).</description></item>
/// <item><description>The layout engine never sees this interface (kernel §12).</description></item>
/// </list>
/// </summary>
public interface IThumbnailCache
{
    /// <summary>The absolute path a tier's artifact would live at, whether or not it exists yet.</summary>
    /// <param name="contentHash">Full SHA-256 of the photo's original bytes.</param>
    /// <param name="tier">Which tier.</param>
    string PathFor(string contentHash, ThumbnailTier tier);

    /// <summary>True when the artifact is already built.</summary>
    /// <param name="contentHash">Full SHA-256 of the photo's original bytes.</param>
    /// <param name="tier">Which tier.</param>
    bool Exists(string contentHash, ThumbnailTier tier);

    /// <summary>
    /// Returns the artifact's path, building it from the archived original if necessary. Concurrent
    /// requests for the same key coalesce into one build.
    /// </summary>
    /// <param name="contentHash">Full SHA-256 of the photo's original bytes.</param>
    /// <param name="tier">Which tier.</param>
    /// <param name="ct">Cancellation token; a cancelled build leaves no partial file.</param>
    Task<string> GetOrCreateAsync(string contentHash, ThumbnailTier tier, CancellationToken ct = default);

    /// <summary>
    /// Drops cached artifacts for one photo so they rebuild on next request — the deletion side of the
    /// cache invalidation matrix (doc 05). Tone edits invalidate the two adjusted tiers only; geometry
    /// edits invalidate the analysis copy as well.
    /// </summary>
    /// <param name="contentHash">Full SHA-256 of the photo's original bytes.</param>
    /// <param name="tiers">The tiers to drop; null or empty drops every tier.</param>
    Task InvalidateAsync(string contentHash, IReadOnlyCollection<ThumbnailTier>? tiers = null);

    /// <summary>
    /// Deletes artifacts whose content hash is no longer in the catalog — the orphan collection run on
    /// project open (doc 06).
    /// </summary>
    /// <param name="liveContentHashes">Every content hash still present in <c>photos.json</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of files removed.</returns>
    Task<int> CollectOrphansAsync(IReadOnlyCollection<string> liveContentHashes, CancellationToken ct = default);
}
