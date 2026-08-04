using PhotoBook.Core.Model;

namespace PhotoBook.Engine;

/// <summary>
/// Phase 2 of doc 08 — the Demand Model. It converts a Day Group into a scalar <b>demand</b> in
/// page units (1.0 = one full page): the single knob-bearing formula that encodes "how much room
/// does this day deserve". Every constant lives in <see cref="LayoutWeights"/>.
/// <para>
/// Calibration targets from doc 08 §4, reproduced exactly by these formulas:
/// 2 C-tier photos + a 250-char entry ⇒ 0.242; 8 B photos, no journal ⇒ 0.93; a 40-photo birthday
/// (6 S, 12 A, 22 B) + 1900 chars ⇒ 6.83 (6.78 before <c>W_BASE</c>) ⇒ 6–7 pages.
/// </para>
/// </summary>
public static class DemandModel
{
    /// <summary>
    /// Σ <c>tierArea[tier(p)]</c> over the day's photos. Tier is the <b>effective</b> tier, so a
    /// user promote/demote (R26) directly moves the day's demand.
    /// </summary>
    public static double PhotoDemand(LayoutDay day, LayoutWeights weights)
    {
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(weights);
        var total = 0.0;
        foreach (var photo in day.Photos) total += weights.TierArea(photo.EffectiveTier);
        return total;
    }

    /// <summary><c>journalChars(d) / CHARS_PER_FULL_PAGE</c>.</summary>
    public static double TextDemand(LayoutDay day, LayoutWeights weights)
    {
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(weights);
        var perPage = weights.CharsPerFullPage > 0 ? weights.CharsPerFullPage : 1.0;
        return day.JournalChars / perPage;
    }

    /// <summary>
    /// <c>demand(d) = max(MIN_DAY_DEMAND, W_PHOTO·photoDemand + W_TEXT·textDemand + W_BASE)</c>
    /// (doc 08 §4).
    /// </summary>
    public static double Demand(LayoutDay day, LayoutWeights weights)
    {
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(weights);
        var raw = weights.PhotoWeight * PhotoDemand(day, weights)
                  + weights.TextWeight * TextDemand(day, weights)
                  + weights.BaseDemand;
        return Math.Max(raw, weights.MinDayDemand);
    }

    /// <summary>Total demand of a run of days — the <c>D</c> of the DP's cost function (doc 08 §5).</summary>
    public static double Demand(IReadOnlyList<LayoutDay> days, LayoutWeights weights)
    {
        ArgumentNullException.ThrowIfNull(days);
        var total = 0.0;
        foreach (var day in days) total += Demand(day, weights);
        return total;
    }

    /// <summary>
    /// <c>fitCost(k, D) = ((k − D) / max(k, D))²</c> — 0 is a perfect fill, →1 is badly off
    /// (doc 08 §5).
    /// </summary>
    public static double FitCost(double pages, double demand)
    {
        var denominator = Math.Max(pages, demand);
        if (!(denominator > 0)) return 0;
        var ratio = (pages - demand) / denominator;
        return ratio * ratio;
    }

    /// <summary>Rank of a tier for <c>tierDist</c>: S = 0 … C = 3 (doc 08 §7).</summary>
    public static int TierRank(Tier tier) => tier switch
    {
        Tier.S => 0,
        Tier.A => 1,
        Tier.B => 2,
        _ => 3,
    };

    /// <summary>Rank of a slot's tier affinity; <see cref="TierAffinity.Any"/> has no rank (doc 08 §7).</summary>
    public static int? TierRank(TierAffinity affinity) => affinity switch
    {
        TierAffinity.S => 0,
        TierAffinity.A => 1,
        TierAffinity.B => 2,
        TierAffinity.C => 3,
        _ => null,
    };
}
