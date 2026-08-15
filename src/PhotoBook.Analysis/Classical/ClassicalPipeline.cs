using PhotoBook.Analysis.Pixels;
using PhotoBook.Core.Model;

namespace PhotoBook.Analysis.Classical;

/// <summary>
/// The model-free perception pipeline: classical metrics plus spectral-residual saliency, fused into
/// <see cref="ClassicalMeasurements"/>. It is shared code, not just <see cref="ClassicalAnalyzer"/>'s
/// implementation — <see cref="Onnx.OnnxAnalyzer"/> runs the same sharpness and exposure metrics
/// (doc 06 puts them in the local pipeline alongside the models) and falls back to this saliency
/// when U2-Netp is not installed.
/// </summary>
public static class ClassicalPipeline
{
    /// <summary>Weight of colorfulness in the aesthetic proxy.</summary>
    public const double AestheticColorfulnessWeight = 0.32;

    /// <summary>Weight of sharpness in the aesthetic proxy.</summary>
    public const double AestheticSharpnessWeight = 0.26;

    /// <summary>Weight of exposure in the aesthetic proxy.</summary>
    public const double AestheticExposureWeight = 0.20;

    /// <summary>Weight of dynamic range in the aesthetic proxy.</summary>
    public const double AestheticDynamicRangeWeight = 0.12;

    /// <summary>Weight of subject strength (the primary region's weight) in the aesthetic proxy.</summary>
    public const double AestheticSubjectWeight = 0.10;

    /// <summary>Measures one image. Pure and deterministic: same pixels in, same numbers out.</summary>
    /// <param name="image">The decoded analysis image.</param>
    /// <param name="options">Tuning constants; <see cref="ClassicalAnalyzerOptions.Default"/> when null.</param>
    public static ClassicalMeasurements Analyze(AnalysisImage image, ClassicalAnalyzerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        options ??= ClassicalAnalyzerOptions.Default;

        var working = image.LimitLongEdge(options.MaxLongEdge);

        var sharpness = ClassicalMetrics.Sharpness(working, options.SharpnessTilesOnLongEdge);
        var exposure = ClassicalMetrics.Exposure(working);
        var colorfulness = ClassicalMetrics.Colorfulness(working);

        var size = options.SaliencyGridSize;
        var residual = ComputeResidual(working, size, options);
        var subject = FuseSubjectMap(residual, sharpness, size, options);

        // A near-flat frame has no subject to find; normalizing its map would turn noise into a
        // confident region, so it goes straight to the center fallback.
        var featureless = exposure.DynamicRange < options.MinDynamicRangeForSaliency &&
                          sharpness.Normalized < options.MinSharpnessForSaliency;

        var (regions, usedFallback) = featureless
            ? (new List<FocusRegion> { FallbackRegion(options) }, true)
            : ExtractRegions(subject, size, options);

        var subjectStrength = regions.Count > 0 ? regions.Max(r => r.Weight) : 0;
        var aesthetic = ImageOps.Clamp01(
            AestheticColorfulnessWeight * colorfulness.Normalized +
            AestheticSharpnessWeight * sharpness.Normalized +
            AestheticExposureWeight * exposure.Score +
            AestheticDynamicRangeWeight * exposure.DynamicRange +
            AestheticSubjectWeight * subjectStrength);

        return new ClassicalMeasurements(
            sharpness.Normalized,
            exposure.Score,
            colorfulness.Normalized,
            exposure.DynamicRange,
            aesthetic,
            sharpness.LaplacianVariance,
            exposure.ClipLow,
            exposure.ClipHigh,
            exposure.MeanLuma,
            regions,
            usedFallback,
            subject,
            size,
            sharpness.TileNormalized,
            sharpness.TileColumns,
            sharpness.TileRows);
    }

