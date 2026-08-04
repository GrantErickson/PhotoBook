using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoBook.Core.Model;

/// <summary>
/// The root of <c>photos.json</c>: the whole photo catalog (doc 04 §3). Photos are stored sorted by
/// <see cref="Photo.Id"/> so two saves of the same model are byte-identical (doc 04 §4 rule 5).
/// Excluded rows are never deleted — the tombstone <em>is</em> the exclusion mechanism (R17).
/// </summary>
public sealed record PhotoCatalog
{
    /// <summary>Schema version of <c>photos.json</c>; currently 1 (doc 04 §5).</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Every imported photo, including excluded tombstones, sorted by id.</summary>
    public IList<Photo> Photos { get; set; } = new List<Photo>();

    /// <summary>
    /// Members written by a newer minor revision of the app, preserved verbatim on round-trip
    /// (doc 04 §4 rule 6).
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalData { get; set; }

    /// <summary>An empty catalog — what a freshly created project holds.</summary>
    public static PhotoCatalog Empty => new();

    /// <summary>Finds a photo by id, or null.</summary>
    public Photo? Find(string photoId) =>
        Photos.FirstOrDefault(p => string.Equals(p.Id, photoId, StringComparison.Ordinal));

    /// <summary>Finds a photo by full content hash — the dedupe key on re-import (doc 04 §7).</summary>
    public Photo? FindByContentHash(string contentHash) =>
        Photos.FirstOrDefault(p => string.Equals(p.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The photos of one Chapter, computed from effective dates and ordered chronologically
    /// (doc 03 §4 Decision, R7). Excluded photos never appear.
    /// </summary>
    public IEnumerable<Photo> InChapter(int year, int month) =>
        Photos.Where(p => p.BelongsToChapter(year, month))
              .OrderBy(p => p.TakenAt)
              .ThenBy(p => p.Id, StringComparer.Ordinal);

    /// <summary>
    /// The Outside-book tray: non-excluded photos whose effective date left the book's year,
    /// typically after a re-date (R6). They are never deleted — moving the date back brings them home.
    /// </summary>
    public IEnumerable<Photo> OutsideBookTray(int bookYear) =>
        Photos.Where(p => !p.Excluded && p.TakenAt.Year != bookYear)
              .OrderBy(p => p.TakenAt)
              .ThenBy(p => p.Id, StringComparer.Ordinal);
}
