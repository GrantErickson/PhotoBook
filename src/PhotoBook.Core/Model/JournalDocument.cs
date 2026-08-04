using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoBook.Core.Model;

/// <summary>
/// The root of <c>journal.json</c>: the parsed entries plus the unmatched-import report (doc 11,
/// doc 04 §3). Importing the same document twice is a strict no-op modulo
/// <see cref="JournalSource.ImportedAtUtc"/>.
/// </summary>
public sealed record JournalDocument
{
    /// <summary>Schema version of <c>journal.json</c>; currently 1 (doc 04 §5).</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>The source document, or null when no journal has been imported — R2 is optional.</summary>
    public JournalSource? Source { get; set; }

    /// <summary>The parsed entries, stored sorted by effective date.</summary>
    public IList<JournalEntry> Entries { get; set; } = new List<JournalEntry>();

    /// <summary>The import report shown after parsing.</summary>
    public JournalImportReport ImportReport { get; set; } = new();

    /// <summary>Members written by a newer minor revision of the app, preserved verbatim (doc 04 §4 rule 6).</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalData { get; set; }

    /// <summary>An empty journal — what a project without an imported journal holds.</summary>
    public static JournalDocument Empty => new();

    /// <summary>The non-excluded entries whose effective date falls on a given day.</summary>
    public IEnumerable<JournalEntry> EntriesOn(DateOnly date) =>
        Entries.Where(e => !e.Excluded && e.EffectiveDate == date);

    /// <summary>The non-excluded entries whose effective date falls in a given month, in date order.</summary>
    public IEnumerable<JournalEntry> EntriesIn(int year, int month) =>
        Entries.Where(e => !e.Excluded && e.EffectiveDate.Year == year && e.EffectiveDate.Month == month)
               .OrderBy(e => e.EffectiveDate)
               .ThenBy(e => e.Id, StringComparer.Ordinal);
}
