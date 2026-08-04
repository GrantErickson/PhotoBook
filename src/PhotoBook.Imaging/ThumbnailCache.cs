using ImageMagick;
using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Persistence;

namespace PhotoBook.Imaging;

/// <summary>
/// The <c>cache/thumbs/</c> implementation of <see cref="IThumbnailCache"/>: the three fixed tiers of
/// kernel §10 / doc 05, built on demand from the immutable originals.
///
/// <list type="table">
/// <listheader><term>Tier</term><description>Long edge, format, contents</description></listheader>
/// <item><term><see cref="ThumbnailTier.Grid256"/></term><description>256 px, JPEG q80, adjustments
/// applied — the Photos grid and the bins.</description></item>
/// <item><term><see cref="ThumbnailTier.Preview1024"/></term><description>1024 px, JPEG q85, adjustments
/// applied — the Pages tab render.</description></item>
/// <item><term><see cref="ThumbnailTier.Analysis1024"/></term><description>1024 px, JPEG, <b>geometry
/// only</b> — the analysis copy, so fixing a photo's exposure never re-tiers it.</description></item>
/// </list>
/// The third tier of kernel §10, full resolution, is deliberately absent: export decodes originals on
/// demand and caches nothing (doc 12).
///
/// <para><b>Keys and the invalidation matrix.</b> An artifact's file name is
/// <c>{contentHash}-{adjustmentHash}.jpg</c>, or plain <c>{contentHash}.jpg</c> when the stack is
/// identity — doc 02's <c>{contentHash}:{tier}:{adjustmentStackHash}</c> rendered onto a filesystem.
/// The adjusted tiers key on the whole stack; the analysis tier keys on
/// <see cref="ImageAdjustments.GeometryOnly"/>. The doc 05 matrix therefore falls out of the key
/// arithmetic rather than out of bookkeeping:</para>
/// <list type="bullet">
/// <item><description>A tone edit changes the two adjusted keys and leaves the analysis key alone —
/// rebuild, rebuild, keep.</description></item>
/// <item><description>A geometry edit changes all three — rebuild, rebuild, rebuild.</description></item>
/// <item><description>A re-date, a Focus Region edit, a Tier override or a <c>CropState</c> pan touch no
/// key at all — keep, keep, keep.</description></item>
/// <item><description>A re-import changes the content hash, so every key changes at once and the old
/// files become orphans.</description></item>
/// </list>
/// Superseded files are swept by <see cref="CollectOrphansAsync"/> on project open ("stale ones are
/// deleted lazily", doc 02).
///
/// <para><b>Regenerable, always.</b> Nothing here is user intent: deleting <c>cache/</c> costs decode
/// time and never data (kernel §5). <b>Concurrency:</b> builds are serialized per key by an async lock
/// and written temp-then-rename, so parallel job-queue requests coalesce into one build and a crash
/// mid-build leaves a missing key, never a torn file.</para>
/// </summary>
public sealed class ThumbnailCache : IThumbnailCache, IDisposable
{
    private readonly ProjectPaths _paths;
    private readonly ThumbnailSourceResolver _resolve;
    private readonly AdjustmentPipeline _pipeline;
    private readonly KeyedAsyncLock _locks = new();

    /// <summary>Creates a cache over a project folder.</summary>
    /// <param name="paths">The project's path set; the cache writes only under <c>cache/thumbs/</c>.</param>
    /// <param name="resolver">Turns a content hash into an original path plus the photo's adjustments.</param>
    public ThumbnailCache(ProjectPaths paths, ThumbnailSourceResolver resolver)
        : this(paths, resolver, AdjustmentPipeline.Default)
    {
    }

    /// <summary>Creates a cache with an explicit adjustment pipeline.</summary>
    /// <param name="paths">The project's path set.</param>
    /// <param name="resolver">Turns a content hash into an original path plus the photo's adjustments.</param>
    /// <param name="pipeline">The pipeline that applies edits while building.</param>
    public ThumbnailCache(ProjectPaths paths, ThumbnailSourceResolver resolver, AdjustmentPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(pipeline);
        _paths = paths;
        _resolve = resolver;
        _pipeline = pipeline;
        MagickRuntime.Ensure();
    }

    /// <summary>The three tiers this cache stores, in the order the generator should build them.</summary>
    public static IReadOnlyList<ThumbnailTier> Tiers { get; } =
        [ThumbnailTier.Preview1024, ThumbnailTier.Grid256, ThumbnailTier.Analysis1024];

