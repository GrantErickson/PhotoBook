using PhotoBook.Analysis.Pixels;
using PhotoBook.Core.Abstractions;

namespace PhotoBook.Analysis.Classical;

/// <summary>
/// The default <see cref="IImageAnalyzer"/>: pure managed code over decoded pixels, zero external
/// files, always available.
/// <para>
/// Doc 06 specifies <c>LocalOnnxAnalyzer</c> as the default, but the ONNX model files cannot be
/// bundled (licensing and size), so on a fresh install there are none. Automatic layout is the whole
/// product, and it depends on focus regions and tiers existing — therefore the model-free pipeline
/// is the floor, not a stub: variance-of-Laplacian sharpness (global and per tile),
/// histogram exposure, Hasler–Süsstrunk colorfulness, and a real spectral-residual saliency detector
/// whose thresholded components become <see cref="Core.Model.FocusKind.Saliency"/> regions.
/// </para>
/// <para>
/// What it honestly cannot do without models: faces. <see cref="RawQualitySignals.FaceCount"/> and
/// <see cref="RawQualitySignals.LargestFaceArea"/> come back zero, so the face bonus is zero for
/// every photo — uniform, hence harmless to the within-month ranking — until either the ONNX models
/// or OneDrive person tags arrive.
/// </para>
/// </summary>
public sealed class ClassicalAnalyzer : IImageAnalyzer
{
    private readonly IAnalysisImageLoader _loader;
    private readonly ClassicalAnalyzerOptions _options;

    /// <summary>Creates the analyzer.</summary>
    /// <param name="loader">Pixel source; the Magick.NET loader when null.</param>
    /// <param name="options">Tuning constants; <see cref="ClassicalAnalyzerOptions.Default"/> when null.</param>
    public ClassicalAnalyzer(IAnalysisImageLoader? loader = null, ClassicalAnalyzerOptions? options = null)
    {
        _loader = loader ?? MagickAnalysisImageLoader.Instance;
        _options = options ?? ClassicalAnalyzerOptions.Default;
    }

    /// <inheritdoc/>
    public string Id => AnalyzerIds.Classical;

    /// <inheritdoc/>
    public string Version => "1.0";

    /// <summary>The tuning constants in force.</summary>
    public ClassicalAnalyzerOptions Options => _options;

    /// <inheritdoc/>
    public async Task<AnalysisResult> AnalyzeAsync(AnalysisInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ct.ThrowIfCancellationRequested();

        var image = await _loader.LoadAsync(input.AnalysisCopyPath, _options.MaxLongEdge, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var measurements = await Task.Run(() => ClassicalPipeline.Analyze(image, _options), ct).ConfigureAwait(false);
        return new AnalysisResult(measurements.Regions, measurements.ToSignals());
    }

    /// <summary>
    /// Measures already-decoded pixels — the path the job queue uses when it has a bitmap in hand,
    /// and the path the ONNX analyzer uses for its classical signals.
    /// </summary>
    public ClassicalMeasurements Measure(AnalysisImage image) => ClassicalPipeline.Analyze(image, _options);
}
