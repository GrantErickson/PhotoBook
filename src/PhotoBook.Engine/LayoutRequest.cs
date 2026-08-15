using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;
using PhotoBook.Core.Templates;

namespace PhotoBook.Engine;

/// <summary>
/// Everything one Chapter's layout depends on (doc 08 §1). The engine reads this and nothing else:
/// no files, no clock, no globals. All analysis inputs — Tier, Focus Regions, pixel dimensions —
/// arrive precomputed on the <see cref="Photo"/> records.
/// </summary>
public sealed record ChapterInput
{
    /// <summary>The Chapter's calendar year.</summary>
    public int Year { get; init; }

    /// <summary>The Chapter's month, 1..12.</summary>
    public int Month { get; init; }

    /// <summary>Month-title override; null uses the localized month name at render time (R24).</summary>
    public string? Title { get; init; }

    /// <summary>
    /// The candidate photos. The engine keeps those that <see cref="Photo.BelongsToChapter"/> and
    /// are not <see cref="Photo.Excluded"/> (R17); order is irrelevant — a total order is imposed
    /// internally (doc 13 determinism test 2).
    /// </summary>
    public IReadOnlyList<Photo> Photos { get; init; } = [];

    /// <summary>The Chapter's journal entries (doc 11). Excluded entries are ignored.</summary>
    public IReadOnlyList<JournalEntry> JournalEntries { get; init; } = [];

    /// <summary>
    /// The Chapter's current pages in reading order, carrying their
    /// <see cref="Page.Pinned"/>/<see cref="Page.IsDetached"/> flags. Empty for a first layout.
    /// </summary>
    public IReadOnlyList<Page> ExistingPages { get; init; } = [];

    /// <summary>
    /// Photos the user deliberately moved to the Unplaced bin. They stay unplaced under every scope
    /// except <see cref="LayoutScope.InsertUnplaced"/> — user intent is never overridden (doc 08 §10).
    /// </summary>
    public IReadOnlySet<string> UnplacedPhotoIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The book's page-size id; only templates declaring the same id are eligible (R19).</summary>
    public string PageSize { get; init; } = PageGeometry.DefaultPageSizeId;

    /// <summary>Trim width of <see cref="PageSize"/> in inches.</summary>
    public double TrimWidthIn { get; init; } = PageGeometry.TrimWidthIn;

    /// <summary>Trim height of <see cref="PageSize"/> in inches.</summary>
    public double TrimHeightIn { get; init; } = PageGeometry.TrimHeightIn;
}

/// <summary>
/// Which pages a layout run may touch (doc 08 §10). Every scope honors Pinned pages; the three
/// non-<see cref="Whole"/> scopes are the editor's three relayout commands (R16).
/// </summary>
public abstract record LayoutScope
{
    private LayoutScope()
    {
    }

    /// <summary>Lay out the entire Chapter from scratch, keeping Pinned pages as anchors.</summary>
    public static LayoutScope WholeChapter { get; } = new Whole();

    /// <summary>Regenerate pages for the Unplaced bin only, inserting them in date order.</summary>
    public static LayoutScope Unplaced { get; } = new InsertUnplaced();

    /// <summary>Regenerate every unpinned page from <paramref name="fromPageNumber"/> (1-based) onward.</summary>
    public static LayoutScope From(int fromPageNumber) => new RestOfChapter(fromPageNumber);

    /// <summary>Re-run the pipeline for exactly one Day Group.</summary>
    public static LayoutScope Day(DateOnly date) => new SingleDay(date);

    /// <summary>The whole Chapter (doc 08 §10).</summary>
    public sealed record Whole : LayoutScope;

    /// <summary>"Auto-layout rest of chapter" from a 1-based page number (doc 08 §10.1, R16).</summary>
    /// <param name="FromPageNumber">The first page number, 1-based within the Chapter, that may be replaced.</param>
    public sealed record RestOfChapter(int FromPageNumber) : LayoutScope;

    /// <summary>"Lay out this day" (doc 08 §10.2).</summary>
    /// <param name="Date">The Day Group's calendar date.</param>
    public sealed record SingleDay(DateOnly Date) : LayoutScope;

    /// <summary>"Insert pages for the Unplaced bin" (doc 08 §10.3).</summary>
    public sealed record InsertUnplaced : LayoutScope;
}

/// <summary>
/// The engine's input (doc 08 §1). <c>LayoutEngine.LayoutChapter(req)</c> is a pure function of
/// this record: same request ⇒ byte-identical <see cref="LayoutResult"/>.
/// </summary>
public sealed record LayoutRequest
{
    /// <summary>The Chapter's photos, journal, existing pages and page size.</summary>
    public required ChapterInput Chapter { get; init; }

    /// <summary>The shipped template library (doc 07). Defaults to <see cref="TemplateLibrary.Default"/>.</summary>
    public TemplateLibrary Templates { get; init; } = TemplateLibrary.Default;

    /// <summary>
    /// The resolved style for this Chapter (<see cref="StyleResolver.Resolve(Book, Chapter?, Page?)"/>);
    /// supplies the text metrics behind capacity and fit (doc 10).
    /// </summary>
    public Style Style { get; init; } = BuiltInStyles.Default;

    /// <summary>The book Seed from <c>book.json</c> (kernel §5). The only source of randomness.</summary>
    public ulong Seed { get; init; }

    /// <summary>Which pages the run may replace (R16).</summary>
    public LayoutScope Scope { get; init; } = LayoutScope.WholeChapter;

    /// <summary>Parity of the Chapter's first regenerated page. Page index 0 of a Chapter is a Left page.</summary>
    public PageSide StartParity { get; init; } = PageSide.Left;

    /// <summary>Every tunable of doc 08 §14; defaults to the shipped values.</summary>
    public LayoutWeights Weights { get; init; } = LayoutWeights.Default;

    /// <summary>
    /// Text measurement. Null uses <see cref="DefaultTextMeasurer"/>, which is calibrated to doc 08's
    /// capacity formula so the engine runs headlessly in tests (doc 11, doc 13).
    /// </summary>
    public ITextMeasurer? TextMeasurer { get; init; }

    /// <summary>
    /// Compute <see cref="LayoutResult.AffectedPages"/> and diagnostics through phases 1–3 without
    /// producing pages — the cheap pass behind the R16 warning modal (doc 08 §10).
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// The editor's optional "include pinned pages (unpins them first)" checkbox (doc 09 §3.8).
    /// Detached pages are never included, whatever this says.
    /// </summary>
    public bool IncludePinnedPages { get; init; }

    /// <summary>
    /// Whether this run may (re)generate the Chapter's month-title page (R24). Only meaningful when
    /// the run owns the Chapter's first page; defaults to true.
    /// </summary>
    public bool GenerateMonthTitlePage { get; init; } = true;
}
