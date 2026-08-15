namespace PhotoBook.Ingestion;

/// <summary>
/// Knobs on one import or re-sync run (doc 05). The defaults are the behavior the docs describe;
/// tests pin <see cref="ImportedAtUtc"/> to keep <c>photos.json</c> byte-stable.
/// </summary>
public sealed record PhotoImportOptions
{
    /// <summary>The defaults: 4 concurrent transfers, removals detected, modified items flagged not swapped.</summary>
    public static PhotoImportOptions Default { get; } = new();

    /// <summary>
    /// How many items are fetched and hashed at once. 4 matches the OneDrive download budget of doc 05;
    /// 1 makes a run strictly sequential, which is what deterministic tests want.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = 4;

    /// <summary>
    /// Flag catalogued photos of this source that the source no longer lists as
    /// <see cref="PhotoBook.Core.Model.Photo.RemovedFromSource"/>. Only ever runs when enumeration
    /// completed — a cancelled sync must not accuse every unvisited photo of vanishing.
    /// </summary>
    public bool DetectRemovals { get; init; } = true;

    /// <summary>
    /// When a source item's bytes changed, swap the new bytes in under their new hash while keeping the
    /// photo's identity, dates, adjustments, tier override and placements (doc 05, "Item modified at
    /// source"). Off by default: re-import is an explicit act offered by the Import Report.
    /// </summary>
    public bool ReimportModifiedSources { get; init; }

    /// <summary>
    /// Photo ids the user chose to re-import from the Import Report's per-photo <em>Re-import</em>
    /// action. Those photos swap in new bytes even when <see cref="ReimportModifiedSources"/> is off;
    /// every other changed item is still only flagged.
    /// </summary>
    public IReadOnlySet<string>? ReimportPhotoIds { get; init; }

    /// <summary>
    /// The import timestamp written to <see cref="PhotoBook.Core.Model.PhotoSourceRef.ImportedAtUtc"/>;
    /// null means "now".
    /// </summary>
    public DateTime? ImportedAtUtc { get; init; }

    /// <summary>
    /// Set the read-only file attribute on archived originals (doc 04 §7: nothing writes into
    /// <c>originals/</c> after the copy).
    /// </summary>
    public bool MarkOriginalsReadOnly { get; init; } = true;
}

/// <summary>Progress for the job queue's status line while an import runs.</summary>
/// <param name="ItemsSeen">Items enumerated so far.</param>
/// <param name="Added">Photos added so far.</param>
/// <param name="CurrentFileName">The item being worked on.</param>
public sealed record PhotoImportProgress(int ItemsSeen, int Added, string CurrentFileName);
