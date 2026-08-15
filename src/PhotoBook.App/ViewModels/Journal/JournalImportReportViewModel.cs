using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoBook.App.Services;
using PhotoBook.Core.Model;

namespace PhotoBook.App.ViewModels.Journal;

/// <summary>One cell of the assign-a-date calendar: a day, and what the book already has on it.</summary>
public sealed partial class JournalPickerDayViewModel : ObservableObject
{
    /// <param name="date">The day this cell stands for.</param>
    /// <param name="inMonth">False for the padding days of the neighbouring months.</param>
    /// <param name="photoCount">How many photos that day holds — a day with photos is a likely home.</param>
    /// <param name="hasEntry">True when another entry already carries this day's text.</param>
    public JournalPickerDayViewModel(DateOnly date, bool inMonth, int photoCount, bool hasEntry)
    {
        Date = date;
        IsCurrentMonth = inMonth;
        PhotoCount = photoCount;
        HasEntry = hasEntry;
    }

    /// <summary>The day this cell stands for.</summary>
    public DateOnly Date { get; }

    /// <summary>The number drawn in the cell.</summary>
    public string Label => Date.Day.ToString(CultureInfo.CurrentCulture);

    /// <summary>False for the padding days either side of the month, which are dimmed.</summary>
    public bool IsCurrentMonth { get; }

    /// <summary>How many photographs the book holds for this day.</summary>
    public int PhotoCount { get; }

    /// <summary>True when the day has photographs — the dot under the number.</summary>
    public bool HasPhotos => PhotoCount > 0;

    /// <summary>True when a journal entry already covers this day, so a second one would merge into it.</summary>
    public bool HasEntry { get; }

    /// <summary>True for the day the picker is set to.</summary>
    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// The calendar behind <em>Assign date</em>. It is not a stock date picker on purpose: the question
/// the user is answering is "which day of the book does this text belong to", so every cell shows
/// whether that day has photographs and whether it already has journal text.
/// </summary>
public sealed partial class JournalDatePickerViewModel : ObservableObject
{
    private readonly Func<DateOnly, int> _photosOn;
    private readonly Func<DateOnly, bool> _entryOn;

