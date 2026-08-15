using PhotoBook.Core.Model;

namespace PhotoBook.Core.Abstractions;

/// <summary>
/// The perception plug-in seam (kernel §10, doc 06, ADR-0010). <c>LocalOnnxAnalyzer</c> is the default
/// and works fully offline; <c>AzureVisionAnalyzer</c> is an optional, opt-in adapter behind the same
/// contract, selected per book by <see cref="Book.Analysis"/>.
/// <para>Contract rules, all normative:</para>
/// <list type="bullet">
/// <item><description><b>Analyzers detect; they do not decide.</b> Fusion, weighting, percentiles, tier
/// assignment and every user-override rule live outside the analyzer.</description></item>
/// <item><description>All rects are normalized image coordinates <c>[0,1] × [0,1]</c>, origin top-left,
/// on the oriented image — the same convention as <see cref="FocusRegion"/> and
/// <see cref="PersonTag.RegionRect"/>.</description></item>
/// <item><description>Implementations are <b>pure with respect to the project</b>: they read the
/// analysis copy, return a value, and never touch <c>photos.json</c>. Persisting to
/// <c>cache/analysis/</c> is the caller's job.</description></item>
/// <item><description><see cref="AnalyzeAsync"/> must be safe to call concurrently at parallelism
/// <c>Environment.ProcessorCount / 2</c>; model sessions are shared and created once per app run.</description></item>
/// <item><description>Cancellation is honored between model runs; a cancelled analysis writes nothing
/// and throws <see cref="OperationCanceledException"/>.</description></item>
/// <item><description>Determinism: identical input bytes plus identical <see cref="Version"/> must
/// produce identical output, so layout stays reproducible end to end (kernel §7).</description></item>
/// </list>
/// <para>
/// The layout engine never sees this interface — analysis results reach it precomputed on
/// <see cref="Photo"/>, which keeps the engine pure and I/O-free (kernel §12).
/// </para>
/// </summary>
public interface IImageAnalyzer
{
    /// <summary>
    /// Stable analyzer id — <c>"local-onnx"</c> or <c>"azure-vision"</c>. Part of every cache key
    /// (<c>cache/analysis/{contentHash}.{analyzerId}.json</c>), so switching analyzers invalidates
    /// results by key mismatch rather than by bookkeeping.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Model-bundle or API version, e.g. <c>"1.0"</c>. Bumping it makes every cached result stale,
    /// which is exactly how a model upgrade is rolled out.
    /// </summary>
    string Version { get; }

    /// <summary>Analyzes one photo's analysis copy.</summary>
    /// <param name="input">The analysis copy and its metadata.</param>
    /// <param name="ct">Cancellation token; honored between model runs.</param>
    /// <returns>Region proposals and raw quality signals for fusion.</returns>
    /// <exception cref="OperationCanceledException">The analysis was cancelled.</exception>
    Task<AnalysisResult> AnalyzeAsync(AnalysisInput input, CancellationToken ct = default);
}