    /// <summary>
    /// Spectral residual over three channels — luma and the two color-opponent channels
    /// (<c>rg = R − G</c>, <c>yb = ½(R + G) − B</c>) — combined as
    /// <c>lumaWeight·srLuma + chromaWeight·max(srRg, srYb)</c>.
    /// <para>
    /// Luma alone is a well-known landscape trap: the horizon is the strongest thing in the
    /// spectrum, so the "subject" comes back as a band across the whole frame. Color opponency is
    /// how the human visual system separates a subject from its surround, and it costs two more
    /// 64×64 transforms.
    /// </para>
    /// </summary>
    private static float[] ComputeResidual(Pixels.AnalysisImage image, int size, ClassicalAnalyzerOptions options)
    {
        var small = image.ResizeTo(size, size);
        var cells = size * size;

        var luma = small.Luma;
        var rg = new float[cells];
        var yb = new float[cells];
        for (var i = 0; i < cells; i++)
        {
            var s = i * 3;
            float r = small.Rgb[s], g = small.Rgb[s + 1], b = small.Rgb[s + 2];
            rg[i] = r - g;
            yb[i] = 0.5f * (r + g) - b;
        }

        var srLuma = SpectralResidualSaliency.Compute(luma, size);
        var srRg = SpectralResidualSaliency.Compute(rg, size);
        var srYb = SpectralResidualSaliency.Compute(yb, size);

        var total = options.LumaSaliencyWeight + options.ChromaSaliencyWeight;
        if (total <= 0) total = 1;

        var combined = new float[cells];
        for (var i = 0; i < cells; i++)
        {
            var chroma = Math.Max(srRg[i], srYb[i]);
            combined[i] = (float)((options.LumaSaliencyWeight * srLuma[i] + options.ChromaSaliencyWeight * chroma) / total);
        }

        return ImageOps.Normalize01(combined);
    }

    /// <summary>
    /// Blends the spectral residual with the tile-sharpness map and applies a gentle center prior.
    /// Result is renormalized to <c>0..1</c>.
    /// </summary>
    private static float[] FuseSubjectMap(
        float[] residual,
        ClassicalMetrics.SharpnessResult sharpness,
        int size,
        ClassicalAnalyzerOptions options)
    {
        var sharpMap = ImageOps.Normalize01(
            ImageOps.UpsampleBilinear(sharpness.TileNormalized, sharpness.TileColumns, sharpness.TileRows, size, size));

        var fused = new float[size * size];
        var totalBlend = options.SaliencyBlend + options.SharpnessBlend;
        if (totalBlend <= 0) totalBlend = 1;

        var sigma = Math.Max(1e-6, options.CenterPriorSigma);
        var strength = Math.Clamp(options.CenterPriorStrength, 0, 1);

        for (var y = 0; y < size; y++)
        {
            var ny = (y + 0.5) / size - 0.5;
            for (var x = 0; x < size; x++)
            {
                var nx = (x + 0.5) / size - 0.5;
                var prior = (1 - strength) + strength * Math.Exp(-(nx * nx + ny * ny) / (2 * sigma * sigma));
                var i = y * size + x;
                var blended = (options.SaliencyBlend * residual[i] + options.SharpnessBlend * sharpMap[i]) / totalBlend;
                fused[i] = (float)(blended * prior);
            }
        }

        return ImageOps.Normalize01(fused);
    }

    /// <summary>
    /// Thresholds the subject map (strictest multiplier first), keeps the largest qualifying
    /// components as <see cref="FocusKind.Saliency"/> regions, and falls back to a center-weighted
    /// region only when nothing survives.
    /// </summary>
    private static (List<FocusRegion> Regions, bool UsedFallback) ExtractRegions(
        float[] subject,
        int size,
        ClassicalAnalyzerOptions options)
    {
        var cells = size * size;
        double mean = 0;
        foreach (var v in subject) mean += v;
        mean /= cells;

        foreach (var multiplier in options.ThresholdMultipliers)
        {
            var threshold = mean * multiplier;
            if (threshold <= 0) continue;

            var foreground = 0;
            for (var i = 0; i < cells; i++)
                if (subject[i] >= threshold)
                    foreground++;

            if (foreground == 0) continue;
            if ((double)foreground / cells > options.MaxForegroundFraction) continue;

            var regions = SaliencyRegions.Extract(
                subject, size, size, threshold, options.MinRegionArea, options.MaxRegions, options.SaliencyFullWeightArea);

            if (regions.Count == 0) continue;
            return (regions, false);
        }

        return ([FallbackRegion(options)], true);
    }

    /// <summary>
    /// The last resort: the central 60% of the frame at a low weight. Doc 06 is explicit that
    /// smart-crop must never fall back to a blind center crop — this at least tells the layout
    /// engine, honestly and weakly, where to look.
    /// </summary>
    private static FocusRegion FallbackRegion(ClassicalAnalyzerOptions options) => new()
    {
        Rect = options.FallbackRegion,
        Weight = ImageOps.Clamp01(options.FallbackWeight),
        Kind = FocusKind.Saliency,
    };
}