    /// <param name="photosOn">How many photos the book holds for a day.</param>
    /// <param name="entryOn">Whether a journal entry already covers a day.</param>
    public JournalDatePickerViewModel(Func<DateOnly, int> photosOn, Func<DateOnly, bool> entryOn)
    {
        ArgumentNullException.ThrowIfNull(photosOn);
        ArgumentNullException.ThrowIfNull(entryOn);

        _photosOn = photosOn;
        _entryOn = entryOn;

        WeekdayNames =
        [
            .. Enumerable.Range(0, 7).Select(i =>
                CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames[
                    ((int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek + i) % 7])
        ];

        _selectedDate = DateOnly.FromDateTime(DateTime.Today);
        DisplayMonth = new DateOnly(_selectedDate.Year, _selectedDate.Month, 1);
        Rebuild();
    }

    /// <summary>The weekday headers, in the culture's own week order.</summary>
    public IReadOnlyList<string> WeekdayNames { get; }

    /// <summary>The 42 cells of the visible month.</summary>
    public ObservableCollection<JournalPickerDayViewModel> Days { get; } = [];

    /// <summary>The month on screen.</summary>
    public DateOnly DisplayMonth { get; private set; }

    /// <summary>That month's name, for the calendar header.</summary>
    public string DisplayMonthLabel => DisplayMonth.ToString("MMMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>The day the picker is set to.</summary>
    [ObservableProperty]
    private DateOnly _selectedDate;

    /// <summary>The chosen day, spelled out for the confirm button.</summary>
    public string SelectedDateLabel => SelectedDate.ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>Shows the previous month.</summary>
    [RelayCommand]
    private void PreviousMonth() => ShowMonth(DisplayMonth.AddMonths(-1));

    /// <summary>Shows the next month.</summary>
    [RelayCommand]
    private void NextMonth() => ShowMonth(DisplayMonth.AddMonths(1));

    /// <summary>Picks a day.</summary>
    /// <param name="day">The cell that was clicked.</param>
    [RelayCommand]
    private void SelectDay(JournalPickerDayViewModel? day)
    {
        if (day is null)
        {
            return;
        }

        SelectedDate = day.Date;
        if (day.Date.Month != DisplayMonth.Month || day.Date.Year != DisplayMonth.Year)
        {
            ShowMonth(day.Date);
            return;
        }

        SyncSelection();
    }

    /// <summary>Points the picker at a day — the seed when a row is selected.</summary>
    /// <param name="date">The day to show and select.</param>
    public void ShowDate(DateOnly date)
    {
        SelectedDate = date;
        ShowMonth(date);
    }

    /// <summary>Re-reads the photo and entry markers, after an import or an assignment.</summary>
    public void Refresh() => Rebuild();

    partial void OnSelectedDateChanged(DateOnly value)
    {
        OnPropertyChanged(nameof(SelectedDateLabel));
        SyncSelection();
    }

    private void ShowMonth(DateOnly month)
    {
        DisplayMonth = new DateOnly(month.Year, month.Month, 1);
        OnPropertyChanged(nameof(DisplayMonth));
        OnPropertyChanged(nameof(DisplayMonthLabel));
        Rebuild();
    }

    private void Rebuild()
    {
        Days.Clear();

        var firstDayOfWeek = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var offset = (((int)DisplayMonth.DayOfWeek - firstDayOfWeek) % 7 + 7) % 7;
        var start = DisplayMonth.AddDays(-offset);

        for (var i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);
            Days.Add(new JournalPickerDayViewModel(
                date,
                date.Month == DisplayMonth.Month && date.Year == DisplayMonth.Year,
                _photosOn(date),
                _entryOn(date)));
        }

        SyncSelection();
    }

    private void SyncSelection()
    {
        foreach (var day in Days)
        {
            day.IsSelected = day.Date == SelectedDate;
        }
    }
}

/// <summary>One of the Import Report's groups: the rows in it, and why they are grouped together.</summary>
public sealed partial class JournalReviewGroupViewModel : ObservableObject
{
    /// <param name="bucket">The group this is.</param>
    /// <param name="title">The heading text.</param>
    /// <param name="hint">The sentence under the heading, saying what happens if it is ignored.</param>
    public JournalReviewGroupViewModel(JournalReviewBucket bucket, string title, string hint)
    {
        Bucket = bucket;
        Title = title;
        Hint = hint;
        _isExpanded = bucket != JournalReviewBucket.Dated;
    }

    /// <summary>Which group this is.</summary>
    public JournalReviewBucket Bucket { get; }

    /// <summary>The heading text.</summary>
    public string Title { get; }

    /// <summary>What happens if the group is left alone.</summary>
    public string Hint { get; }

    /// <summary>The rows in the group.</summary>
    public ObservableCollection<JournalEntryRowViewModel> Rows { get; } = [];

    /// <summary>How many rows are in it.</summary>
    public int Count => Rows.Count;

    /// <summary>True for the three groups that want the user's attention.</summary>
    public bool IsAttention => Bucket is JournalReviewBucket.Undated
        or JournalReviewBucket.Ambiguous or JournalReviewBucket.OutOfYear;

    /// <summary>True when the group's rows are showing. The settled groups start closed.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Opens or closes the group.</summary>
    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    /// <summary>Raises <see cref="Count"/> after the rows have been rebuilt.</summary>
    public void RowsChanged() => OnPropertyChanged(nameof(Count));
}

/// <summary>
/// A user assignment that a re-import could not place, with the two ways out doc 11 offers:
/// re-attach it to an entry of the new document, or let it go.
/// </summary>
public sealed class JournalOrphanViewModel
{
    /// <param name="orphan">The orphaned assignment from the import report.</param>
    public JournalOrphanViewModel(OrphanedUserAssignment orphan)
    {
        ArgumentNullException.ThrowIfNull(orphan);
        Orphan = orphan;
    }

