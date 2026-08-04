namespace PhotoBook.Core.Model;

/// <summary>
/// <b>Computed, never stored</b> (doc 03 §7). The engine's unit of layout demand: all non-excluded
/// photos and journal entries sharing one calendar date, photos in chronological order (R7).
/// Day groups are recomputed from <c>photos.json</c> plus <c>journal.json</c> at engine time; sparse
/// adjacent days merge onto one page (R28) and photo-heavy days split across pages.
/// </summary>
public sealed record DayGroup
{
    /// <summary>The calendar date this group covers.</summary>
    public DateOnly Date { get; set; }

    /// <summary>The day's photo ids, ordered by <see cref="Photo.TakenAt"/>.</summary>
    public IList<string> PhotoIds { get; set; } = new List<string>();

    /// <summary>The day's journal entry ids.</summary>
    public IList<string> JournalEntryIds { get; set; } = new List<string>();

    /// <summary>True when the day has neither photos nor journal text; such a date simply does not appear in the book.</summary>
    public bool IsEmpty => PhotoIds.Count == 0 && JournalEntryIds.Count == 0;

    /// <summary>
    /// Builds the day groups of one Chapter from the catalog and the journal — the input to phase 1
    /// of the layout engine (doc 08 §3). Dates with neither photos nor entries are omitted; the
    /// result is ordered by date, and photos within a day by capture time then id, so the grouping
    /// is deterministic (kernel §7).
    /// </summary>
    /// <param name="photos">The photo catalog, or any photo sequence.</param>
    /// <param name="journal">The journal, or null when no journal was imported (R2 is optional).</param>
    /// <param name="year">The Chapter's year.</param>
    /// <param name="month">The Chapter's month, 1..12.</param>
    public static IReadOnlyList<DayGroup> BuildForChapter(
        IEnumerable<Photo> photos,
        JournalDocument? journal,
        int year,
        int month)
    {
        ArgumentNullException.ThrowIfNull(photos);

        var groups = new SortedDictionary<DateOnly, DayGroup>();

        foreach (var photo in photos.Where(p => p.BelongsToChapter(year, month))
                                    .OrderBy(p => p.TakenAt)
                                    .ThenBy(p => p.Id, StringComparer.Ordinal))
        {
            GetOrAdd(groups, photo.TakenOn).PhotoIds.Add(photo.Id);
        }

        foreach (var entry in journal?.EntriesIn(year, month) ?? Enumerable.Empty<JournalEntry>())
        {
            GetOrAdd(groups, entry.EffectiveDate).JournalEntryIds.Add(entry.Id);
        }

        return groups.Values.Where(g => !g.IsEmpty).ToList();

        static DayGroup GetOrAdd(SortedDictionary<DateOnly, DayGroup> map, DateOnly date)
        {
            if (!map.TryGetValue(date, out var group))
            {
                group = new DayGroup { Date = date };
                map[date] = group;
            }

            return group;
        }
    }
}
