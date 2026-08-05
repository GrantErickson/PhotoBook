using PhotoBook.Core.Model;

namespace PhotoBook.Engine;

/// <summary>
/// One decision of the DP (doc 08 §5): either several consecutive sparse days merged onto a single
/// <c>multiDay</c> page, or one day spread over <see cref="PageCount"/> pages.
/// </summary>
public sealed record PageSegment
{
    /// <summary>Index of the segment's first day in the day sequence.</summary>
    public required int FirstDayIndex { get; init; }

    /// <summary>How many consecutive days the segment covers; &gt; 1 only for a merge.</summary>
    public required int DayCount { get; init; }

    /// <summary>How many pages the segment emits.</summary>
    public required int PageCount { get; init; }

    /// <summary>True when the segment is a merged <c>multiDay</c> page (R28).</summary>
    public required bool IsMerge { get; init; }

    /// <summary>
    /// True when the segment <b>absorbed</b> weak straggler days into a neighbour's ordinary page
    /// (doc 08 §5): the covered days are flattened into one chronological photo stream and laid out
    /// exactly like a single day. Never set together with <see cref="IsMerge"/>.
    /// </summary>
    public bool IsAbsorb { get; init; }

    /// <summary>The side the segment's first page falls on.</summary>
    public required PageSide StartSide { get; init; }

    /// <summary>True when the day's text demand exceeds <c>MAX_TEXT_PER_PAGE</c> and wants a whole Spread.</summary>
    public bool TextNeedsSpread { get; init; }

    /// <summary>True when the spread-text rule could not be honored because the segment starts on a right page.</summary>
    public bool ParityViolated { get; init; }

    /// <summary>The segment's total demand in page units.</summary>
    public double Demand { get; init; }
}

/// <summary>
/// Phase 3 of doc 08 — exact dynamic-programming page partitioning over the day sequence.
/// <para>
/// The state is <c>dp[i][par]</c>: the minimal cost of laying out days <c>1..i</c> when the
/// <em>next</em> page to be emitted has parity <c>par</c>. Three transitions relax it — merging
/// <c>1 &lt; m ≤ MAX_DAYS_PER_PAGE</c> sparse days onto one <c>multiDay</c> page (R28), splitting one
/// day across <c>k</c> pages, and absorbing weak straggler days into a neighbour's ordinary page —
/// with the cost function of §5. The state space is ≤ 31 days × 2 parities, so exactness costs
/// nothing and, unlike greedy packing, small input changes produce small output changes, which is
/// what keeps relayout churn low.
/// </para>
/// <para>
/// The solo-page rule of §4b lives in the same objective: every transition pays
/// <see cref="DemandModel.SoloPageCost"/> for the single-photo pages its shape forces, so "a lame
/// photo must not get a page to itself" is a term in the cost, not a filter bolted on afterwards, and
/// it trades off against merging and splitting like everything else.
/// </para>
/// </summary>
public static class PagePartitioner
{
    private const double Infinity = double.PositiveInfinity;

    /// <summary>
    /// Partitions <paramref name="days"/> into page segments.
    /// </summary>
    /// <param name="days">The day sequence of one DP region (between Pinned anchors).</param>
    /// <param name="startParity">Parity of the region's first page.</param>
    /// <param name="weights">The tunables of doc 08 §14.</param>
    /// <param name="mergeAllowed">
    /// Structural gate on a candidate merge, called as <c>(firstDayIndex, dayCount)</c>: the engine
    /// passes a predicate that checks the R28 caps <em>and</em> that a multiDay template of that
    /// exact shape exists and can hold each day's text, so phase 4 can never be handed an
    /// unsatisfiable merge.
    /// </param>
    /// <param name="absorbAllowed">
    /// Structural gate on a candidate absorb, called as <c>(firstDayIndex, dayCount)</c>. Null
    /// disables the transition entirely, which restores the doc's original two-transition DP.
    /// </param>
    public static IReadOnlyList<PageSegment> Partition(
        IReadOnlyList<LayoutDay> days,
        PageSide startParity,
        LayoutWeights weights,
        Func<int, int, bool> mergeAllowed,
        Func<int, int, bool>? absorbAllowed = null)
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(mergeAllowed);