    /// <summary>The underlying record.</summary>
    public OrphanedUserAssignment Orphan { get; }

    /// <summary>A line of the old entry's text, so the user can recognize what was lost.</summary>
    public string TextPreview => Orphan.TextPreview.Length > 0 ? Orphan.TextPreview : "(no text)";

    /// <summary>What the assignment carried.</summary>
    public string DetailLabel => Orphan switch
    {
        { UserDate: { } date, Excluded: true } =>
            $"You dated this {date.ToString("d MMM yyyy", CultureInfo.CurrentCulture)} and discarded it.",
        { UserDate: { } date } => $"You dated this {date.ToString("d MMM yyyy", CultureInfo.CurrentCulture)}.",
        { Excluded: true } => "You discarded this entry.",
        _ => "The entry carried your changes.",
    };
}

/// <summary>
/// The Import Report of doc 11 — the one place date problems get resolved, and the answer to the
/// question the app could not previously answer at all: <em>what happened to my journal?</em>
///
/// <para>
/// Before this existed, an entry whose date the matcher could not read was parsed, stored, and then
/// never shown anywhere: it appeared in no month, on no page, and in no message. For a real Word
/// journal with mixed date formats that is the common case, so text the user wrote simply went
/// missing. The report lists those entries first, with their full text, and gives them a date.
/// </para>
///
/// <para>
/// The four counts are recomputed from the entries rather than read from the stored report, so they
/// stay true as the user works — a stored count would freeze at whatever the import said and start
/// lying with the first assignment. Every action is one undoable edit
/// (<see cref="UndoStack"/>) against the session's journal, which is the single writer of the model.
/// </para>
/// </summary>
public sealed partial class JournalImportReportViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly UndoStack _undo;

    private string? _selectedId;
    private bool _loading;

    /// <param name="session">The open project — the owner of the journal.</param>
    /// <param name="undo">The book's undo stack.</param>
    public JournalImportReportViewModel(ProjectSession session, UndoStack undo)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(undo);

        _session = session;
        _undo = undo;

        Picker = new JournalDatePickerViewModel(PhotosOn, EntryOn);

        Groups.Add(new JournalReviewGroupViewModel(
            JournalReviewBucket.Undated,
            "Needs a date",
            "These are in the book nowhere until you date them — the text is kept, but nothing renders it."));
        Groups.Add(new JournalReviewGroupViewModel(
            JournalReviewBucket.Ambiguous,
            "The date is a guess",
            "The matcher found a date it was not sure of. Confirm it or pick another reading."));
        Groups.Add(new JournalReviewGroupViewModel(
            JournalReviewBucket.OutOfYear,
            "Outside this book's year",
            "A book covers one year, so these fall in no chapter. Re-date them or leave them for that year's book."));
        Groups.Add(new JournalReviewGroupViewModel(
            JournalReviewBucket.Discarded,
            "Discarded",
            "Removed from the book by you. The text is still here and can be brought back."));
        Groups.Add(new JournalReviewGroupViewModel(
            JournalReviewBucket.Dated,
            "Dated",
            "Nothing to do — these landed on a day of the book."));
    }

    /// <summary>Raised after any edit, so the day map and the shell re-read the journal.</summary>
    public event Action? Changed;

    /// <summary>The report's groups, worst first.</summary>
    public ObservableCollection<JournalReviewGroupViewModel> Groups { get; } = [];

    /// <summary>User assignments a re-import could not place (doc 11 §"Re-import semantics").</summary>
    public ObservableCollection<JournalOrphanViewModel> Orphans { get; } = [];

    /// <summary>The assign-a-date calendar.</summary>
    public JournalDatePickerViewModel Picker { get; }

    /// <summary>The row the detail pane is showing.</summary>
    [ObservableProperty]
    private JournalEntryRowViewModel? _selectedRow;

    /// <summary>The book's year — what "outside the year" is measured against (R3).</summary>
    public int BookYear => _session.Book?.Year ?? DateTime.Now.Year;

