using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoBook.App.Services;
using PhotoBook.Core.Model;
using PhotoBook.Rendering;

namespace PhotoBook.App.ViewModels.Journal;

/// <summary>One page that carries a day's journal text, and the slots it carries it in.</summary>
public sealed class JournalPageRefViewModel
{
    /// <param name="month">The chapter the page belongs to.</param>
    /// <param name="pageId">The page's stable id, for navigation.</param>
    /// <param name="pageNumber">The book-wide 1-based page number.</param>
    /// <param name="slotIds">The journal slots on that page holding the text.</param>
    public JournalPageRefViewModel(int month, string pageId, int pageNumber, IReadOnlyList<string> slotIds)
    {
        ArgumentNullException.ThrowIfNull(slotIds);

        Month = month;
        PageId = pageId;
        PageNumber = pageNumber;
        SlotIds = slotIds;
    }

    /// <summary>The chapter the page belongs to.</summary>
    public int Month { get; }

    /// <summary>The page's stable id.</summary>
    public string PageId { get; }

    /// <summary>The book-wide page number, the same one export and preflight use.</summary>
    public int PageNumber { get; }

    /// <summary>The journal slots the text flows through on this page.</summary>
    public IReadOnlyList<string> SlotIds { get; }

    /// <summary>The chip label, e.g. "p. 14".</summary>
    public string Label => $"p. {PageNumber.ToString(CultureInfo.CurrentCulture)}";

    /// <summary>The tooltip: which slots, and that clicking goes there.</summary>
    public string Detail => SlotIds.Count == 1
        ? $"Journal slot {SlotIds[0]} — click to open this page"
        : $"Journal slots {string.Join(", ", SlotIds)} — click to open this page";
}

/// <summary>
/// One day of the month in the day map: its journal text, its photographs, and the pages that
/// actually carry the text.
/// </summary>
public sealed class JournalDayViewModel
{
    /// <param name="date">The calendar day.</param>
    /// <param name="entries">The day's journal entries, as report rows.</param>
    /// <param name="photoCount">How many non-excluded photos the day holds.</param>
    /// <param name="pages">The pages carrying this day's text.</param>
    public JournalDayViewModel(
        DateOnly date,
        IReadOnlyList<JournalEntryRowViewModel> entries,
        int photoCount,
        IReadOnlyList<JournalPageRefViewModel> pages)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(pages);

