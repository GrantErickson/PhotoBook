using PhotoBook.Core.Model;

namespace PhotoBook.Analysis.Classical;

/// <summary>
/// Tuning constants of the model-free pipeline. Every default is stated in doc 06 or derived from
/// it; they are exposed so the golden tests in doc 13 can pin them and so a future tuning pass has
/// one place to change.
/// </summary>
public sealed class ClassicalAnalyzerOptions
{
    /// <summary>The shared default instance.</summary>
    public static ClassicalAnalyzerOptions Default { get; } = new();

    /// <summary>Long edge the pixels are capped at before measuring; the analysis copy is already 1024 px (doc 06).</summary>
    public int MaxLongEdge { get; init; } = 1024;

    /// <summary>Edge of the square grid the spectral residual runs on. Must be a power of two.</summary>
    public int SaliencyGridSize { get; init; } = SpectralResidualSaliency.MapSize;

    /// <summary>Tile-grid resolution along the long edge for per-tile sharpness.</summary>
    public int SharpnessTilesOnLongEdge { get; init; } = 16;

    /// <summary>Weight of the spectral-residual map in the fused subject map.</summary>
    public double SaliencyBlend { get; init; } = 0.65;

    /// <summary>Weight of the luma channel inside the spectral-residual map.</summary>
    public double LumaSaliencyWeight { get; init; } = 0.60;

    /// <summary>
    /// Weight of the strongest color-opponent channel inside the spectral-residual map. Luma alone
    /// fixates on the horizon line of a landscape; the opponent channels are what make the red coat
    /// on green grass win, which is usually the thing the photo is actually of.
    /// </summary>
    public double ChromaSaliencyWeight { get; init; } = 0.40;

    /// <summary>
    /// Weight of the per-tile sharpness map in the fused subject map. A sharp region against a soft
    /// background is a subject even when the spectral residual is ambivalent.
    /// </summary>
    public double SharpnessBlend { get; init; } = 0.35;

    /// <summary>
    /// Strength of the center prior, <c>0..1</c>. Photographers center their subjects often enough
    /// that a gentle bias improves crops; anything strong would just reinvent center-cropping, so
    /// this stays small.
    /// </summary>
    public double CenterPriorStrength { get; init; } = 0.25;

    /// <summary>Standard deviation of the center prior in normalized image units.</summary>
    public double CenterPriorSigma { get; init; } = 0.35;

    /// <summary>Threshold multipliers applied to the map's mean, tried strictest first (the paper uses 3×).</summary>
    public IReadOnlyList<double> ThresholdMultipliers { get; init; } = [3.0, 2.5, 2.0, 1.5, 1.2];

    /// <summary>Components smaller than this fraction of the image are dropped (doc 06: 2%).</summary>
    public double MinRegionArea { get; init; } = 0.02;

    /// <summary>A threshold whose foreground covers more than this fraction of the image is too permissive to be a subject.</summary>
    public double MaxForegroundFraction { get; init; } = 0.60;

    /// <summary>
    /// Below this dynamic range — and with no measurable detail — an image has nothing to find, and
    /// normalizing its saliency map would only amplify noise into a confident-looking region. Such
    /// images go straight to the center fallback.
    /// </summary>
    public double MinDynamicRangeForSaliency { get; init; } = 0.05;

    /// <summary>Companion of <see cref="MinDynamicRangeForSaliency"/>: normalized sharpness below which detail counts as absent.</summary>
    public double MinSharpnessForSaliency { get; init; } = 0.05;

    /// <summary>How many regions survive; doc 06 keeps the three largest.</summary>
    public int MaxRegions { get; init; } = 3;

    /// <summary>Area at which a saliency region reaches full weight (doc 06: <c>min(1, area/0.15)</c>).</summary>
    public double SaliencyFullWeightArea { get; init; } = 0.15;

    /// <summary>
    /// The last-resort region used when nothing salient is found: the central 60% of the frame, at a
    /// low weight so a real detection from any other source always outranks it.
    /// </summary>
    public Rect FallbackRegion { get; init; } = new(0.2, 0.2, 0.6, 0.6);

    /// <summary>Weight of <see cref="FallbackRegion"/>.</summary>
    public double FallbackWeight { get; init; } = 0.25;
}