    /// <summary>True once a journal has been imported; otherwise the panel shows its empty state.</summary>
    public bool HasJournal => _session.Journal.Source is not null || _session.Journal.Entries.Count > 0;

    /// <summary>The document the entries came from, and when it was read.</summary>
    public string SourceLabel
    {
        get
        {
            if (_session.Journal.Source is not { } source)
            {
                return "No journal has been imported yet.";
            }

            var imported = source.ImportedAtUtc.ToLocalTime()
                .ToString("d MMM yyyy 'at' h:mm tt", CultureInfo.CurrentCulture);
            return $"{source.FileName} · read {imported}";
        }
    }

    /// <summary>Entries whose date is settled — matched by the parser or assigned by the user.</summary>
    public int DatedCount => CountIn(JournalReviewBucket.Dated);

    /// <summary>Entries the matcher dated but was not sure of.</summary>
    public int AmbiguousCount => CountIn(JournalReviewBucket.Ambiguous);

    /// <summary>Entries with no date at all — the ones that would otherwise vanish silently.</summary>
    public int UndatedCount => CountIn(JournalReviewBucket.Undated);

    /// <summary>Entries dated outside the book's year (R3).</summary>
    public int OutOfYearCount => CountIn(JournalReviewBucket.OutOfYear);

    /// <summary>Entries the user removed from the book.</summary>
    public int DiscardedCount => CountIn(JournalReviewBucket.Discarded);

    /// <summary>How many orphaned assignments are waiting to be re-attached or discarded.</summary>
    public int OrphanCount => Orphans.Count;

    /// <summary>Every entry parsed from the document.</summary>
    public int EntryCount => _session.Journal.Entries.Count;

    /// <summary>True when anything in the report wants the user — what decides whether it opens itself.</summary>
    public bool HasFindings => UndatedCount > 0 || AmbiguousCount > 0 || OutOfYearCount > 0 || OrphanCount > 0;

    /// <summary>The header sentence: what the import did, in one line.</summary>
    public string SummarySentence
    {
        get
        {
            if (!HasJournal)
            {
                return "Import a Word journal and its dated entries will interleave with the photos.";
            }

            var parts = new List<string>(4)
            {
                $"{Count(DatedCount, "entry", "entries")} dated",
            };

            if (AmbiguousCount > 0) parts.Add($"{AmbiguousCount.ToString(CultureInfo.CurrentCulture)} unsure");
            if (UndatedCount > 0) parts.Add($"{UndatedCount.ToString(CultureInfo.CurrentCulture)} with no date");
            if (OutOfYearCount > 0)
            {
                parts.Add($"{OutOfYearCount.ToString(CultureInfo.CurrentCulture)} outside " +
                          BookYear.ToString(CultureInfo.CurrentCulture));
            }

            if (DiscardedCount > 0) parts.Add($"{DiscardedCount.ToString(CultureInfo.CurrentCulture)} discarded");
            return string.Join(" · ", parts) + ".";
        }
    }

    /// <summary>True when a row is selected and can take a date.</summary>
    public bool CanAssign => SelectedRow is not null;

    /// <summary>Rebuilds every group and count from the session's journal.</summary>
    public void Load()
    {
        _loading = true;
        try
        {
            var year = BookYear;
            var rows = _session.Journal.Entries
                .Select(e => new JournalEntryRowViewModel(e, year))
                .ToList();

            foreach (var group in Groups)
            {
                group.Rows.Clear();
            }

            foreach (var row in rows
                .OrderBy(r => r.Entry.EffectiveDate)
                .ThenBy(r => r.Id, StringComparer.Ordinal))
            {
                Groups.First(g => g.Bucket == row.Bucket).Rows.Add(row);
            }

            foreach (var group in Groups)
            {
                group.RowsChanged();
            }

            Orphans.Clear();
            foreach (var orphan in _session.Journal.ImportReport.OrphanedUserAssignments)
            {
                Orphans.Add(new JournalOrphanViewModel(orphan));
            }

            // Selection is by entry id, not by object: the rows were just rebuilt.
            SelectedRow = rows.FirstOrDefault(r => string.Equals(r.Id, _selectedId, StringComparison.Ordinal))
                          ?? Groups.Where(g => g.IsAttention).SelectMany(g => g.Rows).FirstOrDefault();
        }
        finally
        {
            _loading = false;
        }

        Picker.Refresh();

        // Seeding happens here rather than in the selection handler alone: Load picks the first row
        // that needs attention, and a picker left on today's date would offer to file a 2024 journal
        // entry under the year the app happens to be running in.
        SeedPicker(SelectedRow);
        RaiseCounts();
    }