        var n = days.Count;
        if (n == 0) return [];

        var demand = new double[n];
        var textDemand = new double[n];

        // Prefix sums over the day sequence, so an absorb run's combined photo count and its share of
        // photos that have not earned a solo page (doc 08 §4b) are O(1) per candidate.
        var photosBefore = new int[n + 1];
        var unearnedBefore = new int[n + 1];

        for (var i = 0; i < n; i++)
        {
            demand[i] = DemandModel.Demand(days[i], weights);
            textDemand[i] = DemandModel.TextDemand(days[i], weights);

            var unearned = 0;
            foreach (var photo in days[i].Photos)
            {
                // An absorbed run always holds ≥ 2 photos, so the "genuinely the day's only photo"
                // clause of §4b can never apply inside one: only S/A photos earn a page there.
                if (!DemandModel.EarnsSoloPage(photo, 2, weights)) unearned++;
            }

            photosBefore[i + 1] = photosBefore[i] + days[i].Photos.Count;
            unearnedBefore[i + 1] = unearnedBefore[i] + unearned;
        }

        // dp[i, par] = best cost for days 1..i with the next page on side `par`.
        var dp = new double[n + 1, 2];
        var back = new Step?[n + 1, 2];
        for (var i = 0; i <= n; i++)
        {
            dp[i, 0] = Infinity;
            dp[i, 1] = Infinity;
        }

        dp[0, (int)startParity] = 0;

        for (var i = 1; i <= n; i++)
        {
            for (var par = 0; par < 2; par++)
            {
                // ── Merge endings: days j+1..i onto one multiDay page. ────────────────────────
                var earliest = Math.Max(0, i - weights.MaxDaysPerPage);
                for (var j = earliest; j <= i - 1; j++)
                {
                    var merged = i - j;
                    if (merged < 2) continue;                 // one day on one page is a split, not a merge
                    if (double.IsPositiveInfinity(dp[j, par])) continue;
                    if (!mergeAllowed(j, merged)) continue;

                    var d = 0.0;
                    for (var k = j; k < i; k++) d += demand[k];

                    var cost = dp[j, par]
                               + weights.FitWeight * DemandModel.FitCost(1, d)
                               + weights.MergeCost * (merged - 1);

                    Relax(dp, back, i, Flip(par), cost,
                        new Step(j, par, PageCount: 1, DayCount: merged, IsMerge: true, IsAbsorb: false,
                            ParityViolated: false, Demand: d));
                }

                // ── Absorb endings: days j+1..i flattened onto one day's pages (doc 08 §5). ───
                if (weights.EnableStragglerAbsorb && absorbAllowed is not null)
                {
                    for (var j = earliest; j <= i - 1; j++)
                    {
                        var merged = i - j;
                        if (merged < 2) continue;
                        if (double.IsPositiveInfinity(dp[j, par])) continue;
                        if (!absorbAllowed(j, merged)) continue;

                        var combinedPhotos = photosBefore[i] - photosBefore[j];
                        if (combinedPhotos == 0) continue;

                        var combinedUnearned = unearnedBefore[i] - unearnedBefore[j];
                        var combinedDemand = 0.0;
                        var combinedText = 0.0;
                        for (var d = j; d < i; d++)
                        {
                            combinedDemand += demand[d];
                            combinedText += textDemand[d];
                        }

                        var absorbMin = Math.Max(1, CeilDiv(combinedPhotos, weights.MaxSlotsPerPage));
                        var absorbMax = Math.Max(absorbMin, (int)Math.Ceiling(weights.SplitPageFactor * combinedDemand));
                        absorbMax = Math.Max(absorbMin, Math.Min(absorbMax, combinedPhotos));

                        var absorbSpread = combinedText > weights.MaxTextPerPage;
                        if (absorbSpread && combinedPhotos >= 2) absorbMin = Math.Max(absorbMin, 2);
                        if (absorbMax < absorbMin) absorbMax = absorbMin;

                        for (var k = absorbMin; k <= absorbMax; k++)
                        {
                            var violated = absorbSpread && (PageSide)par == PageSide.Right;
                            var cost = dp[j, par]
                                       + weights.FitWeight * DemandModel.FitCost(k, combinedDemand)
                                       + weights.AbsorbCost * (merged - 1)
                                       + SoloCost(combinedPhotos, combinedUnearned, k, weights)
                                       + (violated ? weights.ParityPenalty : 0);

                            Relax(dp, back, i, ParityAfter(par, k), cost,
                                new Step(j, par, PageCount: k, DayCount: merged, IsMerge: false, IsAbsorb: true,
                                    ParityViolated: violated, Demand: combinedDemand));
                        }
                    }
                }

                // ── Split endings: day i alone over k pages. ──────────────────────────────────
                if (double.IsPositiveInfinity(dp[i - 1, par])) continue;

                var day = days[i - 1];
                var photos = day.Photos.Count;
                if (photos == 0) continue;                    // no template holds zero photos (see §12)

                var d1 = demand[i - 1];
                var kMin = Math.Max(1, CeilDiv(photos, weights.MaxSlotsPerPage));
                var kMax = Math.Max(kMin, (int)Math.Ceiling(weights.SplitPageFactor * d1));
                kMax = Math.Min(kMax, photos);                // every emitted page needs ≥ 1 photo
                kMax = Math.Max(kMax, kMin);

                var needsSpread = textDemand[i - 1] > weights.MaxTextPerPage;
                var spreadPossible = needsSpread && photos >= 2;
                if (spreadPossible) kMin = Math.Max(kMin, 2);
                if (kMax < kMin) kMax = kMin;

                for (var k = kMin; k <= kMax; k++)
                {
                    if (k > photos) break;

                    var parityViolated = needsSpread && (PageSide)par == PageSide.Right;
                    var cost = dp[i - 1, par]
                               + weights.FitWeight * DemandModel.FitCost(k, d1)
                               + DemandModel.SoloPageCost(day.Photos, k, weights)
                               + (parityViolated ? weights.ParityPenalty : 0);

                    Relax(dp, back, i, ParityAfter(par, k), cost,
                        new Step(i - 1, par, PageCount: k, DayCount: 1, IsMerge: false, IsAbsorb: false,
                            ParityViolated: parityViolated, Demand: d1));
                }
            }
        }

