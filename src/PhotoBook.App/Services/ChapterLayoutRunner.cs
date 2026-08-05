using System.Globalization;
using PhotoBook.Core.Model;
using PhotoBook.Engine;
using PhotoBook.Rendering;

namespace PhotoBook.App.Services;

/// <summary>
/// Deep copies of the mutable model records the editor edits in place. The domain records are
/// <c>record</c>s with settable properties and <see cref="IList{T}"/> members, so <c>with { }</c>
/// alone would share those lists — an undo snapshot taken that way would mutate along with the
/// live model and restore nothing.
/// </summary>
public static class ModelClone
{
    /// <summary>A deep copy of a template, including its slots, text slots, sections and pair link.</summary>
    public static Template? CloneTemplate(Template? template) => template is null ? null : template with
    {
        Slots = [.. template.Slots.Select(s => s with { })],
        TextSlots = [.. template.TextSlots.Select(t => t with { })],
        Sections = template.Sections is null
            ? null
            : [.. template.Sections.Select(s => s with
            {
                SlotIds = new List<string>(s.SlotIds),
                TextSlotIds = new List<string>(s.TextSlotIds),
            })],
        Pair = template.Pair is null ? null : template.Pair with { },
    };

    /// <summary>A deep copy of a (possibly sparse) style, block by block.</summary>
    public static Style? CloneStyle(Style? style) => style is null ? null : new Style
    {
        JournalText = style.JournalText is null ? null : style.JournalText with { },
        CaptionText = style.CaptionText is null ? null : style.CaptionText with { },
        MonthTitle = style.MonthTitle is null ? null : style.MonthTitle with { },
        ImageBorder = style.ImageBorder is null ? null : style.ImageBorder with { },
        OverlayScrim = style.OverlayScrim is null ? null : style.OverlayScrim with { },
        Background = style.Background is null ? null : style.Background with { },
    };

    /// <summary>A deep copy of one page, keeping a distinct object graph from the original.</summary>
    public static Page ClonePage(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page with
        {
            DetachedTemplate = CloneTemplate(page.DetachedTemplate),
            StyleOverride = CloneStyle(page.StyleOverride),
            Placements = [.. page.Placements.Select(p => p with { })],
            JournalAssignments = [.. page.JournalAssignments.Select(j => j with
            {
                EntryIds = new List<string>(j.EntryIds),
            })],
        };
    }
}

/// <summary>
/// A before/after snapshot of everything a layout run can change in one chapter: the page list and
/// each page's own content. Doc 09 §4 asks the three R16 commands to commit as <em>one</em> undo
/// entry — "a before/after snapshot of the affected pages" — so this is that snapshot, and
/// <see cref="Restore"/> is the whole of both directions.
/// <para>
/// Page identity is preserved: pages that survive a run (Pinned and Detached anchors, which the
/// engine hands back by reference) are restored into the same instances the rest of the UI is
/// already holding, so nothing is left pointing at an orphan.
/// </para>
/// </summary>
public sealed class ChapterPagesSnapshot
{
    private readonly Page[] _order;
    private readonly Page[] _content;

    private ChapterPagesSnapshot(Page[] order, Page[] content)
    {
        _order = order;
        _content = content;
    }

    /// <summary>How many pages the chapter had when this was taken.</summary>
    public int PageCount => _order.Length;

    /// <summary>Captures the chapter's pages: their order, their identities and their contents.</summary>
    public static ChapterPagesSnapshot Capture(Chapter chapter)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        var order = chapter.Pages.ToArray();
        return new ChapterPagesSnapshot(order, [.. order.Select(ModelClone.ClonePage)]);
    }

    /// <summary>Puts the chapter back exactly as it was, in place.</summary>
    public void Restore(Chapter chapter)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        for (var i = 0; i < _order.Length; i++)
        {
            var page = _order[i];
            var saved = _content[i];

            page.TemplateRef = saved.TemplateRef;
            page.DetachedTemplate = ModelClone.CloneTemplate(saved.DetachedTemplate);
            page.Mirrored = saved.Mirrored;
            page.Pinned = saved.Pinned;
            page.StyleOverride = ModelClone.CloneStyle(saved.StyleOverride);
            page.Placements = [.. saved.Placements.Select(p => p with { })];
            page.JournalAssignments = [.. saved.JournalAssignments.Select(j => j with
            {
                EntryIds = new List<string>(j.EntryIds),
            })];
        }

        chapter.Pages = [.. _order];
    }
}

