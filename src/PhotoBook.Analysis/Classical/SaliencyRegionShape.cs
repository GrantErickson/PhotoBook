namespace PhotoBook.Analysis.Classical;

/// <summary>
/// The three constants that shape saliency regions, whichever detector produced the map (doc 06):
/// drop components under 2% of the image, keep 3, and saturate the weight at 15% area.
/// </summary>
/// <param name="MinRegionArea">Minimum region area as a fraction of the image.</param>
/// <param name="MaxRegions">How many regions to keep.</param>
/// <param name="FullWeightArea">Area at which a region's weight saturates.</param>
public readonly record struct SaliencyRegionShape(double MinRegionArea, int MaxRegions, double FullWeightArea)
{
    /// <summary>The shape implied by a set of classical options.</summary>
    public static SaliencyRegionShape From(ClassicalAnalyzerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new SaliencyRegionShape(options.MinRegionArea, options.MaxRegions, options.SaliencyFullWeightArea);
    }
}
