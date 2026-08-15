using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;

namespace PhotoBook.Analysis.Classical;

/// <summary>
/// Everything the model-free pipeline measured for one photo: the scalar signals doc 06 asks for,
/// the proposed saliency regions, and the intermediate maps (kept so the Photos tab and the tests
/// can visualize <em>why</em> a region was proposed).
/// </summary>
/// <param name="Sharpness">Normalized variance of the Laplacian, <c>0..1</c>.</param>
/// <param name="Exposure">Histogram exposure score, <c>0..1</c>.</param>
/// <param name="Colorfulness">Normalized Hasler–Süsstrunk colorfulness, <c>0..1</c>.</param>
/// <param name="DynamicRange">1st-to-99th percentile luma spread, <c>0..1</c>.</param>
/// <param name="AestheticProxy">
/// The stand-in for NIMA when no model files are installed — a documented blend of colorfulness,
/// sharpness, exposure, dynamic range and subject strength (never a claim to be NIMA).
/// </param>
/// <param name="LaplacianVariance">The raw Laplacian variance behind <paramref name="Sharpness"/>.</param>
/// <param name="ClipLow">Fraction of pixels crushed to black.</param>
/// <param name="ClipHigh">Fraction of pixels blown to white.</param>
/// <param name="MeanLuma">Mean luma, <c>0..1</c>.</param>
/// <param name="Regions">Proposed <see cref="FocusKind.Saliency"/> regions, strongest first.</param>
/// <param name="UsedFallbackRegion">True when nothing salient was found and the center region was substituted.</param>
/// <param name="SubjectMap">The fused subject map (saliency × sharpness × center prior), normalized <c>0..1</c>.</param>
/// <param name="SubjectMapSize">Edge of the square <paramref name="SubjectMap"/> grid.</param>
/// <param name="TileSharpness">Per-tile normalized sharpness, row-major.</param>
/// <param name="TileColumns">Tile-grid width.</param>
/// <param name="TileRows">Tile-grid height.</param>
public sealed record ClassicalMeasurements(
    double Sharpness,
    double Exposure,
    double Colorfulness,
    double DynamicRange,
    double AestheticProxy,
    double LaplacianVariance,
    double ClipLow,
    double ClipHigh,
    double MeanLuma,
    IReadOnlyList<FocusRegion> Regions,
    bool UsedFallbackRegion,
    float[] SubjectMap,
    int SubjectMapSize,
    float[] TileSharpness,
    int TileColumns,
    int TileRows)
{
    /// <summary>
    /// The signals in the shape the <see cref="IImageAnalyzer"/> contract returns. Face counts are
    /// zero: no model files means no face detection, and inventing faces would poison the tier
    /// score (doc 06's <c>faceBonus</c>).
    /// </summary>
    public RawQualitySignals ToSignals() => new(AestheticProxy, Sharpness, Exposure, 0, 0);
}
