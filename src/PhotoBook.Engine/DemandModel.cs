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

    // ── §4b The solo-page rule ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether <paramref name="photo"/> has <b>earned</b> a page all to itself (doc 08 §4b). A page
    /// holding one photo renders that photo huge on a full template, which is only right when the
    /// photograph deserves it:
    /// <list type="bullet">
    /// <item><description>an S- or A-tier photo always does (<see cref="LayoutWeights.SoloTier"/>);</description></item>
    /// <item><description>otherwise, only when it is genuinely the only photo of its stream — the
    /// day, or the absorbed run of days, being laid out — and is not bottom-tier
    /// (<see cref="LayoutWeights.LoneDayTier"/>).</description></item>
    /// </list>
    /// Tier is the <b>effective</b> tier, so promoting a photo (R26) is also how the user says "yes,
    /// this one does deserve its own page".
    /// </summary>
    /// <param name="photo">The candidate photo.</param>
    /// <param name="streamPhotoCount">How many photos the day (or absorbed run) the page came from holds.</param>
    /// <param name="weights">The tunables of doc 08 §14.</param>
    public static bool EarnsSoloPage(Photo photo, int streamPhotoCount, LayoutWeights weights)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(weights);

        var rank = TierRank(photo.EffectiveTier);
        if (rank <= TierRank(weights.SoloTier)) return true;
        return streamPhotoCount <= 1 && rank <= TierRank(weights.LoneDayTier);
    }

    /// <summary>
    /// A <b>weak straggler</b>: a day whose single photo has not earned a page to itself, so the day
    /// cannot stand alone and must be merged or absorbed (doc 08 §4b, §5).
    /// </summary>
    public static bool IsWeakStraggler(LayoutDay day, LayoutWeights weights)
    {
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(weights);
        return day.Photos.Count == 1 && !EarnsSoloPage(day.Photos[0], 1, weights);
    }

    /// <summary>
    /// The number of single-photo pages a <paramref name="pages"/>-way cut of
    /// <paramref name="photoCount"/> photos <em>cannot avoid</em>: every part holds at least one
    /// photo, so once the parts of two or more run out, <c>2k − n</c> parts are forced down to one.
    /// </summary>
    public static int ForcedSoloPages(int photoCount, int pages) => Math.Max(0, 2 * pages - photoCount);

    /// <summary>
    /// The DP's solo-page cost for cutting <paramref name="photos"/> across <paramref name="pages"/>
    /// pages (doc 08 §4b): <c>SOLO_PAGE_COST × forcedSoloPages × unearnedShare</c>. It is zero for a
    /// stream whose every photo has earned a solo page and zero for any cut roomy enough that no page
    /// is forced down to one photo, so it only bites where the user's complaint lives — a lame frame
    /// alone on a page.
    /// </summary>
    public static double SoloPageCost(IReadOnlyList<Photo> photos, int pages, LayoutWeights weights)
    {
        ArgumentNullException.ThrowIfNull(photos);
        ArgumentNullException.ThrowIfNull(weights);

        var n = photos.Count;
        if (n == 0) return 0;

        var forced = ForcedSoloPages(n, pages);
        if (forced == 0) return 0;

        var unearned = 0;
        foreach (var photo in photos)
        {
            if (!EarnsSoloPage(photo, n, weights)) unearned++;
        }

        if (unearned == 0) return 0;
        return weights.SoloPageCost * forced * ((double)unearned / n);
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
