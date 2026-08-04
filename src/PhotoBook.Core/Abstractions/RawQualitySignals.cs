using PhotoBook.Core.Model;

namespace PhotoBook.Core.Abstractions;

/// <summary>
/// The raw scalar quality signals an analyzer measures (doc 06). Weighting, the fused score,
/// percentiles and <see cref="Tier"/> assignment all happen in shared fusion code outside the
/// analyzer, so swapping local for cloud changes perception quality but never semantics.
/// </summary>
/// <param name="Aesthetic">NIMA mean, normalized to <c>0..1</c>.</param>
/// <param name="Sharpness">Laplacian-variance sharpness, <c>0..1</c>; 1 = crisp.</param>
/// <param name="Exposure">Histogram exposure metric, <c>0..1</c>; 1 = well exposed.</param>
/// <param name="FaceCount">Number of detected faces.</param>
/// <param name="LargestFaceArea">Largest face box area divided by image area, <c>0..1</c>.</param>
public sealed record RawQualitySignals(
    double Aesthetic,
    double Sharpness,
    double Exposure,
    int FaceCount,
    double LargestFaceArea);
