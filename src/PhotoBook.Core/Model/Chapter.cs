using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoBook.Core.Model;

/// <summary>
/// One month of the book (R4), serialized as its own file <c>chapters/{year}-{month:00}.json</c> so
/// a chapter is a standalone, independently editable and diffable unit.
/// <para>
/// A chapter stores <b>no photo list</b>: membership is always computed from the photos' effective
/// dates (doc 03 §4 Decision), so re-dating a photo is one field write. Only placements are stored.
/// </para>
/// </summary>
public sealed record Chapter
{
    /// <summary>Schema version of this chapter file; currently 1 (doc 04 §5).</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>The book's calendar year; must equal <see cref="Book.Year"/>.</summary>
    public int Year { get; set; }

    /// <summary>The month, 1..12.</summary>
    public int Month { get; set; }

    /// <summary>Month-title page text override; null uses the localized month name (R24).</summary>
    public string? Title { get; set; }

    /// <summary>Sparse chapter-level style override — the middle level of the cascade (R23).</summary>
    public Style? StyleOverride { get; set; }

    /// <summary>The chapter's pages in reading order; page numbers are computed book-wide.</summary>
    public IList<Page> Pages { get; set; } = new List<Page>();

    /// <summary>
    /// Members written by a newer minor revision of the app, preserved verbatim so an older build
    /// round-trips them instead of silently deleting them (doc 04 §4 rule 6).
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalData { get; set; }

    /// <summary>The chapter's first day, useful for month naming and day grouping.</summary>
    public DateOnly FirstDay => new(Year, Month, 1);

    /// <summary>
    /// The photos of this chapter, computed from their effective dates — never stored
    /// (doc 03 §4 Decision). Excluded photos are gone from layout forever (R17).
    /// </summary>
    public IEnumerable<Photo> MembersOf(IEnumerable<Photo> allPhotos)
    {
        ArgumentNullException.ThrowIfNull(allPhotos);
        return allPhotos
            .Where(p => p.BelongsToChapter(Year, Month))
            .OrderBy(p => p.TakenAt)
            .ThenBy(p => p.Id, StringComparer.Ordinal);
    }
}
