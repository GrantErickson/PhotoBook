using PhotoBook.Core.Model;

namespace PhotoBook.Core.Templates;

/// <summary>
/// What the shipped library can actually build at one page size (R19, doc 07, doc 12 "Other page
/// sizes").
/// <para>
/// Page size is data everywhere else in the app — geometry, renderer, exporter and preflight all
/// derive from the <see cref="PageSizeSpec"/> the book names — but the template library deliberately
/// does <b>not</b> stretch: a template declares a <c>pageSize</c> and is eligible only for pages
/// whose id matches exactly. So "is this size usable?" is a question about the library, not about
/// the geometry, and it has to be asked before a book is retargeted rather than discovered
/// afterwards as a chapter that will not lay out.
/// </para>
/// </summary>
public sealed record PageSizeAvailability
{
    /// <summary>The trim this is about.</summary>
    public required PageSizeSpec Size { get; init; }

    /// <summary>Every authored template carrying this size id.</summary>
    public required int TemplateCount { get; init; }

    /// <summary>Month-title templates — a chapter cannot open without one (R24).</summary>
    public required int MonthTitleCount { get; init; }

    /// <summary>Multi-day sectioned templates, which is how several thin days share a page (R28).</summary>
    public required int MultiDayCount { get; init; }

    /// <summary>Complete left+right spread pairs for full-spread photos (R18, R22).</summary>
    public required int SpreadPairCount { get; init; }

    /// <summary>The most photos any standard template at this size holds; 0 when there are none (R20).</summary>
    public required int MaxPhotosPerPage { get; init; }

    /// <summary>The size id, for binding and comparison.</summary>
    public string PageSizeId => Size.Id;

    /// <summary>True when anything at all is authored for this size.</summary>
    public bool HasLayouts => TemplateCount > 0;

    /// <summary>
    /// True when a whole chapter can be laid out here: standard pages to put photos on, and a
    /// month-title template to open with. Anything less would produce a book with holes in it.
    /// </summary>
    public bool CanLayOutChapter => TemplateCount > 0 && MonthTitleCount > 0;

    /// <summary>A one-line count for the picker — "60 layouts · up to 8 photos a page".</summary>
    public string Summary => TemplateCount == 0
        ? "No layouts authored yet"
        : $"{TemplateCount} layout{(TemplateCount == 1 ? "" : "s")} · up to {MaxPhotosPerPage} " +
          $"photo{(MaxPhotosPerPage == 1 ? "" : "s")} a page";

    /// <summary>
    /// Why a size cannot be used, in the user's terms, or null when it can. Adding a size is two data
    /// changes and only the template set is missing, so the sentence says exactly that rather than
    /// implying the app cannot print the shape.
    /// </summary>
    public string? Blocker => TemplateCount == 0
        ? $"No page layouts have been authored for {Size.DisplayName} yet. The printer geometry " +
          "supports it, but templates are authored per page size and never stretched across shapes, " +
          "so this size has nothing to lay pages out with."
        : MonthTitleCount == 0
            ? $"{Size.DisplayName} has {TemplateCount} layout{(TemplateCount == 1 ? "" : "s")} but no " +
              "month-title layout, so no chapter could open properly."
            : null;

    /// <summary>Counts what the library holds for one page size.</summary>
    /// <param name="library">The template library to measure; defaults to the shipped one.</param>
    /// <param name="size">The trim to measure it against.</param>
    public static PageSizeAvailability For(TemplateLibrary library, PageSizeSpec size)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(size);

        var templates = library.ByPageSize(size.Id);
        return new PageSizeAvailability
        {
            Size = size,
            TemplateCount = templates.Count,
            MonthTitleCount = templates.Count(t => t.Kind == TemplateKind.MonthTitle),
            MultiDayCount = templates.Count(t => t.Kind == TemplateKind.MultiDay),
            SpreadPairCount = templates
                .Where(t => t.Kind == TemplateKind.SpreadPair && t.Pair is { Side: PairSide.Left })
                .Count(t => templates.Any(o =>
                    o.Kind == TemplateKind.SpreadPair &&
                    o.Pair is { Side: PairSide.Right } &&
                    string.Equals(o.Pair.PairId, t.Pair!.PairId, StringComparison.Ordinal))),
            MaxPhotosPerPage = templates.Count == 0 ? 0 : templates.Max(t => t.PhotoCount),
        };
    }

    /// <summary>Counts the library against every size a print profile prints, in profile order.</summary>
    /// <param name="library">The template library to measure; defaults to the shipped one.</param>
    /// <param name="profile">The print profile whose sizes are on offer.</param>
    public static IReadOnlyList<PageSizeAvailability> ForProfile(TemplateLibrary library, PrintProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return [.. profile.PageSizes.Select(s => For(library, s))];
    }
}