/// <summary>
/// One undo entry covering a whole engine run over a chapter (doc 09 §3.8, §4): a before snapshot,
/// an after snapshot, and nothing in between. <c>Ctrl+Z</c> restores the entire previous state of
/// the chapter — pages, placements, journal bindings and the Pinned/Detached flags that rode along
/// with the run — in one step.
/// </summary>
public sealed class ChapterPagesCommand : IUndoableCommand
{
    private readonly Chapter _chapter;
    private readonly ChapterPagesSnapshot _before;
    private readonly ChapterPagesSnapshot _after;
    private readonly Action? _afterEach;

    /// <summary>Creates the entry from two snapshots taken either side of the run.</summary>
    /// <param name="description">The Edit-menu text, e.g. "Auto-layout rest of chapter".</param>
    /// <param name="chapter">The chapter both snapshots belong to.</param>
    /// <param name="before">The chapter as it was.</param>
    /// <param name="after">The chapter as the run left it.</param>
    /// <param name="afterEach">Called after every restore so the shell can rebuild its page list.</param>
    public ChapterPagesCommand(
        string description,
        Chapter chapter,
        ChapterPagesSnapshot before,
        ChapterPagesSnapshot after,
        Action? afterEach = null)
    {
        Description = description;
        _chapter = chapter;
        _before = before;
        _after = after;
        _afterEach = afterEach;
    }

    /// <inheritdoc/>
    public string Description { get; }

    /// <inheritdoc/>
    public void Do()
    {
        _after.Restore(_chapter);
        _afterEach?.Invoke();
    }

    /// <inheritdoc/>
    public void Undo()
    {
        _before.Restore(_chapter);
        _afterEach?.Invoke();
    }
}

/// <summary>Which of the three R16 auto-layout commands a plan belongs to (doc 09 §3.8).</summary>
public enum LayoutCommandKind
{
    /// <summary>"Re-layout this day" — one Day Group only.</summary>
    Day,

    /// <summary>"Auto-layout rest of chapter" — every unpinned page from here on.</summary>
    RestOfChapter,

    /// <summary>"Insert pages for Unplaced" — new pages only; existing pages are untouched.</summary>
    InsertUnplaced,
}

/// <summary>
/// What a dry run says will happen — everything doc 09 §3.8's warning dialog has to state
/// <em>before</em> anything changes: the concrete page numbers, which pages are protected and why,
/// and the button label that names the size of the operation.
/// </summary>
public sealed record LayoutPlan
{
    /// <summary>Which command this plan is for.</summary>
    public required LayoutCommandKind Kind { get; init; }

    /// <summary>The scope handed to the engine.</summary>
    public required LayoutScope Scope { get; init; }

    /// <summary>The chapter month, 1..12.</summary>
    public required int Month { get; init; }

    /// <summary>True when this plan was computed with "include pinned pages" on.</summary>
    public bool IncludePinnedPages { get; init; }

    /// <summary>1-based chapter page numbers this run replaces.</summary>
    public IReadOnlyList<int> AffectedPages { get; init; } = [];

    /// <summary>Pages kept because the user pinned them.</summary>
    public IReadOnlyList<int> PinnedPages { get; init; } = [];

    /// <summary>Pages kept because their layout is hand-built; never re-laid out, whatever the checkbox says.</summary>
    public IReadOnlyList<int> DetachedPages { get; init; } = [];

    /// <summary>Pages that new pages are inserted after; <c>0</c> means "before the first page".</summary>
    public IReadOnlyList<int> InsertedAfterPages { get; init; } = [];

    /// <summary>How many pages the run produces.</summary>
    public int GeneratedPageCount { get; init; }

