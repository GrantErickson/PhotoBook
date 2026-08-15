using PhotoBook.Core.Model;

namespace PhotoBook.Engine;

/// <summary>What an engine run wants to tell the user (doc 08 §1, §12; surfaced by preflight, doc 12).</summary>
public enum LayoutDiagnosticKind
{
    /// <summary>A day's journal text needs both pages of a Spread but parity forced it onto a right page (§5).</summary>
    TextNeedsSpread,

    /// <summary>No template could hold the day's journal text; it will render clipped and preflight blocks export (§12).</summary>
    TextOverflow,

    /// <summary>The primary Focus Region is larger than the slot's maximal crop window; part of it is cropped away (§8).</summary>
    FocusClipped,

    /// <summary>A face or person region could not be moved clear of the gutter caution zone or safe margin (§8).</summary>
    FaceNearGutter,

    /// <summary>A slot was emitted with no photo — the amber state of R14; only happens in degenerate cases.</summary>
    EmptySlot,

    /// <summary>A photo could not be placed and went to the Unplaced bin.</summary>
    UnplaceablePhoto,

    /// <summary>A page carries substantially more demand than one page holds comfortably.</summary>
    Crowding,

    /// <summary>A journal-only day's text rode along on a neighbouring day's page (§12).</summary>
    JournalOnlyDayCarried,

    /// <summary>
    /// A weak straggler day — one photo that has not earned a page to itself — was absorbed onto a
    /// neighbouring day's page instead of standing alone (§4b, §5).
    /// </summary>
    StragglerAbsorbed,

    /// <summary>No template in the library satisfied the page's hard filters at all.</summary>
    NoTemplate,

    /// <summary>Nothing in scope could be regenerated — every page in scope is Pinned (§12).</summary>
    NothingToDo,
}

/// <summary>How much a diagnostic matters.</summary>
public enum LayoutSeverity
{
    /// <summary>Informational; the layout is fine.</summary>
    Info,

    /// <summary>Worth showing in preflight; the page is usable.</summary>
    Warning,

    /// <summary>Preflight blocks export until the user acts (doc 12).</summary>
    Error,
}

/// <summary>One thing the engine noticed while laying out a Chapter (doc 08 §1).</summary>
public sealed record LayoutDiagnostic
{
    /// <summary>What happened.</summary>
    public required LayoutDiagnosticKind Kind { get; init; }

    /// <summary>How much it matters.</summary>
    public LayoutSeverity Severity { get; init; } = LayoutSeverity.Warning;

    /// <summary>A culture-invariant, developer-readable explanation. UI strings are the app's job.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>The 1-based Chapter page number, when the diagnostic is about a page.</summary>
    public int? PageNumber { get; init; }

    /// <summary>The photo it is about, when applicable.</summary>
    public string? PhotoId { get; init; }

    /// <summary>The slot it is about, when applicable.</summary>
    public string? SlotId { get; init; }

    /// <summary>The Day Group it is about, when applicable.</summary>
    public DateOnly? Date { get; init; }
}

/// <summary>
/// The engine's output (doc 08 §1). <see cref="Pages"/> is the Chapter's complete page list after
/// regenerated pages are spliced around Pinned anchors; <see cref="AffectedPages"/> is what the
/// R16 warning modal lists.
/// </summary>
public sealed record LayoutResult
{
    /// <summary>The Chapter's pages in reading order, Pinned anchors preserved by reference.</summary>
    public IReadOnlyList<Page> Pages { get; init; } = [];

    /// <summary>
    /// 1-based page numbers of the <em>input</em> Chapter that this run replaces — the list the R16
    /// warning shows. Empty for <see cref="LayoutScope.InsertUnplaced"/> and when everything in
    /// scope is Pinned.
    /// </summary>
    public IReadOnlyList<int> AffectedPages { get; init; } = [];

    /// <summary>
    /// 1-based page numbers of the input Chapter that new pages are inserted after — what the
    /// insert-Unplaced modal previews (doc 08 §10.3). <c>0</c> means "before the first page".
    /// </summary>
    public IReadOnlyList<int> InsertedAfterPages { get; init; } = [];

    /// <summary>1-based page numbers of the input Chapter kept because they are Pinned or Detached.</summary>
    public IReadOnlyList<int> PinnedPagesKept { get; init; } = [];

    /// <summary>
    /// Photos of the Chapter that ended up on no page: the ones the user had already unplaced plus
    /// anything the engine could not place. Placed ∪ Unplaced ∪ Excluded = the Chapter, disjoint.
    /// </summary>
    public IReadOnlyList<string> UnplacedPhotoIds { get; init; } = [];

    /// <summary>Everything the engine noticed, in emission order.</summary>
    public IReadOnlyList<LayoutDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>How many pages the run produced (for a dry run: how many it <em>would</em> produce).</summary>
    public int GeneratedPageCount { get; init; }

    /// <summary>True when this result came from <see cref="LayoutRequest.DryRun"/> and carries no new pages.</summary>
    public bool IsDryRun { get; init; }
}