    /// <inheritdoc/>
    public string PathFor(string contentHash, ThumbnailTier tier)
    {
        var hash = ContentHash.Normalize(contentHash);
        var spec = TierSpec.For(tier);
        var key = AdjustmentKey(hash, spec);
        var fileName = key.Length == 0 ? $"{hash}.jpg" : $"{hash}-{key}.jpg";
        return Path.Combine(_paths.ThumbnailTierFolder(spec.Folder), fileName);
    }

    /// <inheritdoc/>
    public bool Exists(string contentHash, ThumbnailTier tier) => File.Exists(PathFor(contentHash, tier));

    /// <inheritdoc/>
    public async Task<string> GetOrCreateAsync(string contentHash, ThumbnailTier tier, CancellationToken ct = default)
    {
        var hash = ContentHash.Normalize(contentHash);
        var spec = TierSpec.For(tier);
        var path = PathFor(hash, tier);
        if (File.Exists(path)) return path;

        ct.ThrowIfCancellationRequested();
        using var _ = await _locks.AcquireAsync(path, ct).ConfigureAwait(false);

        // Someone may have built it while we waited: that is the coalescing this lock exists for.
        if (File.Exists(path)) return path;

        await BuildAsync(hash, spec, path, ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>
    /// Builds the 1024 px preview and, in the same decode, the 256 px grid thumbnail — the generation
    /// order doc 05 prescribes for the visible month ("one decode, two writes"). The analysis copy is
    /// built too when <paramref name="includeAnalysisCopy"/> is set, since analysis is queued right
    /// behind thumbnails.
    /// </summary>
    /// <param name="contentHash">Full SHA-256 of the photo's original bytes.</param>
    /// <param name="includeAnalysisCopy">Also build <see cref="ThumbnailTier.Analysis1024"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WarmAsync(string contentHash, bool includeAnalysisCopy = true, CancellationToken ct = default)
    {
        await GetOrCreateAsync(contentHash, ThumbnailTier.Preview1024, ct).ConfigureAwait(false);
        await GetOrCreateAsync(contentHash, ThumbnailTier.Grid256, ct).ConfigureAwait(false);
        if (includeAnalysisCopy)
            await GetOrCreateAsync(contentHash, ThumbnailTier.Analysis1024, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task InvalidateAsync(string contentHash, IReadOnlyCollection<ThumbnailTier>? tiers = null)
    {
        var hash = ContentHash.Normalize(contentHash);
        var targets = tiers is null || tiers.Count == 0 ? Tiers : tiers;

        return Task.Run(() =>
        {
            foreach (var tier in targets)
            {
                var folder = _paths.ThumbnailTierFolder(TierSpec.For(tier).Folder);
                if (!Directory.Exists(folder)) continue;

                // Delete every variant of this hash, not just the current key: after an edit the current
                // key already points somewhere new, and the file we must drop is the one the old key
                // named.
                foreach (var file in Directory.EnumerateFiles(folder, hash + "*"))
                {
                    AtomicBinaryFile.TryDelete(file);
                }
            }
        });
    }

    /// <inheritdoc/>
    public Task<int> CollectOrphansAsync(IReadOnlyCollection<string> liveContentHashes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(liveContentHashes);
        var live = new HashSet<string>(liveContentHashes.Select(ContentHash.Normalize), StringComparer.Ordinal);

        return Task.Run(
            () =>
            {
                var removed = 0;
                foreach (var tier in Tiers)
                {
                    var spec = TierSpec.For(tier);
                    var folder = _paths.ThumbnailTierFolder(spec.Folder);
                    if (!Directory.Exists(folder)) continue;

                    foreach (var file in Directory.EnumerateFiles(folder))
                    {
                        ct.ThrowIfCancellationRequested();

                        // A stray temp file is by definition an incomplete write (doc 04 §6).
                        if (ProjectPaths.IsTransient(file))
                        {
                            if (AtomicBinaryFile.TryDelete(file)) removed++;
                            continue;
                        }

                        var name = Path.GetFileNameWithoutExtension(file);
                        var dash = name.IndexOf('-');
                        var hash = dash < 0 ? name : name[..dash];

                        // The photo left the catalog: every artifact of it goes.
                        if (!live.Contains(hash))
                        {
                            if (AtomicBinaryFile.TryDelete(file)) removed++;
                            continue;
                        }

                        // The photo is live but this file was built from adjustments it no longer has —
                        // the superseded entry an edit left behind.
                        var source = _resolve(hash);
                        if (source is null) continue;
                        var currentKey = AdjustmentHash.Compute(spec.AppliesTone
                            ? source.Adjustments
                            : source.Adjustments.GeometryOnly);
                        var fileKey = dash < 0 ? string.Empty : name[(dash + 1)..];
                        if (!string.Equals(fileKey, currentKey, StringComparison.Ordinal))
                        {
                            if (AtomicBinaryFile.TryDelete(file)) removed++;
                        }
                    }
                }

                return removed;
            },
            ct);
    }

    /// <inheritdoc/>
    public void Dispose() => _locks.Dispose();

    private string AdjustmentKey(string hash, TierSpec spec)
    {
        var source = _resolve(hash);
        if (source is null) return AdjustmentHash.IdentityKey;
        return AdjustmentHash.Compute(spec.AppliesTone ? source.Adjustments : source.Adjustments.GeometryOnly);
    }

    private async Task BuildAsync(string hash, TierSpec spec, string destination, CancellationToken ct)
    {
        var source = _resolve(hash)
                     ?? throw new InvalidOperationException(
                         $"No photo with content hash '{hash}' is registered, so its {spec.Folder} thumbnail cannot be built.");

        if (!File.Exists(source.OriginalPath))
            throw new FileNotFoundException(
                $"The archived original for content hash '{hash}' is missing.", source.OriginalPath);

        var adjustments = (spec.AppliesTone ? source.Adjustments : source.Adjustments.GeometryOnly).Normalized();

        // The 256 is normally downsampled from the 1024 that was just built — one small decode instead
        // of a second full one, and the two tiers cannot disagree because they came from the same pixels.
        var derivedFrom = spec.Tier == ThumbnailTier.Grid256 ? PathFor(hash, ThumbnailTier.Preview1024) : null;
        var canDerive = derivedFrom is not null && File.Exists(derivedFrom);

        var decodePath = canDerive ? derivedFrom! : source.OriginalPath;
        var applyAdjustments = !canDerive && !adjustments.IsIdentity;

        // Straightening crops the frame down, so decode with headroom and do the exact fit afterwards.
        var decodeHint = applyAdjustments && adjustments.HasGeometry
            ? (int)(spec.LongEdge * 1.5)
            : spec.LongEdge;

        using var image = await MagickPipeline.LoadOrientedAsync(decodePath, decodeHint, ct).ConfigureAwait(false);
        if (applyAdjustments) _pipeline.Apply(image, adjustments, ct);
        MagickPipeline.ResizeToLongEdge(image, spec.LongEdge);
        ct.ThrowIfCancellationRequested();

        // "1024 before 256 for the visible month … one decode, two writes" (doc 05).
        var alsoGrid = spec.Tier == ThumbnailTier.Preview1024 ? PathFor(hash, ThumbnailTier.Grid256) : null;
        using var gridCopy = alsoGrid is not null && !File.Exists(alsoGrid)
            ? (MagickImage)image.Clone()
            : null;

        var bytes = ImageEncoder.EncodeJpeg(image, spec.Quality);
        await AtomicBinaryFile.WriteAllBytesAsync(destination, bytes, ct).ConfigureAwait(false);

        if (gridCopy is not null && alsoGrid is not null)
        {
            var gridSpec = TierSpec.For(ThumbnailTier.Grid256);
            MagickPipeline.ResizeToLongEdge(gridCopy, gridSpec.LongEdge);
            var gridBytes = ImageEncoder.EncodeJpeg(gridCopy, gridSpec.Quality);
            await AtomicBinaryFile.WriteAllBytesAsync(alsoGrid, gridBytes, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The fixed per-tier constants of kernel §10 / doc 05.</summary>
    /// <param name="Tier">The tier this describes.</param>
    /// <param name="Folder">Sub-folder under <c>cache/thumbs/</c>.</param>
    /// <param name="LongEdge">Long-edge pixel size.</param>
    /// <param name="Quality">JPEG quality.</param>
    /// <param name="AppliesTone">
    /// Whether the exposure/color/finish stages are baked in. False for the analysis copy, which gets the
    /// geometry stage only.
    /// </param>
    private sealed record TierSpec(ThumbnailTier Tier, string Folder, int LongEdge, int Quality, bool AppliesTone)
    {
        internal static TierSpec For(ThumbnailTier tier) => tier switch
        {
            ThumbnailTier.Grid256 => new TierSpec(tier, "256", 256, ImageEncoder.GridThumbnailQuality, true),
            ThumbnailTier.Preview1024 => new TierSpec(tier, "1024", 1024, ImageEncoder.PreviewThumbnailQuality, true),
            ThumbnailTier.Analysis1024 => new TierSpec(tier, "1024a", 1024, 90, false),
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown thumbnail tier."),
        };
    }
}