    /// <summary>How many photos are waiting in the Unplaced bin.</summary>
    public int UnplacedPhotoCount { get; init; }

    /// <summary>How many pages the chapter has right now — the backdrop the dialog draws its map on.</summary>
    public int ChapterPageCount { get; init; }

    /// <summary>The Day Group, for <see cref="LayoutCommandKind.Day"/>.</summary>
    public DateOnly? Date { get; init; }

    /// <summary>Anything the engine flagged during the dry run, worst first.</summary>
    public IReadOnlyList<LayoutDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>True when running this would actually change something.</summary>
    public bool HasWork => Kind == LayoutCommandKind.InsertUnplaced
        ? GeneratedPageCount > 0
        : AffectedPages.Count > 0;

    /// <summary>True when pages are being protected — the trigger for the include-pinned checkbox.</summary>
    public bool HasProtectedPages => PinnedPages.Count > 0 || DetachedPages.Count > 0;

    /// <summary>The dialog's first sentence: exactly which pages change.</summary>
    public string Headline => Kind switch
    {
        LayoutCommandKind.InsertUnplaced when GeneratedPageCount == 0 =>
            "There is nothing in the Unplaced bin to insert.",
        LayoutCommandKind.InsertUnplaced =>
            $"{Plural(GeneratedPageCount, "new page")} will be inserted " +
            $"({InsertPositions()}) for {Plural(UnplacedPhotoCount, "unplaced photo")}.",
        _ when AffectedPages.Count == 0 =>
            "Nothing here can be re-laid out — every page in scope is protected.",
        LayoutCommandKind.Day =>
            $"{PageList(AffectedPages)} for {DayLabel()} will be re-laid out.",
        _ => $"{PageList(AffectedPages)} will be re-laid out.",
    };

    /// <summary>The dialog's second sentence, naming the pages that survive; empty when none do.</summary>
    public string ProtectedNote
    {
        get
        {
            if (Kind == LayoutCommandKind.InsertUnplaced)
            {
                return AffectedPages.Count == 0 && GeneratedPageCount > 0
                    ? "Existing pages are not modified."
                    : string.Empty;
            }

            var parts = new List<string>(2);
            if (PinnedPages.Count > 0)
            {
                parts.Add($"Pinned pages ({PageRanges.Format(PinnedPages)})");
            }

            if (DetachedPages.Count > 0)
            {
                parts.Add($"detached pages ({PageRanges.Format(DetachedPages)})");
            }

            return parts.Count == 0
                ? string.Empty
                : string.Join(" and ", parts) + " will not change.";
        }
    }

    /// <summary>The confirm button's label, which names the size of what is about to happen.</summary>
    public string ConfirmLabel => Kind == LayoutCommandKind.InsertUnplaced
        ? $"Insert {Plural(GeneratedPageCount, "page")}"
        : $"Re-lay out {Plural(AffectedPages.Count, "page")}";

    /// <summary>The undo entry's description.</summary>
    public string UndoDescription => Kind switch
    {
        LayoutCommandKind.Day => $"Re-layout {DayLabel()}",
        LayoutCommandKind.InsertUnplaced => "Insert pages for unplaced photos",
        _ => "Auto-layout rest of chapter",
    };

    private string DayLabel() =>
        Date?.ToString("dddd, d MMMM", CultureInfo.CurrentCulture) ?? "this day";

    private string InsertPositions()
    {
        if (InsertedAfterPages.Count == 0)
        {
            return "at the end of the chapter";
        }

        var after = InsertedAfterPages.Where(p => p > 0).ToList();
        var atStart = InsertedAfterPages.Any(p => p <= 0);

        if (after.Count == 0)
        {
            return "before the first page";
        }

        var text = $"after {PageList(after).ToLowerInvariant()}";
        return atStart ? "before the first page and " + text : text;
    }

    private static string PageList(IReadOnlyList<int> pages) =>
        pages.Count == 1 ? $"Page {pages[0]}" : $"Pages {PageRanges.Format(pages)}";

