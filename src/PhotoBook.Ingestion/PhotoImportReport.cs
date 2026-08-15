using System.Globalization;
using System.Text;

namespace PhotoBook.Ingestion;

/// <summary>What happened to one item during an import or re-sync (doc 05, "Delta re-sync").</summary>
public enum PhotoImportOutcome
{
    /// <summary>New bytes: copied into <c>originals/</c> and catalogued.</summary>
    Added,

    /// <summary>Already in the catalog with the same content hash — no copy, no second row (doc 04 §7).</summary>
    SkippedDuplicate,

    /// <summary>Matches an excluded photo. <c>excluded: true</c> wins, always: re-sync never resurrects it (R17).</summary>
    SkippedExcluded,

    /// <summary>Not an image type PhotoBook accepts; listed so it is visibly skipped, not silently lost.</summary>
    SkippedUnsupported,

    /// <summary>Same source item, new bytes: flagged <c>sourceModified</c>; the report offers a re-import.</summary>
    ModifiedAtSource,

    /// <summary>Re-imported modified bytes under a new hash, keeping the photo's identity and user work.</summary>
    Reimported,

    /// <summary>Catalogued before, absent from the source now: flagged <c>removedFromSource</c>, never deleted.</summary>
    RemovedFromSource,

    /// <summary>Copied and catalogued, but the file could not be parsed: flagged <c>decodeFailed</c>.</summary>
    DecodeFailed,

    /// <summary>The item could not be read or copied at all; one bad file never aborts the batch.</summary>
    Failed,
}

/// <summary>One line of the Import Report.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="FileName">The file name as the source knows it.</param>
/// <param name="PhotoId">The catalog id, when the item reached the catalog.</param>
/// <param name="Message">Detail worth showing the user; null when the outcome says it all.</param>
/// <param name="SourceId">The source's own id (absolute path, or Graph <c>driveItem</c> id).</param>
public sealed record PhotoImportEntry(
    PhotoImportOutcome Outcome,
    string FileName,
    string? PhotoId = null,
    string? Message = null,
    string? SourceId = null);

/// <summary>
/// The photo-side Import Report (doc 05): what an import or re-sync added, skipped, excluded, flagged
/// and failed. It is a pure result object — the caller decides how to show it and when to persist the
/// catalog it mutated.
/// </summary>
public sealed record PhotoImportReport
{
    /// <summary>Every line of the report, in a stable order (outcome, then file name).</summary>
    public IReadOnlyList<PhotoImportEntry> Entries { get; init; } = [];

    /// <summary>How many items the source enumerated.</summary>
    public int ItemsSeen { get; init; }

    /// <summary>True when enumeration ran to completion; false when it was cancelled or failed part-way.</summary>
    public bool Completed { get; init; }

    /// <summary>The source's display name, for the report header.</summary>
    public string SourceName { get; init; } = string.Empty;

    /// <summary>Wall-clock duration of the import.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Report lines with a given outcome.</summary>
    public IEnumerable<PhotoImportEntry> Of(PhotoImportOutcome outcome) => Entries.Where(e => e.Outcome == outcome);

    /// <summary>Count of report lines with a given outcome.</summary>
    public int Count(PhotoImportOutcome outcome) => Entries.Count(e => e.Outcome == outcome);

    /// <summary>Photos copied in and catalogued by this run.</summary>
    public int AddedCount => Count(PhotoImportOutcome.Added);

    /// <summary>Items already in the catalog by content hash.</summary>
    public int DuplicateCount => Count(PhotoImportOutcome.SkippedDuplicate);

    /// <summary>Items skipped because the matching photo is excluded (R17).</summary>
    public int ExcludedCount => Count(PhotoImportOutcome.SkippedExcluded);

    /// <summary>Items whose file type PhotoBook does not accept.</summary>
    public int UnsupportedCount => Count(PhotoImportOutcome.SkippedUnsupported);

    /// <summary>Items that failed to read, download or copy.</summary>
    public int FailedCount => Count(PhotoImportOutcome.Failed);

    /// <summary>True when there is anything worth putting in front of the user.</summary>
    public bool HasFindings =>
        ExcludedCount > 0 || UnsupportedCount > 0 || FailedCount > 0 ||
        Count(PhotoImportOutcome.DecodeFailed) > 0 ||
        Count(PhotoImportOutcome.ModifiedAtSource) > 0 ||
        Count(PhotoImportOutcome.RemovedFromSource) > 0;

    /// <summary>
    /// A one-line summary in the voice of the Import Report, e.g.
    /// <c>"12 photos added, 4 already imported, 3 excluded photos skipped"</c>.
    /// </summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            void Add(int count, string singular, string plural) =>
                parts.Add(string.Create(CultureInfo.CurrentCulture, $"{count} {(count == 1 ? singular : plural)}"));

            if (AddedCount > 0) Add(AddedCount, "photo added", "photos added");
            if (DuplicateCount > 0) Add(DuplicateCount, "already imported", "already imported");
            if (ExcludedCount > 0) Add(ExcludedCount, "excluded photo skipped", "excluded photos skipped");
            if (UnsupportedCount > 0) Add(UnsupportedCount, "unsupported file skipped", "unsupported files skipped");
            if (Count(PhotoImportOutcome.Reimported) > 0)
                Add(Count(PhotoImportOutcome.Reimported), "photo re-imported", "photos re-imported");
            if (Count(PhotoImportOutcome.ModifiedAtSource) > 0)
                Add(Count(PhotoImportOutcome.ModifiedAtSource), "changed at the source", "changed at the source");
            if (Count(PhotoImportOutcome.RemovedFromSource) > 0)
                Add(Count(PhotoImportOutcome.RemovedFromSource), "no longer in the source", "no longer in the source");
            if (Count(PhotoImportOutcome.DecodeFailed) > 0)
                Add(Count(PhotoImportOutcome.DecodeFailed), "unreadable image", "unreadable images");
            if (FailedCount > 0) Add(FailedCount, "failure", "failures");

            if (parts.Count == 0) return "Nothing to import — the source has no new photos.";

            var text = new StringBuilder();
            for (var i = 0; i < parts.Count; i++)
            {
                if (i > 0) text.Append(i == parts.Count - 1 ? " and " : ", ");
                text.Append(parts[i]);
            }

            return text.Append('.').ToString();
        }
    }
}
