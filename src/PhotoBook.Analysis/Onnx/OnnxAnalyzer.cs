using PhotoBook.Analysis.Classical;
using PhotoBook.Analysis.Pixels;
using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;

namespace PhotoBook.Analysis.Onnx;

/// <summary>
/// Doc 06's local pipeline — YuNet faces, U2-Netp saliency, NIMA aesthetics — over ONNX Runtime's
/// CPU execution provider, <b>activated only when the model files are actually present</b> in the
/// <c>models/</c> folder (see <see cref="OnnxModelSet"/> for the exact file names).
/// <para>
/// The models cannot be bundled, so absence is the normal state, not an error: a missing file
/// disables its stage and nothing else. With no files at all the analyzer reports
/// <see cref="IsAvailable"/> = false and still returns valid results by running the model-free
/// pipeline — it never throws, and it never leaves a photo unanalyzed.
/// </para>
/// <para>
/// Sessions are created once and shared; ONNX Runtime sessions are thread-safe, so
/// <see cref="AnalyzeAsync"/> is safe to run at the job queue's parallelism (doc 06).
/// </para>
/// </summary>
public sealed class OnnxAnalyzer : IImageAnalyzer, IDisposable
{
    private readonly OnnxAnalyzerOptions _options;
    private readonly IAnalysisImageLoader _loader;
    private readonly YuNetFaceDetector? _faces;
    private readonly U2NetSaliencyDetector? _saliency;
    private readonly NimaAestheticScorer? _aesthetics;
    private readonly SaliencyRegionShape _regionShape;
    private bool _disposed;

    /// <summary>
    /// Creates the analyzer, loading whichever models are installed. Never throws for missing or
    /// unloadable files — those are reported through <see cref="OnnxAnalyzerOptions.Log"/> and
    /// leave the corresponding stage disabled.
    /// </summary>
    /// <param name="options">Configuration; <see cref="OnnxAnalyzerOptions.Default"/> when null.</param>
    public OnnxAnalyzer(OnnxAnalyzerOptions? options = null)
    {
        _options = options ?? OnnxAnalyzerOptions.Default;
        _loader = _options.ImageLoader ?? MagickAnalysisImageLoader.Instance;
        Models = OnnxModelSet.Probe(_options.ModelsDirectory);

        if (Models.FaceModelPath is { } facePath) _faces = YuNetFaceDetector.TryLoad(facePath, _options);
        if (Models.SaliencyModelPath is { } saliencyPath) _saliency = U2NetSaliencyDetector.TryLoad(saliencyPath, _options);
        if (Models.AestheticModelPath is { } aestheticPath) _aesthetics = NimaAestheticScorer.TryLoad(aestheticPath, _options);

        _regionShape = SaliencyRegionShape.From(_options.Classical);
    }

    /// <summary>Which model files were found.</summary>
    public OnnxModelSet Models { get; }

    /// <summary>True when at least one model actually loaded — otherwise this analyzer is the classical one wearing a hat.</summary>
    public bool IsAvailable => _faces is not null || _saliency is not null || _aesthetics is not null;

    /// <summary>True when face detection is live.</summary>
    public bool HasFaceDetection => _faces is not null;

    /// <summary>True when model-based saliency is live.</summary>
    public bool HasSaliency => _saliency is not null;

    /// <summary>True when NIMA aesthetics are live.</summary>
    public bool HasAesthetics => _aesthetics is not null;

    /// <inheritdoc/>
    public string Id => AnalyzerIds.LocalOnnx;

    /// <summary>
    /// <c>"1.0-"</c> plus the loaded-model signature (<c>f</c>/<c>s</c>/<c>a</c>). Installing a model
    /// later changes the version, which re-keys <c>cache/analysis/</c> and gets the affected photos
    /// re-analyzed in the background — invalidation by key mismatch, not bookkeeping (doc 06).
    /// </summary>
    public string Version => "1.0-" + (LoadedSignature.Length == 0 ? "none" : LoadedSignature);