        Date = date;
        Entries = entries;
        PhotoCount = photoCount;
        Pages = pages;
    }

    /// <summary>The calendar day.</summary>
    public DateOnly Date { get; }

    /// <summary>The day number, big and on the left.</summary>
    public string DayLabel => Date.Day.ToString(CultureInfo.CurrentCulture);

    /// <summary>The weekday, under the number.</summary>
    public string WeekdayLabel =>
        CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames[(int)Date.DayOfWeek];

    /// <summary>The day's entries — normally one, since doc 11 merges same-day text.</summary>
    public IReadOnlyList<JournalEntryRowViewModel> Entries { get; }

    /// <summary>How many photographs the day holds.</summary>
    public int PhotoCount { get; }

    /// <summary>The pages carrying this day's text, in book order.</summary>
    public IReadOnlyList<JournalPageRefViewModel> Pages { get; }

    /// <summary>True when the day has journal text at all.</summary>
    public bool HasText => Entries.Count > 0;

    /// <summary>True when the day has photographs.</summary>
    public bool HasPhotos => PhotoCount > 0;

    /// <summary>True when the day's text is on at least one page.</summary>
    public bool IsPlaced => Pages.Count > 0;

    /// <summary>
    /// True when the day has text that no page carries — the case worth seeing. It happens when the
    /// month has not been laid out since the text arrived, or when a template with no journal slot was
    /// chosen for the page the day landed on.
    /// </summary>
    public bool IsHomeless => HasText && Pages.Count == 0;

    /// <summary>The one-line preview of the day's text.</summary>
    public string Preview => Entries.Count == 0 ? string.Empty : Entries[0].Preview;

    /// <summary>"412 characters · 6 photos" — the shape of the day in one line.</summary>
    public string SizeLabel
    {
        get
        {
            var parts = new List<string>(2);
            var characters = Entries.Sum(e => e.Entry.CharacterCount);
            if (characters > 0)
            {
                parts.Add($"{characters.ToString("N0", CultureInfo.CurrentCulture)} characters");
            }

            if (PhotoCount > 0)
            {
                parts.Add($"{PhotoCount.ToString(CultureInfo.CurrentCulture)} " +
                          $"photo{(PhotoCount == 1 ? string.Empty : "s")}");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>What happened to this day's text, in words.</summary>
    public string StatusLabel => (HasText, IsPlaced, HasPhotos) switch
    {
        (true, true, _) => "In the book",
        (true, false, _) => "Not on any page yet",
        (false, _, true) => "Photos, no journal text",
        _ => "Nothing on this day",
    };
}

/// <summary>
/// The month's journal, day by day, tied to the pages that carry it.
///
/// <para>
/// The Import Report answers "did my text get a date"; this answers the question after it — "did my
/// text get into the book". A day whose entry is dated but sits on no page looks identical to a day
/// with no entry at all once the book is printed, so those days are called out rather than left to be
/// discovered on the PDF.
/// </para>
///
/// <para>
/// Page numbers come from <see cref="BookPagination"/>, the same numbering export and preflight use,
/// so a page reference here means the same page a preflight finding does.
/// </para>
/// </summary>
public sealed partial class JournalDayMapViewModel : ObservableObject
{
    private readonly ProjectSession _session;

    /// <param name="session">The open project.</param>
    public JournalDayMapViewModel(ProjectSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _month = 1;
    }

    /// <summary>Raised when a page chip is clicked: the chapter month and the page id.</summary>
    public event Action<int, string>? PageActivated;

    /// <summary>The days on screen, ascending.</summary>
    public ObservableCollection<JournalDayViewModel> Days { get; } = [];

    /// <summary>The month being shown, 1..12.</summary>
    [ObservableProperty]
    private int _month;

    /// <summary>True to list days that have photographs but no journal text.</summary>
    [ObservableProperty]
    private bool _showDaysWithoutText = true;

    /// <summary>The month's name and the book's year.</summary>
    public string MonthLabel => _session.Book is null
        ? string.Empty
        : $"{CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(Month)} " +
          _session.Book.Year.ToString(CultureInfo.CurrentCulture);

    /// <summary>Days of the month whose journal text made it onto a page.</summary>
    public int PlacedDayCount => Days.Count(d => d.HasText && d.IsPlaced);

    /// <summary>Days whose text exists but sits on no page — the ones to fix.</summary>
    public int HomelessDayCount => Days.Count(d => d.IsHomeless);

    /// <summary>Days with photographs and no journal text — deliberate negative space, not an error.</summary>
    public int TextlessDayCount => Days.Count(d => !d.HasText && d.HasPhotos);

    /// <summary>The amber header badge, e.g. "3 days on no page".</summary>
    public string HomelessBadgeLabel =>
        $"{HomelessDayCount.ToString(CultureInfo.CurrentCulture)} " +
        $"day{(HomelessDayCount == 1 ? string.Empty : "s")} on no page";

    /// <summary>Entries anywhere in the book that still have no date, so they belong to no day at all.</summary>
    public int UndatedEntryCount =>
        _session.Journal.Entries.Count(e => !e.Excluded && e.Status == JournalEntryStatus.Unmatched);

    /// <summary>True when the chapter has pages at all; without them nothing can carry any text.</summary>
    public bool HasPages => Chapter?.Pages.Count > 0;

    /// <summary>The header sentence.</summary>
    public string SummarySentence
    {
        get
        {
            if (!_session.IsOpen)
            {
                return string.Empty;
            }

            if (!HasPages)
            {
                return "This month has no pages yet — lay it out and the dated text will land on them.";
            }

            var parts = new List<string>(3)
            {
                $"{PlacedDayCount.ToString(CultureInfo.CurrentCulture)} " +
                $"day{(PlacedDayCount == 1 ? string.Empty : "s")} of text in the book",
            };

            if (HomelessDayCount > 0)
            {
                parts.Add($"{HomelessDayCount.ToString(CultureInfo.CurrentCulture)} on no page");
            }

            if (UndatedEntryCount > 0)
            {
                parts.Add($"{UndatedEntryCount.ToString(CultureInfo.CurrentCulture)} " +
                          $"entr{(UndatedEntryCount == 1 ? "y" : "ies")} with no date at all");
            }

            return string.Join(" · ", parts) + ".";
        }
    }

    /// <summary>The chapter being shown.</summary>
    private Chapter? Chapter => _session.Chapters.FirstOrDefault(c => c.Month == Month);

    /// <summary>Shows a month.</summary>
    /// <param name="month">The month to show, 1..12.</param>
    public void Load(int month)
    {
        Month = Math.Clamp(month, 1, 12);
        Refresh();
    }

    /// <summary>Re-reads the month after an assignment, a layout run or an import.</summary>
    public void Refresh()
    {
        Days.Clear();

        if (_session.Book is not { } book)
        {
            RaiseCounts();
            return;
        }

        var year = book.Year;
        var carriers = BuildCarrierIndex();
        var daysInMonth = DateTime.DaysInMonth(year, Month);

        for (var day = 1; day <= daysInMonth; day++)
        {
            var date = new DateOnly(year, Month, day);
            var entries = _session.Journal.EntriesOn(date)
                .Select(e => new JournalEntryRowViewModel(e, year))
                .ToList();
            var photos = _session.Catalog.Photos.Count(p => !p.Excluded && p.TakenOn == date);

            if (entries.Count == 0 && (photos == 0 || !ShowDaysWithoutText))
            {
                continue;
            }

            var pages = entries
                .SelectMany(e => Carriers(carriers, e.Id))
                .DistinctBy(p => p.PageId, StringComparer.Ordinal)
                .OrderBy(p => p.PageNumber)
                .ToList();

            Days.Add(new JournalDayViewModel(date, entries, photos, pages));
        }

        RaiseCounts();
    }

    /// <summary>Shows the previous month.</summary>
    [RelayCommand]
    private void PreviousMonth() => Load(Month == 1 ? 12 : Month - 1);

    /// <summary>Shows the next month.</summary>
    [RelayCommand]
    private void NextMonth() => Load(Month == 12 ? 1 : Month + 1);

    /// <summary>Opens the page a chip points at.</summary>
    /// <param name="page">The chip that was clicked.</param>
    [RelayCommand]
    private void OpenPage(JournalPageRefViewModel? page)
    {
        if (page is not null)
        {
            PageActivated?.Invoke(page.Month, page.PageId);
        }
    }

    partial void OnMonthChanged(int value)
    {
        OnPropertyChanged(nameof(MonthLabel));
        OnPropertyChanged(nameof(HasPages));
    }

    partial void OnShowDaysWithoutTextChanged(bool value) => Refresh();

    /// <summary>The pages carrying one entry, or none.</summary>
    private static IEnumerable<JournalPageRefViewModel> Carriers(
        Dictionary<string, List<JournalPageRefViewModel>> index, string entryId) =>
        index.TryGetValue(entryId, out var found) ? found : [];

    /// <summary>
    /// Entry id → the pages carrying it, scanned across the whole book rather than this chapter alone:
    /// re-dating an entry after a layout run can leave its text on a page of the month it used to
    /// belong to, and a map that only looked at this chapter would report that as "on no page".
    /// </summary>
    private Dictionary<string, List<JournalPageRefViewModel>> BuildCarrierIndex()
    {
        var index = new Dictionary<string, List<JournalPageRefViewModel>>(StringComparer.Ordinal);

        foreach (var bookPage in BookPagination.Paginate(_session.Chapters))
        {
            var slotsByEntry = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var assignment in bookPage.Page.JournalAssignments)
            {
                foreach (var entryId in assignment.EntryIds)
                {
                    if (!slotsByEntry.TryGetValue(entryId, out var slots))
                    {
                        slots = [];
                        slotsByEntry[entryId] = slots;
                    }

                    if (!slots.Contains(assignment.TextSlotId, StringComparer.Ordinal))
                    {
                        slots.Add(assignment.TextSlotId);
                    }
                }
            }

            foreach (var (entryId, slots) in slotsByEntry)
            {
                if (!index.TryGetValue(entryId, out var pages))
                {
                    pages = [];
                    index[entryId] = pages;
                }

                pages.Add(new JournalPageRefViewModel(
                    bookPage.Chapter.Month, bookPage.Page.Id, bookPage.PageNumber, slots));
            }
        }

        return index;
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(PlacedDayCount));
        OnPropertyChanged(nameof(HomelessDayCount));
        OnPropertyChanged(nameof(HomelessBadgeLabel));
        OnPropertyChanged(nameof(TextlessDayCount));
        OnPropertyChanged(nameof(UndatedEntryCount));
        OnPropertyChanged(nameof(HasPages));
        OnPropertyChanged(nameof(SummarySentence));
        OnPropertyChanged(nameof(MonthLabel));
    }
}
