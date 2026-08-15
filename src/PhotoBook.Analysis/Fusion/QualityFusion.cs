using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;

namespace PhotoBook.Analysis.Fusion;

/// <summary>
/// Doc 06's raw quality fusion — the absolute half of R26 ("goodness of an image"), before the
/// month-relative half that <see cref="TierAssigner"/> owns.
/// <code>
/// faceBonus = min(1, 0.25·min(faceCount, 3) + 0.5·min(1, largestFaceArea / 0.08))
///           + (0.1 if any rect-less PersonTag present)      // capped at 1
/// raw       = 0.50·aesthetic + 0.25·sharpness + 0.15·exposure + 0.10·faceBonus
/// </code>
/// The weights are v1 defaults, exposed as constants so the golden tests of doc 13 pin them.
/// </summary>
public static class QualityFusion
{
    /// <summary>Weight of the aesthetic signal in the raw score.</summary>
    public const double AestheticWeight = 0.50;

    /// <summary>Weight of sharpness in the raw score.</summary>
    public const double SharpnessWeight = 0.25;

    /// <summary>Weight of exposure in the raw score.</summary>
    public const double ExposureWeight = 0.15;

    /// <summary>Weight of the face bonus in the raw score.</summary>
    public const double FaceBonusWeight = 0.10;

    /// <summary>Per-face contribution, for up to three faces.</summary>
    public const double PerFaceBonus = 0.25;

    /// <summary>Faces beyond this count add nothing — a crowd is not three times a portrait.</summary>
    public const int MaxCountedFaces = 3;

    /// <summary>Contribution of the largest face's size.</summary>
    public const double FaceAreaBonus = 0.5;

    /// <summary>Face area at which the size term saturates.</summary>
    public const double FaceAreaFullBonus = 0.08;

    /// <summary>Bonus for a person tag that carries no rect: it says "someone we know is here" and nothing more.</summary>
    public const double RectlessPersonTagBonus = 0.10;

    /// <summary>The face bonus term.</summary>
    /// <param name="faceCount">Detected faces.</param>
    /// <param name="largestFaceArea">Largest face box area over image area, <c>0..1</c>.</param>
    /// <param name="hasRectlessPersonTag">True when at least one person tag has no rect.</param>
    public static double FaceBonus(int faceCount, double largestFaceArea, bool hasRectlessPersonTag)
    {
        var bonus = Math.Min(1.0,
            PerFaceBonus * Math.Min(Math.Max(faceCount, 0), MaxCountedFaces) +
            FaceAreaBonus * Math.Min(1.0, Math.Max(largestFaceArea, 0) / FaceAreaFullBonus));

        if (hasRectlessPersonTag) bonus += RectlessPersonTagBonus;
        return Math.Clamp(bonus, 0, 1);
    }

    /// <summary>Fuses raw signals into a <see cref="QualityScore"/>; the percentile stays 0 until tiering runs.</summary>
    /// <param name="signals">What the analyzer measured.</param>
    /// <param name="personTags">The photo's person tags; only the rect-less ones matter here.</param>
    public static QualityScore Fuse(RawQualitySignals signals, IReadOnlyList<PersonTag>? personTags = null)
    {
        ArgumentNullException.ThrowIfNull(signals);

        var hasRectless = personTags is not null && personTags.Any(t => t is { RegionRect: null });
        var faceBonus = FaceBonus(signals.FaceCount, signals.LargestFaceArea, hasRectless);

        var aesthetic = Clamp01(signals.Aesthetic);
        var sharpness = Clamp01(signals.Sharpness);
        var exposure = Clamp01(signals.Exposure);

        var fused = Clamp01(
            AestheticWeight * aesthetic +
            SharpnessWeight * sharpness +
            ExposureWeight * exposure +
            FaceBonusWeight * faceBonus);

        return new QualityScore
        {
            Aesthetic = aesthetic,
            Sharpness = sharpness,
            Exposure = exposure,
            FaceBonus = faceBonus,
            Fused = fused,
            MonthPercentile = 0,
        };
    }

    private static double Clamp01(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
}