    private static string Plural(int count, string noun) =>
        $"{count} {noun}{(count == 1 ? string.Empty : "s")}";
}

/// <summary>Formats a set of page numbers the way the warning dialog reads them: <c>7, 9–12, 15</c>.</summary>
public static class PageRanges
{
    /// <summary>Collapses consecutive runs into en-dashed ranges.</summary>
    public static string Format(IEnumerable<int> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var sorted = pages.Distinct().OrderBy(p => p).ToList();
        if (sorted.Count == 0)
        {
            return "none";
        }

        var parts = new List<string>();
        var start = sorted[0];
        var previous = start;

        foreach (var page in sorted.Skip(1))
        {
            if (page == previous + 1)
            {
                previous = page;
                continue;
            }

            parts.Add(Range(start, previous));
            start = page;
            previous = page;
        }

        parts.Add(Range(start, previous));
        return string.Join(", ", parts);

        static string Range(int from, int to) => from == to
            ? from.ToString(CultureInfo.CurrentCulture)
            : $"{from}–{to}";
    }
}

/// <summary>
/// Runs the layout engine for one chapter of the open project (doc 08 §10, R16).
/// <para>
/// It builds the <see cref="LayoutRequest"/> itself rather than going through
/// <see cref="ProjectSession.Layout"/> because the three editor commands need the three knobs that
/// helper does not expose: <see cref="LayoutRequest.DryRun"/> (the warning modal runs first and the
/// model must stay untouched until the user says yes), <see cref="LayoutRequest.IncludePinnedPages"/>
/// and <see cref="ChapterInput.UnplacedPhotoIds"/> (without which "insert pages for unplaced" has
/// nothing to insert). The Skia measurer is passed for the same reason
/// <see cref="ProjectSession"/> passes it: the headless default disagrees with real font metrics
/// and journal text ends up clipped.
/// </para>
/// </summary>
public sealed class ChapterLayoutRunner
{
    private readonly ProjectSession _session;
    private readonly SkiaTextMeasurer _measurer = new();

    /// <summary>Creates a runner over the open session.</summary>
    public ChapterLayoutRunner(ProjectSession session) => _session = session;

    /// <summary>The chapter model for a month, or null when no project is open.</summary>
    public Chapter? ChapterFor(int month) => _session.Chapters.FirstOrDefault(c => c.Month == month);

