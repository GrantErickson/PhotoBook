using PhotoBook.Core.Model;

namespace PhotoBook.Analysis.Fusion;

/// <summary>
/// The month-relative half of R26: rank every non-excluded, analyzed photo of a Chapter by its fused
/// raw score and map the rank to a <see cref="Tier"/> at the kernel §4 boundaries — <b>S</b> top 10%,
/// <b>A</b> next 25%, <b>B</b> next 45%, <b>C</b> bottom 20%.
/// <para>
/// Why relative: a phone snap in December competes with December, not with July's golden-hour
/// vacation shots. Tier drives slot-size affinity in template scoring, so it must express "good
/// <em>for this month</em>".
/// </para>
/// <para>
/// <b>The user's override is never touched.</b> <see cref="Photo.UserTierOverride"/> is absolute
/// (kernel §4): an overridden photo still takes part in the ranking pool and still gets a computed
/// <see cref="Photo.Tier"/> for the "B, promoted to S" display, but nothing here ever writes,
/// clears or re-derives the override.
/// </para>
/// </summary>
public static class TierAssigner
{
    /// <summary>Percentile at or above which a photo is <see cref="Tier.S"/>.</summary>
    public const double TierSPercentile = 90;

    /// <summary>Percentile at or above which a photo is <see cref="Tier.A"/>.</summary>
    public const double TierAPercentile = 65;

    /// <summary>Percentile at or above which a photo is <see cref="Tier.B"/>; below it, <see cref="Tier.C"/>.</summary>
    public const double TierBPercentile = 20;

    /// <summary>
    /// Ranks one month's photos and writes <see cref="QualityScore.MonthPercentile"/> and
    /// <see cref="Photo.Tier"/>. Photos that are excluded (R17) or not yet analyzed are left alone
    /// and stay out of the pool.
    /// </summary>
    /// <param name="monthPhotos">The photos of a single Chapter; membership is the caller's business.</param>
    public static TierAssignmentResult AssignMonth(IEnumerable<Photo> monthPhotos)
    {
        ArgumentNullException.ThrowIfNull(monthPhotos);

        var all = monthPhotos as IReadOnlyCollection<Photo> ?? monthPhotos.ToList();
        var pool = all
            .Where(p => p is { Excluded: false, Quality: not null })
            .OrderByDescending(p => p.Quality!.Fused)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToList();

        var counts = new Dictionary<Tier, int> { [Tier.S] = 0, [Tier.A] = 0, [Tier.B] = 0, [Tier.C] = 0 };
        var overridden = 0;

        for (var i = 0; i < pool.Count; i++)
        {
            var photo = pool[i];
            var percentile = PercentRank(i, pool.Count);
            photo.Quality!.MonthPercentile = percentile;

            var tier = photo.Quality.TierFromPercentile;
            photo.Tier = tier;
            counts[tier]++;
            if (photo.UserTierOverride is not null) overridden++;
        }

        return new TierAssignmentResult(pool.Count, all.Count - pool.Count, overridden, counts);
    }

    /// <summary>Ranks the photos of one Chapter, selecting them from the whole catalog by effective date.</summary>
    /// <param name="allPhotos">Every photo in the book.</param>
    /// <param name="year">Chapter year.</param>
    /// <param name="month">Chapter month, 1..12.</param>
    public static TierAssignmentResult AssignChapter(IEnumerable<Photo> allPhotos, int year, int month)
    {
        ArgumentNullException.ThrowIfNull(allPhotos);
        return AssignMonth(allPhotos.Where(p => p.BelongsToChapter(year, month)).ToList());
    }

    /// <summary>
    /// Ranks every month present in the set, grouped by the photos' own effective dates — the
    /// operation behind "photo re-dated across months" and "photo excluded/re-included", which
    /// change a month's pool (doc 06 recomputation triggers).
    /// </summary>
    /// <param name="photos">Any set of photos; grouping by year and month happens here.</param>
    public static IReadOnlyDictionary<(int Year, int Month), TierAssignmentResult> AssignAllMonths(IEnumerable<Photo> photos)
    {
        ArgumentNullException.ThrowIfNull(photos);

        var results = new Dictionary<(int Year, int Month), TierAssignmentResult>();
        foreach (var group in photos.Where(p => !p.Excluded).GroupBy(p => (p.TakenAt.Year, p.TakenAt.Month)))
            results[group.Key] = AssignMonth(group.ToList());

        return results;
    }

    /// <summary>
    /// The percent rank of a photo at zero-based descending <paramref name="index"/> in a pool of
    /// <paramref name="count"/>: the best photo is 100, the worst 0, evenly spaced. This is the
    /// "rank-ceiling at the boundaries" rule of doc 06 — with the bands applied to it, a month
    /// always has a top photo in <see cref="Tier.S"/> and a bottom photo in <see cref="Tier.C"/>
    /// however few photos it holds.
    /// </summary>
    /// <param name="index">Zero-based index in descending score order.</param>
    /// <param name="count">Pool size.</param>
    public static double PercentRank(int index, int count)
    {
        if (count <= 1) return 100;
        var clamped = Math.Clamp(index, 0, count - 1);
        return 100.0 * (count - 1 - clamped) / (count - 1);
    }

    /// <summary>
    /// The tier for a percentile, using Core's single definition of the kernel §4 bands
    /// (<see cref="QualityScore.TierFromPercentile"/>) rather than a second copy of them.
    /// </summary>
    public static Tier TierForPercentile(double percentile) =>
        new QualityScore { MonthPercentile = percentile }.TierFromPercentile;
}