    /// <summary>Probes a models folder without loading anything.</summary>
    /// <param name="modelsDirectory">Folder to probe; the default <c>models/</c> folder when null.</param>
    public static OnnxAvailability Probe(string? modelsDirectory = null) => OnnxAvailability.Probe(modelsDirectory);

    /// <summary>
    /// Creates the analyzer only when at least one model loaded; otherwise reports why not and
    /// leaves the caller to use <see cref="ClassicalAnalyzer"/>.
    /// </summary>
    /// <param name="options">Configuration; defaults when null.</param>
    /// <param name="analyzer">The analyzer, or null.</param>
    /// <param name="reason">Human-readable outcome, suitable for the settings page and the log.</param>
    public static bool TryCreate(OnnxAnalyzerOptions? options, out OnnxAnalyzer? analyzer, out string reason)
    {
        options ??= OnnxAnalyzerOptions.Default;
        var availability = OnnxAvailability.Probe(options.ModelsDirectory);
        if (!availability.IsAvailable)
        {
            analyzer = null;
            reason = availability.Reason;
            return false;
        }

        var candidate = new OnnxAnalyzer(options);
        if (!candidate.IsAvailable)
        {
            candidate.Dispose();
            analyzer = null;
            reason = $"ONNX model files were found in '{availability.Models.Directory}' but none of them loaded; " +
                     "the model-free analyzer will be used.";
            return false;
        }

        analyzer = candidate;
        reason = availability.Reason;
        return true;
    }

    /// <inheritdoc/>
    public async Task<AnalysisResult> AnalyzeAsync(AnalysisInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        var image = await _loader.LoadAsync(input.AnalysisCopyPath, _options.Classical.MaxLongEdge, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        return await Task.Run(() => AnalyzeCore(image, ct), ct).ConfigureAwait(false);
    }

    /// <summary>Analyzes already-decoded pixels — the path for a bitmap the job queue already holds.</summary>
    public AnalysisResult Analyze(AnalysisImage image, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return AnalyzeCore(image, ct);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _faces?.Dispose();
        _saliency?.Dispose();
        _aesthetics?.Dispose();
    }

    private string LoadedSignature =>
        (_faces is not null ? "f" : string.Empty) +
        (_saliency is not null ? "s" : string.Empty) +
        (_aesthetics is not null ? "a" : string.Empty);

    private AnalysisResult AnalyzeCore(AnalysisImage image, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Classical metrics always run: doc 06 puts sharpness and exposure in the local pipeline
        // beside the models, and they are also the fallback for every stage that is not installed.
        var classical = ClassicalPipeline.Analyze(image, _options.Classical);
        var regions = new List<FocusRegion>();

        var faces = _faces?.Detect(image, ct) ?? [];
        var faceCount = faces.Count;
        double largestFaceArea = 0;
        foreach (var face in faces)
        {
            var area = face.Rect.Area;
            if (area > largestFaceArea) largestFaceArea = area;

            // doc 06: weight = 0.6·confidence + 0.4·min(1, faceArea/0.04).
            var weight = Math.Clamp(
                0.6 * face.Confidence + 0.4 * Math.Min(1, area / _options.FaceFullWeightArea), 0, 1);
            regions.Add(new FocusRegion { Rect = face.Rect, Weight = weight, Kind = FocusKind.Face });
        }

        ct.ThrowIfCancellationRequested();

        var saliency = _saliency?.Detect(image, _regionShape, ct);
        regions.AddRange(saliency is { Count: > 0 } ? saliency : classical.Regions);

        ct.ThrowIfCancellationRequested();

        var aesthetic = _aesthetics?.Score(image, ct) ?? classical.AestheticProxy;

        var signals = new RawQualitySignals(
            aesthetic,
            classical.Sharpness,
            classical.Exposure,
            faceCount,
            largestFaceArea);

        return new AnalysisResult(regions, signals);
    }
}