        var bestPar = dp[n, 0] <= dp[n, 1] ? 0 : 1;
        if (double.IsPositiveInfinity(dp[n, bestPar]))
        {
            return Fallback(days, startParity, weights, demand);
        }

        // Backtrack, then reverse into reading order.
        var segments = new List<PageSegment>();
        var index = n;
        var parity = bestPar;
        while (index > 0)
        {
            var step = back[index, parity];
            if (step is null) return Fallback(days, startParity, weights, demand);

            var spread = 0.0;
            if (!step.IsMerge)
            {
                for (var d = step.PreviousDay; d < index; d++) spread += textDemand[d];
            }

            segments.Add(new PageSegment
            {
                FirstDayIndex = step.PreviousDay,
                DayCount = step.DayCount,
                PageCount = step.PageCount,
                IsMerge = step.IsMerge,
                IsAbsorb = step.IsAbsorb,
                StartSide = (PageSide)step.PreviousParity,
                TextNeedsSpread = !step.IsMerge && spread > weights.MaxTextPerPage,
                ParityViolated = step.ParityViolated,
                Demand = step.Demand,
            });

            index = step.PreviousDay;
            parity = step.PreviousParity;
        }

        segments.Reverse();
        return segments;
    }

    /// <summary>
    /// Splits a day's photos across <paramref name="pages"/> pages at the largest time gaps, so
    /// breakfast / park / dinner clusters fall out naturally (doc 08 §5). Every page gets between 1
    /// and <see cref="LayoutWeights.MaxSlotsPerPage"/> photos; equal gaps are broken by the seeded
    /// content hash, never by position. A part is cut down to a single photo only when that photo has
    /// earned a page to itself (§4b) — otherwise the cut pays
    /// <see cref="LayoutWeights.SoloPartPenalty"/> and the boundary moves.
    /// </summary>
    /// <param name="photos">The day's photos in chronological order.</param>
    /// <param name="pages">How many pages to cut into.</param>
    /// <param name="weights">Tunables (supplies the per-page slot ceiling).</param>
    /// <param name="seed">The book seed, for gap tie-breaks.</param>
    /// <param name="firstPageMax">Optional cap on the first page's photo count — used to leave room for journal text.</param>
    public static IReadOnlyList<IReadOnlyList<Photo>> SplitAtLargestGaps(
        IReadOnlyList<Photo> photos, int pages, LayoutWeights weights, ulong seed, int? firstPageMax = null)
    {
        ArgumentNullException.ThrowIfNull(photos);
        ArgumentNullException.ThrowIfNull(weights);

        var n = photos.Count;
        pages = Math.Clamp(pages, 1, Math.Max(1, n));
        if (pages == 1 || n <= 1) return [photos];

        var maxPer = Math.Max(1, weights.MaxSlotsPerPage);
        var firstMax = Math.Clamp(firstPageMax ?? maxPer, 1, maxPer);

        // gap[i] = the (jittered) time gap between photos[i-1] and photos[i], i = 1..n-1.
        var gap = new double[n];
        var maxGap = 0.0;
        for (var i = 1; i < n; i++)
        {
            var delta = (photos[i].TakenAt - photos[i - 1].TakenAt).TotalSeconds;
            gap[i] = delta < 0 ? 0 : delta;
            if (gap[i] > maxGap) maxGap = gap[i];
        }

        var jitterScale = (maxGap > 0 ? maxGap : 1.0) * 1e-9;
        for (var i = 1; i < n; i++)
        {
            gap[i] += LayoutRandom.Unit(seed, "gap|" + photos[i].ContentHash) * jitterScale;
        }

        // Gaps are normalized by the day's largest so the objective is scale-free, and every page
        // carries a balance penalty against the day's average. A real cluster boundary is worth ~1.0
        // and dwarfs the penalty, so breakfast / park / dinner still fall out; but when a day is one
        // even burst — every gap the same — the balance term is all that is left, and it produces
        // [6,6,6,6,6,5,5] instead of the [1,5,8,3,8,…] that nanoscale jitter used to pick.
        var scale = maxGap > 0 ? maxGap : 1.0;
        var target = (double)n / pages;
        var balance = Math.Max(0, weights.SplitBalanceWeight);

        double Penalty(int size)
        {
            if (balance <= 0 || target <= 0) return 0;
            var delta = (size - target) / target;
            return balance * delta * delta;
        }

        // The solo-page rule of §4b applied to the cut itself: a part of exactly one photo is a page
        // holding one photo, and it is only allowed when that photo earned it. The penalty is larger
        // than the largest possible normalized gap, so not even a day boundary — the biggest gap there
        // is, and exactly what an absorbed straggler sits behind — can strand a weak frame alone.
        var soloPenalty = Math.Max(0, weights.SoloPartPenalty);

        double PartCost(int start, int size) =>
            size == 1 && soloPenalty > 0 && !DemandModel.EarnsSoloPage(photos[start], n, weights)
                ? soloPenalty
                : 0;

        // best[i, j] = maximal total cut weight using j cuts among the first i photos, where the
        // last part ends at photo i-1. Parts have 1..maxPer photos (1..firstMax for the first).
        var best = new double[n + 1, pages + 1];
        var from = new int[n + 1, pages + 1];
        for (var i = 0; i <= n; i++)
        {
            for (var j = 0; j <= pages; j++)
            {
                best[i, j] = double.NegativeInfinity;
                from[i, j] = -1;
            }
        }

        best[0, 0] = 0;
        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= pages; j++)
            {
                var cap = j == 1 ? firstMax : maxPer;
                var lower = Math.Max(0, i - cap);
                for (var p = lower; p <= i - 1; p++)
                {
                    if (double.IsNegativeInfinity(best[p, j - 1])) continue;
                    var value = best[p, j - 1] + (p == 0 ? 0 : gap[p] / scale)
                                - Penalty(i - p) - PartCost(p, i - p);
                    if (value > best[i, j])
                    {
                        best[i, j] = value;
                        from[i, j] = p;
                    }
                }
            }
        }

        if (double.IsNegativeInfinity(best[n, pages]))
        {
            return EvenSplit(photos, pages);
        }

        var cuts = new List<int>();
        var at = n;
        for (var j = pages; j >= 1; j--)
        {
            var previous = from[at, j];
            if (previous < 0) return EvenSplit(photos, pages);
            if (previous > 0) cuts.Add(previous);
            at = previous;
        }

        cuts.Reverse();
        var result = new List<IReadOnlyList<Photo>>(pages);
        var start = 0;
        foreach (var cut in cuts)
        {
            result.Add(Slice(photos, start, cut - start));
            start = cut;
        }

        result.Add(Slice(photos, start, n - start));
        return result;
    }

    private static IReadOnlyList<IReadOnlyList<Photo>> EvenSplit(IReadOnlyList<Photo> photos, int pages)
    {
        var result = new List<IReadOnlyList<Photo>>(pages);
        var n = photos.Count;
        var start = 0;
        for (var i = 0; i < pages; i++)
        {
            var take = (n - start) / (pages - i);
            if (take < 1) take = 1;
            result.Add(Slice(photos, start, Math.Min(take, n - start)));
            start += take;
        }

        return result;
    }

    private static IReadOnlyList<Photo> Slice(IReadOnlyList<Photo> photos, int start, int count)
    {
        var slice = new List<Photo>(Math.Max(0, count));
        for (var i = start; i < start + count && i < photos.Count; i++) slice.Add(photos[i]);
        return slice;
    }

    private static IReadOnlyList<PageSegment> Fallback(
        IReadOnlyList<LayoutDay> days, PageSide startParity, LayoutWeights weights, double[] demand)
    {
        // Degenerate safety net: one page per photo-bearing day. Never throws, never loses a day.
        var segments = new List<PageSegment>(days.Count);
        var side = startParity;
        for (var i = 0; i < days.Count; i++)
        {
            var photos = days[i].Photos.Count;
            if (photos == 0) continue;
            var pages = Math.Max(1, CeilDiv(photos, weights.MaxSlotsPerPage));
            segments.Add(new PageSegment
            {
                FirstDayIndex = i,
                DayCount = 1,
                PageCount = pages,
                IsMerge = false,
                StartSide = side,
                Demand = demand[i],
            });

            side = (PageSide)ParityAfter((int)side, pages);
        }

        return segments;
    }

    private static void Relax(double[,] dp, Step?[,] back, int day, int parity, double cost, Step step)
    {
        if (cost < dp[day, parity])
        {
            dp[day, parity] = cost;
            back[day, parity] = step;
        }
    }

    private static int Flip(int parity) => parity == 0 ? 1 : 0;

    private static int ParityAfter(int parity, int pages) => pages % 2 == 0 ? parity : Flip(parity);

    private static int CeilDiv(int a, int b) => b <= 0 ? a : (a + b - 1) / b;

    /// <summary>
    /// <c>SOLO_PAGE_COST × forcedSoloPages × unearnedShare</c> from counts alone — the prefix-sum form
    /// of <see cref="DemandModel.SoloPageCost"/> used by the absorb transition (doc 08 §4b).
    /// </summary>
    private static double SoloCost(int photoCount, int unearned, int pages, LayoutWeights weights)
    {
        if (photoCount <= 0 || unearned <= 0) return 0;
        var forced = DemandModel.ForcedSoloPages(photoCount, pages);
        if (forced == 0) return 0;
        return weights.SoloPageCost * forced * ((double)unearned / photoCount);
    }

    private sealed record Step(
        int PreviousDay, int PreviousParity, int PageCount, int DayCount, bool IsMerge, bool IsAbsorb,
        bool ParityViolated, double Demand);
}
