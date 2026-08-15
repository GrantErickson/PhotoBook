using System.Diagnostics;
using PhotoBook.Analysis.Caching;
using PhotoBook.Analysis.Fusion;
using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;

namespace PhotoBook.Analysis.Running;

/// <summary>
/// Batch analysis: run the analyzer over a set of photos with bounded parallelism, reuse the
/// regenerable cache, fuse each result, write it through to the catalog, and finish by assigning
/// month tiers.
/// <para>
/// Where things land is the whole point (kernel §5, doc 06):
/// </para>
/// <list type="bullet">
/// <item><description><c>cache/analysis/{hash}.{analyzerId}.json</c> — the analyzer's <em>derived</em>
/// regions and raw signals. Regenerable; deleting it costs one background pass.</description></item>
/// <item><description><see cref="Photo.FocusRegions"/> and <see cref="Photo.Quality"/> — the fused
/// result, model state that belongs in <c>photos.json</c> because it contains the user's own regions
/// and must survive cache deletion.</description></item>
/// <item><description><see cref="Photo.Tier"/> — written by <see cref="TierAssigner"/> at the end of
/// the run, because a percentile needs the whole month. <see cref="Photo.UserTierOverride"/> is
/// never touched.</description></item>
/// </list>
/// <para>
/// One bad photo never aborts the batch; failures are collected and the photo keeps whatever it had.
/// Cancellation is honored between photos and inside each analyzer.
/// </para>
/// </summary>
public sealed class AnalysisRunner
{
    private readonly IImageAnalyzer _analyzer;
    private readonly IAnalysisCopyLocator _copies;
    private readonly IAnalysisCache _cache;

    /// <summary>Creates a runner.</summary>
    /// <param name="analyzer">The analyzer to drive, typically from <see cref="Composition.AnalyzerFactory"/>.</param>
    /// <param name="copies">How to obtain each photo's analysis copy.</param>
    /// <param name="cache">The analysis cache; <see cref="NullAnalysisCache"/> when null.</param>
    public AnalysisRunner(IImageAnalyzer analyzer, IAnalysisCopyLocator copies, IAnalysisCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(copies);
        _analyzer = analyzer;
        _copies = copies;
        _cache = cache ?? NullAnalysisCache.Instance;
    }

    /// <summary>The analyzer being driven.</summary>
    public IImageAnalyzer Analyzer => _analyzer;

