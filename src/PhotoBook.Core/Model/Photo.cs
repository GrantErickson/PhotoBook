namespace PhotoBook.Core.Model;

/// <summary>
/// The catalog record for one imported image (doc 03 §3), serialized in <c>photos.json</c>. A photo
/// owns everything the user can say <em>about a photo</em> independently of any page: its date,
/// edits, focus, quality, tags, exclusion and caption.
/// <para>
/// Identity is content identity: <see cref="Id"/> is <c>"ph-"</c> plus the first 16 hex characters
/// of the SHA-256 of the original bytes, so re-importing identical bytes is a no-op (doc 04 §7).
/// </para>
/// </summary>
public sealed record Photo
{
    /// <summary><c>"ph-" + contentHash[..16]</c>; stable forever (doc 04 §7).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Full 64-character lowercase SHA-256 of the original bytes; the dedupe key on re-import.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>The file name as it arrived, kept for human recognizability.</summary>
    public string OriginalFileName { get; set; } = string.Empty;

    /// <summary>Project-relative path of the archived original, e.g. <c>originals/3fa9…-IMG_1234.heic</c>. Immutable (R1).</summary>
    public string OriginalPath { get; set; } = string.Empty;

    /// <summary>Where the photo came from and when it was imported.</summary>
    public PhotoSourceRef Source { get; set; } = new();

    /// <summary>
    /// The effective capture date, stored as unzoned local wall-clock time — the date on the calendar
    /// where the photo was taken. Drives Chapter membership (R6).
    /// </summary>
    public DateTime TakenAt { get; set; }

    /// <summary>Provenance of <see cref="TakenAt"/> (kernel §10).</summary>
    public DateSource DateSource { get; set; } = DateSource.FileMtime;

    /// <summary>
    /// True when <see cref="TakenAt"/> came from the file mtime and is probably the copy date rather
    /// than the capture date. Badged in the grid, raised by preflight, cleared by a user edit.
    /// </summary>
    public bool DateUncertain { get; set; }

    /// <summary>Pixel width after EXIF orientation is applied.</summary>
    public int Width { get; set; }

    /// <summary>Pixel height after EXIF orientation is applied.</summary>
    public int Height { get; set; }

    /// <summary>Non-destructive parametric edits (R6, R11).</summary>
    public AdjustmentStack Adjustments { get; set; } = new();

    /// <summary>Fused focus regions — user intent plus derived detections (R25).</summary>
    public IList<FocusRegion> FocusRegions { get; set; } = new List<FocusRegion>();

    /// <summary>The fused quality signals and month percentile; null until analysis has run.</summary>
    public QualityScore? Quality { get; set; }

    /// <summary>The engine-computed, month-relative tier; null until analysis has run (R26).</summary>
    public Tier? Tier { get; set; }

    /// <summary>
    /// The user's promote/demote (R26). <b>Absolute</b>: it wins over <see cref="Tier"/> and is never
    /// re-derived, decayed, or recomputed when the month's photo set changes.
    /// </summary>
    public Tier? UserTierOverride { get; set; }

    /// <summary>Named people in the photo, from OneDrive or the user (doc 05).</summary>
    public IList<PersonTag> PersonTags { get; set; } = new List<PersonTag>();

    /// <summary>
    /// The R17 tombstone. An excluded photo keeps its catalog row forever, appears in no placement,
    /// bin, day group or export, and is never re-imported by a re-scan or re-sync. Only an explicit
    /// user "restore" clears it.
    /// </summary>
    public bool Excluded { get; set; }

    /// <summary>The photo's caption; most photos have none (R5).</summary>
    public string? Caption { get; set; }

    /// <summary>The item vanished from the source album or folder on a re-sync; the archived original stays (doc 05).</summary>
    public bool RemovedFromSource { get; set; }

    /// <summary>The source item's bytes changed since import; the Import Report offers a re-import (doc 05).</summary>
    public bool SourceModified { get; set; }

    /// <summary>The file could not be decoded; shown as a broken-image placeholder and listed in the Import Report (doc 05).</summary>
    public bool DecodeFailed { get; set; }

    /// <summary>
    /// The tier layout actually uses: the user's override wins absolutely, else the computed tier,
    /// else <see cref="Model.Tier.B"/> for a photo that has not been analyzed yet (kernel §4, R26).
    /// </summary>
    public Tier EffectiveTier => UserTierOverride ?? Tier ?? PhotoBook.Core.Model.Tier.B;

    /// <summary>Native aspect ratio (width / height) of the oriented image; NaN when unknown.</summary>
    public double Aspect => Height <= 0 ? double.NaN : (double)Width / Height;

    /// <summary>The calendar date of <see cref="TakenAt"/> — the day-grouping key (doc 08 phase 1).</summary>
    public DateOnly TakenOn => DateOnly.FromDateTime(TakenAt);

    /// <summary>
    /// True when this photo belongs to the given Chapter. Chapter membership is always computed from
    /// the effective date and never stored (doc 03 §4 Decision).
    /// </summary>
    public bool BelongsToChapter(int year, int month) =>
        !Excluded && TakenAt.Year == year && TakenAt.Month == month;
}
