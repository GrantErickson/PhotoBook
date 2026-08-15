using PhotoBook.Core.Model;

namespace PhotoBook.Engine;

/// <summary>
/// One calendar day of a Chapter as the engine sees it (doc 08 §3): the day's photos in reading
/// order plus its atomic journal text. A day may be photos-only, journal-only, or both.
/// </summary>
public sealed record LayoutDay
{
    /// <summary>The calendar date.</summary>
    public required DateOnly Date { get; init; }

    /// <summary>The day's photos in chronological order, ties broken by content hash (doc 08 §3).</summary>
    public IReadOnlyList<Photo> Photos { get; init; } = [];

    /// <summary>The day's journal entries; doc 11 merges same-day entries, so this is normally 0 or 1.</summary>
    public IReadOnlyList<JournalEntry> Entries { get; init; } = [];

    /// <summary>Total characters of journal body text — the text-demand input (doc 08 §4).</summary>
    public int JournalChars
    {
        get
        {
            var total = 0;
            foreach (var entry in Entries) total += entry.CharacterCount;
            return total;
        }
    }

    /// <summary>The day's paragraphs in entry order — what <see cref="Core.Abstractions.ITextMeasurer"/> measures.</summary>
    public IReadOnlyList<string> Paragraphs
    {
        get
        {
            var paragraphs = new List<string>();
            foreach (var entry in Entries) paragraphs.AddRange(entry.Paragraphs);
            return paragraphs;
        }
    }

    /// <summary>True when the day carries journal text.</summary>
    public bool HasJournal => Entries.Count > 0 && JournalChars > 0;

    /// <summary>True when the day has journal text but no photos — the degenerate case of doc 08 §12.</summary>
    public bool IsJournalOnly => Photos.Count == 0 && Entries.Count > 0;

    /// <summary>
    /// A content hash of everything about this day that layout depends on: photo ids, effective
    /// tiers, focus regions and journal text. Editing day 14 changes only day 14's hash, which is
    /// what makes segment memoization and "small edit ⇒ small relayout" work (doc 08 §13).
    /// </summary>
    public string DayHash
    {
        get
        {
            var builder = new System.Text.StringBuilder(64);
            builder.Append(Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            foreach (var photo in Photos)
            {
                builder.Append('|').Append(photo.Id).Append(':').Append((int)photo.EffectiveTier);
                foreach (var region in photo.FocusRegions)
                {
                    builder.Append(';').Append((int)region.Kind).Append(',')
                        .Append(region.Rect.X.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                        .Append(region.Rect.Y.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                        .Append(region.Rect.W.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                        .Append(region.Rect.H.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            foreach (var entry in Entries) builder.Append('#').Append(entry.Id).Append(':').Append(entry.CharacterCount);
            return LayoutRandom.Fnv1a64(builder.ToString()).ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}

/// <summary>
/// Phase 1 of doc 08 — day grouping. Photos and journal entries share one date axis, which is
/// exactly why dated journal text interleaves with photos for free (R2).
/// </summary>
public static class DayGrouping
{
    /// <summary>
    /// Groups photos and journal entries by calendar date (doc 08 §3). The result is ordered by
    /// date; within a day, photos are ordered by capture time with the content hash as the total-order
    /// tie-break, so the grouping is independent of input enumeration order (doc 13).
    /// </summary>
    /// <param name="photos">The Chapter's non-excluded photos.</param>
    /// <param name="entries">The Chapter's non-excluded journal entries.</param>
    public static IReadOnlyList<LayoutDay> GroupDays(
        IEnumerable<Photo> photos, IEnumerable<JournalEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(photos);
        ArgumentNullException.ThrowIfNull(entries);

        var photosByDate = new SortedDictionary<DateOnly, List<Photo>>();
        foreach (var photo in photos)
        {
            if (photo.Excluded) continue;
            if (!photosByDate.TryGetValue(photo.TakenOn, out var bucket))
            {
                bucket = [];
                photosByDate[photo.TakenOn] = bucket;
            }

            bucket.Add(photo);
        }

        var entriesByDate = new SortedDictionary<DateOnly, List<JournalEntry>>();
        foreach (var entry in entries)
        {
            if (entry.Excluded) continue;
            if (!entriesByDate.TryGetValue(entry.EffectiveDate, out var bucket))
            {
                bucket = [];
                entriesByDate[entry.EffectiveDate] = bucket;
            }

            bucket.Add(entry);
        }

        var dates = new SortedSet<DateOnly>(photosByDate.Keys);
        foreach (var date in entriesByDate.Keys) dates.Add(date);

        var days = new List<LayoutDay>(dates.Count);
        foreach (var date in dates)
        {
            var dayPhotos = photosByDate.TryGetValue(date, out var p) ? SortPhotos(p) : [];
            var dayEntries = entriesByDate.TryGetValue(date, out var e) ? SortEntries(e) : [];
            if (dayPhotos.Count == 0 && dayEntries.Count == 0) continue;
            days.Add(new LayoutDay { Date = date, Photos = dayPhotos, Entries = dayEntries });
        }

        return days;
    }

    /// <summary>
    /// The engine's total order over photos: capture time, then content hash, then id (doc 08 §3).
    /// Never array position — that is what makes shuffled inputs produce identical books (doc 13).
    /// </summary>
    public static IReadOnlyList<Photo> SortPhotos(IEnumerable<Photo> photos)
    {
        ArgumentNullException.ThrowIfNull(photos);
        return photos
            .OrderBy(p => p.TakenAt)
            .ThenBy(p => p.ContentHash, StringComparer.Ordinal)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<JournalEntry> SortEntries(IEnumerable<JournalEntry> entries) =>
        entries
            .OrderBy(e => e.EffectiveDate)
            .ThenBy(e => e.Occurrence)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();
}
