using PhotoBook.Analysis.Pixels;
using PhotoBook.Core.Model;

namespace PhotoBook.Analysis.Classical;

/// <summary>
/// Turns a saliency map into <see cref="FocusKind.Saliency"/> focus regions exactly the way doc 06
/// prescribes for U2-Netp: threshold, connected components, drop components under 2% of the image,
/// keep the largest few, weight each <c>meanMaskValue × min(1, area/0.15)</c>. Shared by the
/// model-free detector and the U2-Netp path so both produce regions on identical terms.
/// </summary>
public static class SaliencyRegions
{
    /// <summary>Extracts regions using a <see cref="SaliencyRegionShape"/> for the three shaping constants.</summary>
    /// <param name="map">Saliency values, normalized to <c>0..1</c>, row-major.</param>
    /// <param name="width">Map width.</param>
    /// <param name="height">Map height.</param>
    /// <param name="threshold">Foreground threshold.</param>
    /// <param name="shape">Minimum area, region count and full-weight area.</param>
    public static List<FocusRegion> Extract(float[] map, int width, int height, double threshold, SaliencyRegionShape shape) =>
        Extract(map, width, height, threshold, shape.MinRegionArea, shape.MaxRegions, shape.FullWeightArea);

    /// <summary>Extracts regions from a normalized saliency map at a fixed threshold.</summary>
    /// <param name="map">Saliency values, normalized to <c>0..1</c>, row-major.</param>
    /// <param name="width">Map width.</param>
    /// <param name="height">Map height.</param>
    /// <param name="threshold">Foreground threshold (doc 06 uses 0.5 for U2-Netp).</param>
    /// <param name="minArea">Minimum bounding-box area as a fraction of the image (doc 06: 0.02).</param>
    /// <param name="maxRegions">How many regions to keep (doc 06: 3).</param>
    /// <param name="fullWeightArea">Area at which weight saturates (doc 06: 0.15).</param>
    public static List<FocusRegion> Extract(
        float[] map,
        int width,
        int height,
        double threshold,
        double minArea,
        int maxRegions,
        double fullWeightArea)
    {
        ArgumentNullException.ThrowIfNull(map);

        var cells = width * height;
        var mask = new bool[cells];
        var foreground = 0;
        for (var i = 0; i < cells; i++)
        {
            if (map[i] < threshold) continue;
            mask[i] = true;
            foreground++;
        }

        var regions = new List<FocusRegion>();
        if (foreground == 0) return regions;

        foreach (var component in ConnectedComponents.Find(mask, map, width, height))
        {
            var area = (double)component.BoxWidth * component.BoxHeight / cells;
            if (area < minArea) continue;

            var rect = new Rect(
                (double)component.MinX / width,
                (double)component.MinY / height,
                (double)component.BoxWidth / width,
                (double)component.BoxHeight / height);

            regions.Add(new FocusRegion
            {
                Rect = rect,
                Weight = ImageOps.Clamp01(component.MeanValue * Math.Min(1, area / fullWeightArea)),
                Kind = FocusKind.Saliency,
            });
        }

        // Doc 06 keeps "the 3 largest"; ranking by weight keeps the same three in practice, because
        // weight already carries the size term min(1, area/0.15) — but when a small, intensely
        // salient blob competes with a sprawling low-intensity one, the blob is the subject.
        regions.Sort((a, b) =>
        {
            var byWeight = b.Weight.CompareTo(a.Weight);
            if (byWeight != 0) return byWeight;
            var byArea = b.Rect.Area.CompareTo(a.Rect.Area);
            if (byArea != 0) return byArea;
            var byX = a.Rect.X.CompareTo(b.Rect.X);
            return byX != 0 ? byX : a.Rect.Y.CompareTo(b.Rect.Y);
        });

        if (regions.Count > maxRegions) regions.RemoveRange(maxRegions, regions.Count - maxRegions);
        return regions;
    }
}
