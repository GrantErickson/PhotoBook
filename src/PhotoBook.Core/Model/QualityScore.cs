namespace PhotoBook.Core.Model;

/// <summary>
/// The fused per-photo quality signals and the photo's rank within its month (doc 03 §3, doc 06).
/// All components are <c>[0,1]</c>; the raw fusion is
/// <c>0.50·aesthetic + 0.25·sharpness + 0.15·exposure + 0.10·faceBonus</c>, then percentile-ranked
/// against the non-excluded photos of the same Chapter.
/// </summary>
public sealed record QualityScore
{
    /// <summary>NIMA/MobileNet aesthetic score, normalized to <c>[0,1]</c>.</summary>
    public double Aesthetic { get; set; }

    /// <summary>Classical Laplacian-variance sharpness, <c>[0,1]</c>; 1 = crisp.</summary>
    public double Sharpness { get; set; }

    /// <summary>Classical histogram exposure metric, <c>[0,1]</c>; 1 = well exposed.</summary>
    public double Exposure { get; set; }

    /// <summary>Bonus for the count and size of detected faces, <c>[0,1]</c>.</summary>
    public double FaceBonus { get; set; }

    /// <summary>The fused raw score, <c>[0,1]</c> — comparable only within a month.</summary>
    public double Fused { get; set; }

    /// <summary>Percentile of <see cref="Fused"/> within the photo's month, <c>0..100</c>.</summary>
    public double MonthPercentile { get; set; }

    /// <summary>
    /// The tier implied by <see cref="MonthPercentile"/> using the kernel §4 bands: S = top 10%,
    /// A = next 25%, B = next 45%, C = bottom 20%.
    /// </summary>
    public Tier TierFromPercentile => MonthPercentile switch
    {
        >= 90 => Tier.S,
        >= 65 => Tier.A,
        >= 20 => Tier.B,
        _ => Tier.C,
    };
}
