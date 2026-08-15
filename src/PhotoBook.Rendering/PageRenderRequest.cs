using PhotoBook.Core.Model;

namespace PhotoBook.Rendering;

/// <summary>
/// Everything <see cref="PageRenderer.Render"/> needs to draw one page — the same object whether the
/// canvas belongs to an on-screen preview or to <c>SKDocument.CreatePdf</c>. That is the whole point
/// of ADR-0003: there is one draw path, and the target only chooses the pixel tier.
/// </summary>
public sealed record PageRenderRequest
{
    /// <summary>The book, for the global style level, the year and the page size.</summary>
    public required Book Book { get; init; }

    /// <summary>The page to draw.</summary>
    public required Page Page { get; init; }

    /// <summary>The page's resolved template — already mirrored for a left page (see <see cref="Page.ResolveTemplate"/>).</summary>
    public required Template Template { get; init; }

    /// <summary>Where the normalized template coordinates land.</summary>
    public required PageGeometryMapper Geometry { get; init; }

    /// <summary>Where the photo pixels come from.</summary>
    public required IRenderImageSource Images { get; init; }

    /// <summary>The chapter, for the chapter style level and the month title (R24).</summary>
    public Chapter? Chapter { get; init; }

    /// <summary>The photo catalog — captions, crops, dimensions and adjustments.</summary>
    public PhotoCatalog Photos { get; init; } = PhotoCatalog.Empty;

    /// <summary>The parsed journal, resolved through the page's <see cref="Page.JournalAssignments"/>.</summary>
    public JournalDocument Journal { get; init; } = JournalDocument.Empty;

    /// <summary>The font library; substitutions are reported as diagnostics.</summary>
    public FontLibrary Fonts { get; init; } = FontLibrary.Default;

    /// <summary>Which output is being drawn.</summary>
    public RenderTarget Target { get; init; } = RenderTarget.Screen;

    /// <summary>Which half of the sheet this page occupies; <see cref="PageHalf.Full"/> for a single-page sheet.</summary>
    public PageHalf Half { get; init; } = PageHalf.Full;

    /// <summary>
    /// Which side of a spread this page is, for gutter-spanning photos (R18). Only consulted when
    /// <see cref="FacingTemplate"/> is supplied or <see cref="Half"/> is not <see cref="PageHalf.Full"/>.
    /// </summary>
    public PairSide Side { get; init; } = PairSide.Right;

    /// <summary>The facing page, when this page belongs to a spread — required to resolve <c>spanId</c> slots.</summary>
    public Page? FacingPage { get; init; }

    /// <summary>The facing page's resolved template.</summary>
    public Template? FacingTemplate { get; init; }

    /// <summary>The book-wide page number, quoted in diagnostics and used by preflight navigation.</summary>
    public int? BookPageNumber { get; init; }

    /// <summary>
    /// The already-resolved style. When null the renderer resolves the
    /// <c>book → chapter → page</c> cascade itself (doc 10 §2).
    /// </summary>
    public Style? ResolvedStyle { get; init; }

    /// <summary>
    /// Whether empty slots get the amber flag of doc 09 §3.6. Defaults to on-screen only — the flag
    /// is an editor affordance and must <b>never</b> reach the PDF.
    /// </summary>
    public bool? ShowEmptySlotFlags { get; init; }

    /// <summary>Whether to paint the page background; off when a caller has already cleared the sheet.</summary>
    public bool DrawBackground { get; init; } = true;

    /// <summary>Whether to draw the diagonal DRAFT watermark; defaults to on for the draft target.</summary>
    public bool? DrawDraftWatermark { get; init; }

    /// <summary>Whether the month-title page shows the optional year subtitle (doc 10 §7).</summary>
    public bool ShowYearSubtitle { get; init; } = true;

    /// <summary>
    /// Editor-only guides: the safe area, the trim box and the gutter caution zone. Never drawn in
    /// export regardless of this flag.
    /// </summary>
    public bool DrawGuides { get; init; }

    /// <summary>Resolved value of <see cref="ShowEmptySlotFlags"/>.</summary>
    public bool EmptySlotFlagsVisible => ShowEmptySlotFlags ?? Target == RenderTarget.Screen;

    /// <summary>Resolved value of <see cref="DrawDraftWatermark"/>.</summary>
    public bool WatermarkVisible => DrawDraftWatermark ?? Target == RenderTarget.Draft150Dpi;

    /// <summary>The effective style for this page — the cascade result, or the supplied override.</summary>
    public Style Style => ResolvedStyle ?? StyleResolver.Resolve(Book, Chapter, Page);
}