    /// <summary>
    /// Photos of the chapter that no page uses — the Unplaced bin the engine's
    /// <see cref="LayoutScope.InsertUnplaced"/> scope consumes.
    /// </summary>
    public IReadOnlyList<Photo> UnplacedPhotos(int month)
    {
        var book = _session.Book;
        var chapter = ChapterFor(month);
        if (book is null || chapter is null)
        {
            return [];
        }

        var placed = chapter.Pages
            .SelectMany(p => p.Placements)
            .Select(p => p.PhotoId)
            .ToHashSet(StringComparer.Ordinal);

        return [.. _session.Catalog.InChapter(book.Year, month)
            .Where(p => !p.Excluded && !placed.Contains(p.Id))
            .OrderBy(p => p.TakenAt)
            .ThenBy(p => p.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The Day Group a page belongs to: the earliest date among its photos, falling back to its
    /// journal bindings for a text-only page. Null when the page carries neither.
    /// </summary>
    public DateOnly? DayOfPage(Page? page)
    {
        var book = _session.Book;
        if (page is null || book is null)
        {
            return null;
        }

        var dates = page.Placements
            .Select(p => _session.Catalog.Find(p.PhotoId))
            .Where(p => p is not null)
            .Select(p => p!.TakenOn)
            .ToList();

        if (dates.Count > 0)
        {
            return dates.Min();
        }

        var entryIds = page.JournalAssignments
            .SelectMany(a => a.EntryIds)
            .ToHashSet(StringComparer.Ordinal);

        var entryDates = _session.Journal.Entries
            .Where(e => entryIds.Contains(e.Id))
            .Select(e => e.EffectiveDate)
            .ToList();

        return entryDates.Count > 0 ? entryDates.Min() : null;
    }

    /// <summary>Runs the engine. Pure — with <paramref name="dryRun"/> the model is not touched at all.</summary>
    public LayoutResult Run(int month, LayoutScope scope, bool includePinnedPages, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var book = _session.Book ?? throw new InvalidOperationException("No project is open.");
        var chapter = ChapterFor(month) ?? throw new InvalidOperationException($"No chapter for month {month}.");

        var photos = _session.Catalog.InChapter(book.Year, month).Where(p => !p.Excluded).ToList();
        var unplaced = UnplacedPhotos(month).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

        return LayoutEngine.LayoutChapter(new LayoutRequest
        {
            Chapter = new ChapterInput
            {
                Year = book.Year,
                Month = month,
                Title = chapter.Title,
                Photos = photos,
                JournalEntries = [.. _session.Journal.EntriesIn(book.Year, month)],
                ExistingPages = [.. chapter.Pages],
                UnplacedPhotoIds = unplaced,
                PageSize = book.PageSize,
            },
            Style = StyleResolver.Resolve(book, chapter, null),
            Seed = book.Seed,
            Scope = scope,
            DryRun = dryRun,
            IncludePinnedPages = includePinnedPages,
            TextMeasurer = _measurer,
        });
    }

    /// <summary>
    /// The cheap phases-1-to-3 pass behind the R16 warning modal: what <em>would</em> change, named
    /// page by page, with nothing written.
    /// </summary>
    public LayoutPlan DryRun(int month, LayoutCommandKind kind, LayoutScope scope, bool includePinnedPages, DateOnly? date = null)
    {
        var result = Run(month, scope, includePinnedPages, dryRun: true);
        var chapter = ChapterFor(month);

        var pinned = new List<int>();
        var detached = new List<int>();
        foreach (var number in result.PinnedPagesKept)
        {
            var page = chapter is not null && number >= 1 && number <= chapter.Pages.Count
                ? chapter.Pages[number - 1]
                : null;

            if (page?.IsDetached == true)
            {
                detached.Add(number);
            }
            else
            {
                pinned.Add(number);
            }
        }

        return new LayoutPlan
        {
            Kind = kind,
            Scope = scope,
            Month = month,
            IncludePinnedPages = includePinnedPages,
            AffectedPages = [.. result.AffectedPages],
            PinnedPages = pinned,
            DetachedPages = detached,
            InsertedAfterPages = [.. result.InsertedAfterPages],
            GeneratedPageCount = result.GeneratedPageCount,
            UnplacedPhotoCount = UnplacedPhotos(month).Count,
            ChapterPageCount = chapter?.Pages.Count ?? 0,
            Date = date,
            Diagnostics = [.. result.Diagnostics.OrderByDescending(d => d.Severity)],
        };
    }

    /// <summary>
    /// Applies a real run to the chapter and records it as a single undo entry (doc 09 §4). Returns
    /// false when the run produced no pages at all, which would otherwise blank the chapter.
    /// </summary>
    /// <param name="month">The chapter month.</param>
    /// <param name="result">A non-dry-run result for that chapter.</param>
    /// <param name="undo">The stack to record on.</param>
    /// <param name="description">The Edit-menu text.</param>
    /// <param name="afterEach">Invoked after apply, undo and redo so the shell can rebuild its lists.</param>
    public bool Apply(int month, LayoutResult result, UndoStack undo, string description, Action? afterEach = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(undo);

        var chapter = ChapterFor(month);
        if (chapter is null || result.IsDryRun || result.Pages.Count == 0)
        {
            return false;
        }

        var before = ChapterPagesSnapshot.Capture(chapter);
        chapter.Pages = [.. result.Pages];
        var after = ChapterPagesSnapshot.Capture(chapter);

        afterEach?.Invoke();

        // Push, not Execute: the pages are already in place, and re-running Do() here would only
        // restore what is already true.
        undo.Push(new ChapterPagesCommand(description, chapter, before, after, afterEach));
        _session.MarkDirty();
        return true;
    }
}