/// <summary>
/// What retargeting a book at another page size would do to the pages it already has (R19).
/// <para>
/// The honest part is that nothing re-flows. A page laid out at 11 × 8.5 keeps a template authored
/// for 11 × 8.5, and after the book moves to another size that page's template no longer matches the
/// book it lives in. Re-running the engine fixes every page it is allowed to touch — but not a
/// <see cref="Page.IsDetached"/> page, whose geometry the user built by hand and which no layout run
/// will ever replace (R15). Those have to be rebuilt or reverted deliberately, so they are counted
/// separately and said out loud.
/// </para>
/// </summary>
public sealed record PageSizeChange
{
    /// <summary>The size the book is on now.</summary>
    public required string FromPageSizeId { get; init; }

    /// <summary>The size the book would move to.</summary>
    public required string ToPageSizeId { get; init; }

    /// <summary>What the library can build at the new size.</summary>
    public required PageSizeAvailability Availability { get; init; }

    /// <summary>Pages currently laid out across every chapter.</summary>
    public required int LaidOutPageCount { get; init; }

    /// <summary>Pages whose template is authored for some other size — every one of them, after a real move.</summary>
    public required int MismatchedPageCount { get; init; }

    /// <summary>Mismatched pages that are hand-built, which a re-layout will not touch (R15).</summary>
    public required int MismatchedDetachedPageCount { get; init; }

    /// <summary>True when the two ids differ — a move at all.</summary>
    public bool IsChange => !string.Equals(FromPageSizeId, ToPageSizeId, StringComparison.Ordinal);

    /// <summary>True when accepting this would leave pages that need the engine run over them again.</summary>
    public bool NeedsRelayout => MismatchedPageCount > 0;

    /// <summary>True when the move is safe to make: the new size has layouts to build with.</summary>
    public bool IsAllowed => !IsChange || Availability.CanLayOutChapter;

    /// <summary>
    /// The sentence the settings surface shows before anything happens. Never optimistic: it names
    /// the page counts, and it names the hand-built pages a re-layout cannot rescue.
    /// </summary>
    public string Warning
    {
        get
        {
            if (!IsChange)
            {
                return string.Empty;
            }

            if (Availability.Blocker is { } blocker)
            {
                return blocker;
            }

            if (MismatchedPageCount == 0)
            {
                return $"Nothing is laid out yet, so moving to {Availability.Size.DisplayName} costs nothing.";
            }

            var text = $"{Plural(MismatchedPageCount, "page")} " +
                       $"{(MismatchedPageCount == 1 ? "is" : "are")} laid out with templates authored for " +
                       $"{FromPageSizeId}. Templates are never stretched to a new shape, so those pages " +
                       "have to be laid out again before the book is printable.";

            if (MismatchedDetachedPageCount > 0)
            {
                text += $" {Plural(MismatchedDetachedPageCount, "page")} " +
                        $"{(MismatchedDetachedPageCount == 1 ? "was" : "were")} laid out by hand; " +
                        "re-running layout leaves those alone, so you will need to rebuild " +
                        $"{(MismatchedDetachedPageCount == 1 ? "it" : "them")} yourself.";
            }

            return text;
        }
    }

    /// <summary>
    /// Measures a proposed page-size change against the book's chapters.
    /// </summary>
    /// <param name="chapters">The book's chapters.</param>
    /// <param name="templates">Resolves a library template id, typically <see cref="TemplateLibrary.Find"/>.</param>
    /// <param name="fromPageSizeId">The book's current size id.</param>
    /// <param name="toPageSizeId">The size id being proposed.</param>
    /// <param name="availability">What the library holds at the proposed size.</param>
    public static PageSizeChange Inspect(
        IEnumerable<Chapter> chapters,
        Func<string, Template?> templates,
        string fromPageSizeId,
        string toPageSizeId,
        PageSizeAvailability availability)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        ArgumentNullException.ThrowIfNull(templates);

        var pages = chapters.SelectMany(c => c.Pages).ToList();
        var mismatched = pages
            .Where(p => !string.Equals(PageSizeOf(p, templates), toPageSizeId, StringComparison.Ordinal))
            .ToList();

        return new PageSizeChange
        {
            FromPageSizeId = fromPageSizeId,
            ToPageSizeId = toPageSizeId,
            Availability = availability,
            LaidOutPageCount = pages.Count,
            MismatchedPageCount = mismatched.Count,
            MismatchedDetachedPageCount = mismatched.Count(p => p.IsDetached),
        };
    }

    /// <summary>
    /// The page-size id a page is actually built for: its detached snapshot's, else its library
    /// template's. A page whose reference resolves to nothing is counted as mismatched — an
    /// unresolvable template is not evidence that the page fits the new size.
    /// </summary>
    private static string? PageSizeOf(Page page, Func<string, Template?> templates) =>
        page.DetachedTemplate?.PageSize
        ?? (page.TemplateRef is { } id ? templates(id)?.PageSize : null);

    private static string Plural(int count, string noun) =>
        $"{count} {noun}{(count == 1 ? string.Empty : "s")}";
}
