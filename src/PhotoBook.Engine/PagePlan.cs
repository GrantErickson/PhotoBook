using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;

namespace PhotoBook.Engine;

/// <summary>What structural kind of page a plan needs (doc 08 §6 hard filter "Kind").</summary>
public enum PagePlanKind
{
    /// <summary>One day's photos on an ordinary page — <c>standard</c> or <c>fullBleed</c>.</summary>
    Standard,

    /// <summary>Several sparse days sharing a page — <c>multiDay</c> only (R28).</summary>
    MultiDay,

    /// <summary>The Chapter's opening page — <c>monthTitle</c> only (R24).</summary>
    MonthTitle,
}

/// <summary>One day's share of a merged <c>multiDay</c> page (doc 07 "Multi-day section templates").</summary>
/// <param name="Day">The Day Group filling this section.</param>
/// <param name="Photos">That day's photos, in chronological order.</param>
public sealed record PagePlanSection(LayoutDay Day, IReadOnlyList<Photo> Photos);

/// <summary>
/// A page the partitioner decided to emit, before a template has been chosen: the photo list, the
/// journal text it carries, the side it falls on, and the demand it represents. Phases 4–6 turn
/// this into a concrete <see cref="Page"/>.
/// </summary>
public sealed record PagePlan
{
    /// <summary>Which structural kind of template this page needs.</summary>
    public required PagePlanKind Kind { get; init; }

    /// <summary>Every photo on the page, in chronological order.</summary>
    public IReadOnlyList<Photo> Photos { get; init; } = [];

    /// <summary>The per-day sections; non-empty exactly when <see cref="Kind"/> is <see cref="PagePlanKind.MultiDay"/>.</summary>
    public IReadOnlyList<PagePlanSection> Sections { get; init; } = [];

    /// <summary>The journal entries whose text renders on this page (single-day pages).</summary>
    public IReadOnlyList<JournalEntry> Entries { get; init; } = [];

    /// <summary>Which side of the Spread the page falls on — decides mirroring and gutter side.</summary>
    public required PageSide Side { get; init; }

    /// <summary>The demand this page carries, in page units — the target for <c>S_pacing</c>'s coverage term.</summary>
    public double Demand { get; init; }

    /// <summary>The day this page belongs to (the first day, for a merged page).</summary>
    public required DateOnly PrimaryDate { get; init; }

    /// <summary>A stable key for jitter and page ids: content-derived, never positional (doc 08 §9).</summary>
    public required string StableKey { get; init; }

    /// <summary>True when the day's text wants both pages of a Spread and this is the first of them (§5).</summary>
    public bool TextNeedsSpread { get; init; }

    /// <summary>True when this page continues the previous page's journal chain across a Spread (doc 11).</summary>
    public bool ContinuesText { get; init; }

    /// <summary>
    /// Set by the spread-pair promotion pass (R22): the one template this page must use, bypassing
    /// the normal kind filter. Null for ordinary pages.
    /// </summary>
    public string? ForcedTemplateId { get; init; }

    /// <summary>
    /// Width multiplier applied to the slot when cropping, so a gutter-spanning photo is cropped once
    /// over the virtual 22 × 8.5 in Spread canvas and each page renders its half (R18, doc 07).
    /// </summary>
    public double SpanWidthFactor { get; init; } = 1.0;

    /// <summary>
    /// True when this page's photo also appears on the facing page of a spanning spread pair, so the
    /// photo must be counted once in placed/unplaced accounting (doc 07 "Spread pairs").
    /// </summary>
    public bool SharesPhotoWithFacingPage { get; init; }

    /// <summary>The journal paragraphs rendered on this page, in order.</summary>
    public IReadOnlyList<string> Paragraphs
    {
        get
        {
            var paragraphs = new List<string>();
            foreach (var entry in Entries) paragraphs.AddRange(entry.Paragraphs);
            return paragraphs;
        }
    }

    /// <summary>Total characters of journal text on this page — the <c>S_text</c> numerator.</summary>
    public int JournalChars
    {
        get
        {
            var total = 0;
            foreach (var entry in Entries) total += entry.CharacterCount;
            return total;
        }
    }

    /// <summary>True when any photo on the page is S-tier — the gate for full-bleed and spread pacing.</summary>
    public bool HasHeroPhoto
    {
        get
        {
            foreach (var photo in Photos)
            {
                if (photo.EffectiveTier == Tier.S) return true;
            }

            return false;
        }
    }
}

/// <summary>
/// The immutable per-run environment phases 4–6 need: style metrics, page geometry, the injected
/// text measurer, the seed and the tunables. Keeping it in one object is what lets every phase stay
/// a pure function of its inputs.
/// </summary>
public sealed class LayoutContext
{
    /// <summary>Creates a context for one engine run.</summary>
    public LayoutContext(
        TemplateCatalog catalog, Style style, double trimWidthIn, double trimHeightIn,
        ITextMeasurer measurer, ulong seed, LayoutWeights weights)
    {
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Style = style ?? throw new ArgumentNullException(nameof(style));
        Measurer = measurer ?? throw new ArgumentNullException(nameof(measurer));
        Weights = weights ?? throw new ArgumentNullException(nameof(weights));
        TrimWidthIn = trimWidthIn;
        TrimHeightIn = trimHeightIn;
        Seed = seed;
    }

    /// <summary>The indexed template library for this page size.</summary>
    public TemplateCatalog Catalog { get; }

    /// <summary>The resolved style (doc 10).</summary>
    public Style Style { get; }

    /// <summary>Page trim width in inches.</summary>
    public double TrimWidthIn { get; }

    /// <summary>Page trim height in inches.</summary>
    public double TrimHeightIn { get; }

    /// <summary>The injected text measurer (doc 11).</summary>
    public ITextMeasurer Measurer { get; }

    /// <summary>The book seed (kernel §5).</summary>
    public ulong Seed { get; }

    /// <summary>The tunables of doc 08 §14.</summary>
    public LayoutWeights Weights { get; }

    /// <summary>
    /// The doc 11 hard fit check: does the text fit the journal slot chain at current Style sizes?
    /// There is no auto font-shrink anywhere (kernel §9) — a "no" forces a roomier template.
    /// </summary>
    public bool FitsText(IReadOnlyList<string> paragraphs, IReadOnlyList<TextSlot> chain)
    {
        ArgumentNullException.ThrowIfNull(paragraphs);
        ArgumentNullException.ThrowIfNull(chain);

        var chars = 0;
        foreach (var paragraph in paragraphs) chars += paragraph?.Length ?? 0;
        if (chars == 0) return true;
        if (chain.Count == 0) return false;

        var width = TextCapacity.ColumnWidthIn(chain, TrimWidthIn);
        var needed = Measurer.MeasureHeightIn(paragraphs, Style.JournalText ?? BuiltInStyles.DefaultJournalText, width);
        var available = TextCapacity.AvailableHeightIn(chain, TrimHeightIn);
        return needed <= available + 1e-9;
    }

    /// <summary>Characters the chain holds, for the <c>S_text</c> fill ratio (doc 08 §6).</summary>
    public double CapacityChars(IReadOnlyList<TextSlot> chain) =>
        TextCapacity.CapacityChars(chain, Style, TrimWidthIn, TrimHeightIn);
}