    /// <summary>
    /// Points the calendar somewhere useful for a row: its own date when it has one, otherwise the
    /// first day of the book that has photographs and nothing written about it — which is where an
    /// undated entry most often belongs, and is at worst in the right year.
    /// </summary>
    private void SeedPicker(JournalEntryRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var entry = row.Entry;
        if (entry.Status != JournalEntryStatus.Unmatched || entry.UserDate is not null)
        {
            Picker.ShowDate(entry.EffectiveDate);
            return;
        }

        var year = BookYear;
        var firstBlankDay = _session.IsOpen
            ? _session.Catalog.Photos
                .Where(p => !p.Excluded && p.TakenOn.Year == year)
                .Select(p => p.TakenOn)
                .Distinct()
                .Where(d => !_session.Journal.EntriesOn(d).Any())
                .OrderBy(d => d)
                .Cast<DateOnly?>()
                .FirstOrDefault()
            : null;

        Picker.ShowDate(firstBlankDay ?? new DateOnly(year, 1, 1));
    }

    partial void OnSelectedRowChanged(JournalEntryRowViewModel? value)
    {
        _selectedId = value?.Id;

        foreach (var group in Groups)
        {
            foreach (var row in group.Rows)
            {
                row.IsSelected = ReferenceEquals(row, value);
            }
        }

        if (!_loading)
        {
            SeedPicker(value);
        }

        OnPropertyChanged(nameof(CanAssign));
        AssignPickedDateCommand.NotifyCanExecuteChanged();
        AssignCandidateCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
        ReattachOrphanCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Selects a row from the list.</summary>
    /// <param name="row">The row that was clicked.</param>
    [RelayCommand]
    private void SelectRow(JournalEntryRowViewModel? row)
    {
        if (row is not null)
        {
            SelectedRow = row;
        }
    }

    /// <summary>Takes one of the matcher's own readings — the one-click path for an unsure entry.</summary>
    /// <param name="candidate">The chip that was clicked.</param>
    [RelayCommand(CanExecute = nameof(CanAssign))]
    private void AssignCandidate(JournalDateCandidate? candidate)
    {
        if (candidate is not null && SelectedRow is { } row)
        {
            Assign(row, candidate.Date);
        }
    }

    /// <summary>Takes the date the calendar is showing.</summary>
    [RelayCommand(CanExecute = nameof(CanAssign))]
    private void AssignPickedDate()
    {
        if (SelectedRow is { } row)
        {
            Assign(row, Picker.SelectedDate);
        }
    }

    /// <summary>Removes the selected entry from the book (doc 11 <em>Exclude</em>).</summary>
    [RelayCommand(CanExecute = nameof(CanAssign))]
    private void Discard()
    {
        if (SelectedRow is { } row)
        {
            SetExcluded(row, true);
        }
    }

    /// <summary>Puts a discarded entry back.</summary>
    [RelayCommand(CanExecute = nameof(CanAssign))]
    private void Restore()
    {
        if (SelectedRow is { } row)
        {
            SetExcluded(row, false);
        }
    }

    /// <summary>Moves an orphaned assignment onto the selected entry.</summary>
    /// <param name="orphan">The orphan to re-attach.</param>
    [RelayCommand(CanExecute = nameof(CanAssign))]
    private void ReattachOrphan(JournalOrphanViewModel? orphan)
    {
        if (orphan is null || SelectedRow is not { } row)
        {
            return;
        }

        var journal = _session.Journal;
        var record = orphan.Orphan;
        var index = journal.ImportReport.OrphanedUserAssignments.IndexOf(record);
        var entry = row.Entry;
        var before = JournalEdits.Capture(entry);

        Run(new EditCommand(
            "Re-attach a journal date",
            () => JournalEdits.ReattachOrphan(journal, record, entry, BookYear),
            () =>
            {
                JournalEdits.Restore(journal, entry, before, BookYear);
                JournalEdits.RestoreOrphan(journal, record, index);
            }),
            journal);
    }

    /// <summary>Lets an orphaned assignment go.</summary>
    /// <param name="orphan">The orphan to forget.</param>
    [RelayCommand]
    private void DiscardOrphan(JournalOrphanViewModel? orphan)
    {
        if (orphan is null)
        {
            return;
        }

        var journal = _session.Journal;
        var record = orphan.Orphan;
        var index = journal.ImportReport.OrphanedUserAssignments.IndexOf(record);

        Run(new EditCommand(
            "Discard a journal date",
            () => JournalEdits.DiscardOrphan(journal, record),
            () => JournalEdits.RestoreOrphan(journal, record, index)),
            journal);
    }

    private void Assign(JournalEntryRowViewModel row, DateOnly date)
    {
        var journal = _session.Journal;
        var entry = row.Entry;
        var before = JournalEdits.Capture(entry);
        var label = date.ToString("d MMM yyyy", CultureInfo.CurrentCulture);

        Run(new EditCommand(
            $"Date journal entry {label}",
            () => JournalEdits.AssignDate(journal, entry, date, BookYear),
            () => JournalEdits.Restore(journal, entry, before, BookYear)),
            journal);
    }

    private void SetExcluded(JournalEntryRowViewModel row, bool excluded)
    {
        var journal = _session.Journal;
        var entry = row.Entry;
        var before = JournalEdits.Capture(entry);

        Run(new EditCommand(
            excluded ? "Discard journal entry" : "Restore journal entry",
            () => JournalEdits.SetExcluded(journal, entry, excluded, BookYear),
            () => JournalEdits.Restore(journal, entry, before, BookYear)),
            journal);
    }

    /// <summary>
    /// Runs an edit as one undo entry and re-reads the report afterwards. The document the edit was
    /// made against is captured: a re-import replaces the whole <see cref="JournalDocument"/>, and an
    /// undo that then wrote to the entries of a document no longer in the project would change
    /// nothing the user can see while claiming it had.
    /// </summary>
    private void Run(IUndoableCommand command, JournalDocument journal)
    {
        _undo.Execute(new EditCommand(
            command.Description,
            () =>
            {
                if (!ReferenceEquals(_session.Journal, journal)) return;
                command.Do();
                AfterEdit();
            },
            () =>
            {
                if (!ReferenceEquals(_session.Journal, journal)) return;
                command.Undo();
                AfterEdit();
            }));
    }

    private void AfterEdit()
    {
        _session.MarkDirty();
        Load();
        Changed?.Invoke();
    }

    private int CountIn(JournalReviewBucket bucket) =>
        Groups.FirstOrDefault(g => g.Bucket == bucket)?.Rows.Count ?? 0;

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(HasJournal));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(DatedCount));
        OnPropertyChanged(nameof(AmbiguousCount));
        OnPropertyChanged(nameof(UndatedCount));
        OnPropertyChanged(nameof(OutOfYearCount));
        OnPropertyChanged(nameof(DiscardedCount));
        OnPropertyChanged(nameof(OrphanCount));
        OnPropertyChanged(nameof(EntryCount));
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(SummarySentence));
        OnPropertyChanged(nameof(BookYear));
    }

    private int PhotosOn(DateOnly date) =>
        _session.IsOpen ? _session.Catalog.Photos.Count(p => !p.Excluded && p.TakenOn == date) : 0;

    private bool EntryOn(DateOnly date) => _session.Journal.EntriesOn(date).Any();

    private static string Count(int n, string singular, string plural) =>
        $"{n.ToString(CultureInfo.CurrentCulture)} {(n == 1 ? singular : plural)}";
}
