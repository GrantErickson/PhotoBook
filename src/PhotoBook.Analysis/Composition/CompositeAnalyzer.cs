using PhotoBook.Core.Abstractions;

namespace PhotoBook.Analysis.Composition;

/// <summary>
/// A primary analyzer with the model-free analyzer underneath it as a safety net. Doc 06 already
/// describes this shape for the Azure adapter ("Azure for regions, local NIMA + classical metrics
/// for signals"); the same composition is what keeps the local pipeline useful when only some model
/// files are installed, and what guarantees that a failing analyzer degrades instead of leaving a
/// photo unanalyzed (doc 02: "Analysis failures degrade gracefully").
/// <para>
/// The composite keeps the primary's <see cref="Id"/> so the cache key stays
/// <c>{hash}.{analyzerId}.json</c> as specified, and combines both versions so a change on either
/// side re-keys the cache.
/// </para>
/// </summary>
public sealed class CompositeAnalyzer : IImageAnalyzer, IDisposable
{
    private readonly IImageAnalyzer _primary;
    private readonly IImageAnalyzer _fallback;
    private readonly bool _ownsChildren;
    private readonly Action<string>? _log;
    private bool _disposed;

    /// <summary>Composes two analyzers.</summary>
    /// <param name="primary">The preferred analyzer; its id names the composite.</param>
    /// <param name="fallback">The always-available analyzer used to fill gaps and absorb failures.</param>
    /// <param name="ownsChildren">Whether disposing the composite disposes both children.</param>
    /// <param name="log">Optional diagnostics sink.</param>
    public CompositeAnalyzer(IImageAnalyzer primary, IImageAnalyzer fallback, bool ownsChildren = true, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(fallback);
        _primary = primary;
        _fallback = fallback;
        _ownsChildren = ownsChildren;
        _log = log;
    }

    /// <inheritdoc/>
    public string Id => _primary.Id;

    /// <inheritdoc/>
    public string Version => $"{_primary.Version}+{_fallback.Version}";

    /// <summary>The preferred analyzer.</summary>
    public IImageAnalyzer Primary => _primary;

    /// <summary>The analyzer that fills gaps and absorbs failures.</summary>
    public IImageAnalyzer Fallback => _fallback;

    /// <inheritdoc/>
    public async Task<AnalysisResult> AnalyzeAsync(AnalysisInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ObjectDisposedException.ThrowIf(_disposed, this);

        AnalysisResult? primary = null;
        try
        {
            primary = await _primary.AnalyzeAsync(input, ct).ConfigureAwait(false);
            if (IsComplete(primary)) return primary;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Analyzer '{_primary.Id}' failed on {input.ContentHash}: {ex.Message}. Falling back to '{_fallback.Id}'.");
        }

        var fallback = await _fallback.AnalyzeAsync(input, ct).ConfigureAwait(false);
        return primary is null ? fallback : Merge(primary, fallback);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_ownsChildren) return;
        (_primary as IDisposable)?.Dispose();
        (_fallback as IDisposable)?.Dispose();
    }

    /// <summary>A result is complete when it proposes at least one region and carries non-zero signals.</summary>
    private static bool IsComplete(AnalysisResult result) =>
        result.Regions.Count > 0 &&
        result.Signals.Aesthetic > 0 &&
        result.Signals.Sharpness > 0;

    /// <summary>
    /// Takes the primary's contribution wherever it has one and the fallback's everywhere else.
    /// Face counts only ever come from the primary — the model-free analyzer has none to give.
    /// </summary>
    private static AnalysisResult Merge(AnalysisResult primary, AnalysisResult fallback) =>
        new(
            primary.Regions.Count > 0 ? primary.Regions : fallback.Regions,
            new RawQualitySignals(
                primary.Signals.Aesthetic > 0 ? primary.Signals.Aesthetic : fallback.Signals.Aesthetic,
                primary.Signals.Sharpness > 0 ? primary.Signals.Sharpness : fallback.Signals.Sharpness,
                primary.Signals.Exposure > 0 ? primary.Signals.Exposure : fallback.Signals.Exposure,
                primary.Signals.FaceCount,
                primary.Signals.LargestFaceArea));
}
