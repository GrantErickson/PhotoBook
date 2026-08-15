namespace PhotoBook.Rendering;

/// <summary>Which kernel §11 gate a finding came from, in doc 12's display order.</summary>
public enum PreflightCheck
{
    /// <summary>An <see cref="PhotoBook.Core.Model.ImageSlot"/> with no photo (R14).</summary>
    EmptySlot,

    /// <summary>A day's journal text that does not fit its slot (doc 11's atomic-text ladder).</summary>
    TextOverflow,

    /// <summary>Page size not in the profile, or a page count outside min/max/multiple.</summary>
    ProfileConstraint,

    /// <summary>A placement whose effective print resolution is below 200 DPI.</summary>
    EffectiveDpi,

    /// <summary>Photos in the book's Unplaced bin that will not print.</summary>
    UnplacedBin,

    /// <summary>Placed photos whose date was guessed (kernel §10).</summary>
    DateUncertain,

    /// <summary>A caption longer than its band, hard-truncated with an ellipsis (doc 10 §4).</summary>
    CaptionTruncated,

    /// <summary>A style font family that is not installed and was substituted (doc 10 §3).</summary>
    FontFallback,

    /// <summary>A page whose template reference cannot be resolved.</summary>
    MissingTemplate,
}

/// <summary>Whether a finding blocks export or merely needs acknowledging.</summary>
public enum PreflightSeverity
{
    /// <summary>Requires one explicit "I understand, export anyway" acknowledgement (doc 12).</summary>
    Warning,

    /// <summary>Disables the Export button. No override.</summary>
    Error,
}

/// <summary>
/// One structured, user-readable preflight finding, carrying the navigation target doc 12 requires:
/// every row in the dialog is a hyperlink that closes it and goes to the offending page, slot or
/// photo in the editor.
/// </summary>
/// <param name="Check">Which gate produced it.</param>
/// <param name="Severity">Whether it blocks export.</param>
/// <param name="Title">A short label, e.g. "Empty image slot".</param>
/// <param name="Detail">A sentence the user can act on.</param>
/// <param name="BookPageNumber">The page to navigate to, when the finding is page-scoped.</param>
/// <param name="PageId">The page's id.</param>
/// <param name="SlotId">The slot to select, when the finding is slot-scoped.</param>
/// <param name="PhotoId">The photo to select, when the finding is photo-scoped.</param>
/// <param name="ChapterMonth">The chapter, when the finding is chapter-scoped.</param>
/// <param name="Value">A number the detail quotes, such as the computed DPI.</param>
public sealed record PreflightFinding(
    PreflightCheck Check,
    PreflightSeverity Severity,
    string Title,
    string Detail,
    int? BookPageNumber = null,
    string? PageId = null,
    string? SlotId = null,
    string? PhotoId = null,
    int? ChapterMonth = null,
    double? Value = null)
{
    /// <inheritdoc/>
    public override string ToString() =>
        BookPageNumber is { } page ? $"{Severity}: {Title} (page {page}) — {Detail}" : $"{Severity}: {Title} — {Detail}";
}

/// <summary>
/// The result of a preflight run: findings grouped by severity, in doc 12's display order, plus the
/// two questions the dialog actually asks — may I export, and must the user acknowledge anything?
/// </summary>
/// <param name="Findings">Every finding, ordered by check then page number.</param>
/// <param name="PageCount">How many pages were in scope.</param>
/// <param name="Scope">The scope that was checked.</param>
public sealed record PreflightReport(
    IReadOnlyList<PreflightFinding> Findings,
    int PageCount,
    ExportScope Scope)
{
    /// <summary>The blocking findings.</summary>
    public IReadOnlyList<PreflightFinding> Errors =>
        [.. Findings.Where(f => f.Severity == PreflightSeverity.Error)];

    /// <summary>The findings that need one acknowledgement between them.</summary>
    public IReadOnlyList<PreflightFinding> Warnings =>
        [.. Findings.Where(f => f.Severity == PreflightSeverity.Warning)];

    /// <summary>True when nothing blocks export.</summary>
    public bool CanExport => Errors.Count == 0;

    /// <summary>True when the dialog must show its acknowledgement checkbox.</summary>
    public bool RequiresAcknowledgement => Warnings.Count > 0;

    /// <summary>True when the book is clean — no errors and no warnings.</summary>
    public bool IsClean => Findings.Count == 0;

    /// <summary>A one-line summary for the dialog header and the log.</summary>
    public string Summarize() => IsClean
        ? $"Preflight passed: {PageCount} page(s) in scope, nothing to report."
        : $"Preflight found {Errors.Count} error(s) and {Warnings.Count} warning(s) across {PageCount} page(s).";
}
