using PhotoBook.Core.Model;

namespace PhotoBook.Engine;

/// <summary>One photo bound to one slot by phase 5, with the cost terms that put it there.</summary>
/// <param name="Photo">The placed photo.</param>
/// <param name="Slot">The slot it goes in (already oriented for the page side).</param>
/// <param name="Cost">The total assignment cost of the pair.</param>
/// <param name="TierDistance">The pair's <c>tierDist</c> term, reused by <c>S_tier</c> (doc 08 §6).</param>
/// <param name="AspectCrop">The pair's <c>aspectCrop</c> term, reused by diagnostics.</param>
public readonly record struct SlotBinding(
    Photo Photo, ImageSlot Slot, double Cost, double TierDistance, double AspectCrop);

/// <summary>The result of one Hungarian solve — the bindings plus the aggregates phase 4 scores with.</summary>
public sealed record SlotAssignment
{
    /// <summary>The bindings, in slot (reading) order.</summary>
    public IReadOnlyList<SlotBinding> Bindings { get; init; } = [];

    /// <summary>Mean assignment cost — <c>1 − meanAssignmentCost</c> is <c>S_aspect</c> (doc 08 §6).</summary>
    public double MeanCost { get; init; }

    /// <summary>Mean <c>tierDist</c> — <c>1 − mean</c> is <c>S_tier</c> (doc 08 §6).</summary>
    public double MeanTierDistance { get; init; }

    /// <summary>An empty assignment (a page with no photo slots).</summary>
    public static SlotAssignment Empty { get; } = new();
}

/// <summary>
/// Phase 5 of doc 08 — the photo → slot cost matrix and its exact solution.
/// <code>
/// C[p][s] = 0.45·aspectCrop + 0.20·focusRisk + 0.20·tierDist + 0.15·chronoDisp + captionAdj
/// </code>
/// Matching is solved with <see cref="Hungarian"/> per page — per <em>section</em> for a multiDay
/// page, so each day's photos stay inside that day's section (the R28 invariant, enforced
/// structurally).
/// </summary>
public static class AssignmentCost
{
    private static readonly double LogThree = Math.Log(3.0);

    /// <summary>
    /// <c>aspectCrop</c> — log-space aspect mismatch, ≈ the fraction of the image lost to
    /// cover-cropping, saturating at a 3:1 mismatch. With
    /// <see cref="LayoutWeights.UseAspectTolerance"/> the slot's authored tolerance becomes a dead
    /// zone first (doc 07's reading); off by default so the shipped behavior is exactly doc 08 §7.
    /// </summary>
    public static double AspectCrop(Photo photo, ImageSlot slot, LayoutWeights weights)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(weights);

        var photoAspect = SmartCrop.AspectOf(photo);
        var slotAspect = slot.Aspect > 0 && double.IsFinite(slot.Aspect) ? slot.Aspect : 1.0;

        var ratio = photoAspect / slotAspect;
        if (weights.UseAspectTolerance)
        {
            var tolerance = Math.Clamp(slot.AspectTolerance, 0, 0.6);
            var symmetric = Math.Max(ratio, 1.0 / ratio);
            if (symmetric <= 1 + tolerance) return 0;
            return Math.Min(1.0, Math.Log(symmetric / (1 + tolerance)) / LogThree);
        }