    /// <summary>Analyzes a set of photos.</summary>
    /// <param name="source">
    /// The photos to analyze, in priority order — the caller sorts by month distance so the current
    /// Chapter finishes first (doc 06 "Performance budget"). Enumerated exactly once. The parameter is
    /// <see cref="IEnumerable{T}"/> rather than a list type so <c>catalog.Photos</c>
    /// (an <see cref="IList{T}"/>) and a LINQ projection both flow in without a copy at the call site,
    /// matching <see cref="AnalysisApplier.ApplyAll"/> and <see cref="TierAssigner.AssignAllMonths"/>.
    /// </param>
    /// <param name="options">Run configuration; <see cref="AnalysisRunOptions.Default"/> when null.</param>
    /// <param name="progress">Optional progress sink, reported once per completed photo.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="OperationCanceledException">The run was cancelled.</exception>
    public async Task<AnalysisRunResult> RunAsync(
        IEnumerable<Photo> source,
        AnalysisRunOptions? options = null,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= AnalysisRunOptions.Default;

        // Materialize once: the run indexes into the set from several threads, so it must never
        // re-enumerate a lazy sequence. A List<Photo> — what a catalog holds — passes through uncopied.
        var photos = source as IReadOnlyList<Photo> ?? [.. source];
        if (photos.Count == 0) return AnalysisRunResult.Empty;

        var stopwatch = Stopwatch.StartNew();
        var outcomes = new PhotoAnalysisOutcome[photos.Count];
        var counters = new Counters();
        var applyGate = new object();

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism),
            CancellationToken = ct,
        };

        await Parallel.ForAsync(0, photos.Count, parallelOptions, async (index, token) =>
        {
            var photo = photos[index];
            var outcome = await AnalyzeOneAsync(photo, options, counters, token).ConfigureAwait(false);
            outcomes[index] = outcome;

            if (options.ApplyToPhotos && outcome.HasResults)
            {
                lock (applyGate)
                {
                    if (AnalysisApplier.Apply(photo, outcome)) Interlocked.Increment(ref counters.Applied);
                }
            }

            var completed = Interlocked.Increment(ref counters.Completed);
            progress?.Report(new AnalysisProgress(
                completed,
                photos.Count,
                Volatile.Read(ref counters.Analyzed),
                Volatile.Read(ref counters.FromCache),
                Volatile.Read(ref counters.Skipped),
                Volatile.Read(ref counters.Failed),
                photo.Id));
        }).ConfigureAwait(false);

        var tiers = options is { AssignTiers: true, ApplyToPhotos: true }
            ? TierAssigner.AssignAllMonths(options.TierPool ?? photos)
            : new Dictionary<(int Year, int Month), TierAssignmentResult>();

        stopwatch.Stop();

        return new AnalysisRunResult(
            outcomes,
            Volatile.Read(ref counters.Analyzed),
            Volatile.Read(ref counters.FromCache),
            Volatile.Read(ref counters.Skipped),
            Volatile.Read(ref counters.Failed),
            Volatile.Read(ref counters.Applied),
            tiers,
            stopwatch.Elapsed);
    }

    private async Task<PhotoAnalysisOutcome> AnalyzeOneAsync(
        Photo photo,
        AnalysisRunOptions options,
        Counters counters,
        CancellationToken ct)
    {
        if ((options.SkipExcluded && photo.Excluded) || (options.SkipDecodeFailed && photo.DecodeFailed))
        {
            Interlocked.Increment(ref counters.Skipped);
            return new PhotoAnalysisOutcome(photo.Id, AnalysisOutcomeStatus.Skipped, null, null, null, null);
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            AnalysisResult? raw = null;
            var fromCache = false;

            if (options.UseCache)
            {
                raw = await _cache.TryReadAsync(photo.ContentHash, _analyzer.Id, _analyzer.Version, ct).ConfigureAwait(false);
                fromCache = raw is not null;
            }

            if (raw is null)
            {
                var copyPath = await _copies.GetAnalysisCopyPathAsync(photo, ct).ConfigureAwait(false);
                var input = new AnalysisInput(
                    photo.ContentHash,
                    copyPath,
                    photo.Width,
                    photo.Height,
                    photo.PersonTags as IReadOnlyList<PersonTag> ?? photo.PersonTags.ToList());

                raw = await _analyzer.AnalyzeAsync(input, ct).ConfigureAwait(false);

                if (options.WriteCache)
                {
                    // A cache write failure is never fatal: the entry is regenerable by definition.
                    try
                    {
                        await _cache.WriteAsync(photo.ContentHash, _analyzer.Id, _analyzer.Version, raw, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        options.Log?.Invoke($"Could not cache analysis for {photo.Id}: {ex.Message}");
                    }
                }
            }

            ct.ThrowIfCancellationRequested();

            IReadOnlyList<Rect>? suppressions = null;
            options.Suppressions?.TryGetValue(photo.Id, out suppressions);

            var focus = FocusFusion.FuseForPhoto(photo, raw, suppressions);
            var quality = QualityFusion.Fuse(raw.Signals, photo.PersonTags as IReadOnlyList<PersonTag> ?? photo.PersonTags.ToList());

            if (fromCache) Interlocked.Increment(ref counters.FromCache);
            else Interlocked.Increment(ref counters.Analyzed);

            return new PhotoAnalysisOutcome(
                photo.Id,
                fromCache ? AnalysisOutcomeStatus.FromCache : AnalysisOutcomeStatus.Analyzed,
                focus,
                quality,
                raw,
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref counters.Failed);
            options.Log?.Invoke($"Analysis failed for {photo.Id} ({photo.OriginalFileName}): {ex.Message}");
            return new PhotoAnalysisOutcome(photo.Id, AnalysisOutcomeStatus.Failed, null, null, null, ex.Message);
        }
    }

    private sealed class Counters
    {
        public int Completed;
        public int Analyzed;
        public int FromCache;
        public int Skipped;
        public int Failed;
        public int Applied;
    }
}