        return Math.Min(1.0, Math.Abs(Math.Log(ratio)) / LogThree);
    }

    /// <summary>
    /// <c>tierDist</c> — <c>|rank(tierEff(p)) − rank(s.tierAffinity)| / 3</c>, zero for
    /// <see cref="TierAffinity.Any"/>. <c>tierEff</c> honors <c>userTierOverride</c>, which is how a
    /// promote steers a photo into hero slots (R26).
    /// </summary>
    public static double TierDistance(Photo photo, ImageSlot slot)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(slot);
        var slotRank = DemandModel.TierRank(slot.TierAffinity);
        if (slotRank is null) return 0;
        return Math.Abs(DemandModel.TierRank(photo.EffectiveTier) - slotRank.Value) / 3.0;
    }

    /// <summary>
    /// <c>chronoDisp</c> — <c>|timeIndex(p) − readingIndex(s)| / max(1, n−1)</c>. It preserves
    /// chronological reading order (R7) as pressure, not as a hard constraint.
    /// </summary>
    public static double ChronoDisplacement(int timeIndex, int readingIndex, int count) =>
        Math.Abs(timeIndex - readingIndex) / (double)Math.Max(1, count - 1);

    /// <summary><c>captionAdj</c> — captions need a <c>below</c> or <c>overlay</c> slot (R5).</summary>
    public static double CaptionAdjustment(Photo photo, ImageSlot slot, LayoutWeights weights)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(weights);
        var hasCaption = !string.IsNullOrWhiteSpace(photo.Caption);
        return hasCaption && slot.CaptionPolicy == CaptionPolicy.None ? weights.CaptionPenalty : 0;
    }

    /// <summary>The full doc 08 §7 cost of putting <paramref name="photo"/> in <paramref name="slot"/>.</summary>
    public static double Cost(
        Photo photo, ImageSlot slot, int timeIndex, int readingIndex, int count, LayoutWeights weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        var slotAspect = slot.Aspect > 0 && double.IsFinite(slot.Aspect) ? slot.Aspect : 1.0;
        return weights.AspectCropWeight * AspectCrop(photo, slot, weights)
               + weights.FocusRiskWeight * SmartCrop.FocusRisk(photo, slotAspect)
               + weights.TierDistanceWeight * TierDistance(photo, slot)
               + weights.ChronoWeight * ChronoDisplacement(timeIndex, readingIndex, count)
               + CaptionAdjustment(photo, slot, weights);
    }

    /// <summary>
    /// Solves one photos × slots matching exactly (doc 08 §7). <paramref name="photos"/> arrive in
    /// chronological order — their index <em>is</em> <c>timeIndex</c> — and
    /// <paramref name="readingIndex"/> gives each slot its position in the page's reading order.
    /// </summary>
    public static SlotAssignment Assign(
        IReadOnlyList<Photo> photos,
        IReadOnlyList<ImageSlot> slots,
        IReadOnlyList<int> readingIndex,
        LayoutWeights weights)
    {
        ArgumentNullException.ThrowIfNull(photos);
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(readingIndex);
        ArgumentNullException.ThrowIfNull(weights);

        var n = photos.Count;
        if (n == 0 || slots.Count != n) return SlotAssignment.Empty;

        var matrix = new double[n, n];
        for (var p = 0; p < n; p++)
        {
            for (var s = 0; s < n; s++)
            {
                matrix[p, s] = Cost(photos[p], slots[s], p, readingIndex[s], n, weights);
            }
        }

        var assignment = Hungarian.Solve(matrix);

        var bindings = new SlotBinding[n];
        var totalCost = 0.0;
        var totalTier = 0.0;
        for (var p = 0; p < n; p++)
        {
            var s = assignment[p];
            if (s < 0) s = p;
            var tier = TierDistance(photos[p], slots[s]);
            var aspect = AspectCrop(photos[p], slots[s], weights);
            bindings[s] = new SlotBinding(photos[p], slots[s], matrix[p, s], tier, aspect);
            totalCost += matrix[p, s];
            totalTier += tier;
        }

        return new SlotAssignment
        {
            Bindings = bindings,
            MeanCost = totalCost / n,
            MeanTierDistance = totalTier / n,
        };
    }

    /// <summary>
    /// The reading order of a page's slots: top to bottom in row bands, then left to right, with the
    /// authored id as the final tie-break. Computed on the <em>oriented</em> geometry, so a mirrored
    /// left page reads correctly (doc 07 "Mirroring"); set
    /// <see cref="LayoutWeights.UseGeometricReadingOrder"/> to false to use authored order verbatim.
    /// </summary>
    public static IReadOnlyList<int> ReadingOrder(IReadOnlyList<ImageSlot> slots, LayoutWeights weights)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(weights);

        var order = new int[slots.Count];
        if (!weights.UseGeometricReadingOrder)
        {
            for (var i = 0; i < slots.Count; i++) order[i] = i;
            return order;
        }

        var band = weights.ReadingRowBand > 0 ? weights.ReadingRowBand : 0.12;
        var ranked = Enumerable.Range(0, slots.Count)
            .OrderBy(i => (int)Math.Floor(slots[i].Rect.Y / band))
            .ThenBy(i => slots[i].Rect.X)
            .ThenBy(i => slots[i].Rect.Y)
            .ThenBy(i => slots[i].Id, StringComparer.Ordinal)
            .ToList();

        for (var rank = 0; rank < ranked.Count; rank++) order[ranked[rank]] = rank;
        return order;
    }
}
