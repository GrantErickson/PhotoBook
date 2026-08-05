using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoBook.App.Services;

namespace PhotoBook.App.ViewModels.Journal;

/// <summary>The two halves of journal review.</summary>
public enum JournalReviewSection
{
    /// <summary>The Import Report: what the matcher made of each entry, and where dates get fixed.</summary>
    Report,

    /// <summary>The month day by day: which pages actually carry each day's text.</summary>
    Days,
}

/// <summary>
/// Journal review as a whole (doc 11): the Import Report and the day map, over one open project.
///
/// <para>
/// It opens itself after an import that produced anything worth saying — that is the point, since the
/// alternative is text silently missing from the book — and it is reachable at any time afterwards,
/// because a journal is re-imported many times and the dates the user assigned are permanent work,
/// not a modal moment.
/// </para>
/// </summary>
public sealed partial class JournalReviewViewModel : ObservableObject
{
    private readonly ProjectSession _session;

    /// <param name="session">The open project — the owner of the journal.</param>
    /// <param name="undo">The book's undo stack; every assignment is one entry on it.</param>
    public JournalReviewViewModel(ProjectSession session, UndoStack undo)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(undo);

        _session = session;
        Report = new JournalImportReportViewModel(session, undo);
        Days = new JournalDayMapViewModel(session);

        // A date assigned in the report moves text into a month, so the map is stale the moment the
        // report changes. Nothing in the other direction: the map never writes.
        Report.Changed += OnReportChanged;
        Days.PageActivated += (month, pageId) => PageActivated?.Invoke(month, pageId);
    }

    /// <summary>Raised after any journal edit, so the shell can re-render pages and re-count chapters.</summary>
    public event Action? Changed;

    /// <summary>Raised when a page chip is clicked: the chapter month and the page id to open.</summary>
    public event Action<int, string>? PageActivated;

    /// <summary>Raised when the user is done, for a host that is a window.</summary>
    public event Action? CloseRequested;

    /// <summary>The Import Report.</summary>
    public JournalImportReportViewModel Report { get; }

    /// <summary>The month's days and the pages carrying them.</summary>
    public JournalDayMapViewModel Days { get; }

    /// <summary>Which half is showing.</summary>
    [ObservableProperty]
    private JournalReviewSection _section = JournalReviewSection.Report;

    /// <summary>True when the report is showing — the segmented switcher's checked state.</summary>
    public bool IsReportSection => Section == JournalReviewSection.Report;

    /// <summary>True when the day map is showing.</summary>
    public bool IsDaysSection => Section == JournalReviewSection.Days;

    /// <summary>True when a book is open; without one there is nothing to review.</summary>
    public bool HasProject => _session.IsOpen;

    /// <summary>True when a journal has been imported at all.</summary>
    public bool HasJournal => Report.HasJournal;

    /// <summary>True when something in the report wants the user — what decides whether it opens itself.</summary>
    public bool HasFindings => Report.HasFindings;

    /// <summary>The count badge on the report tab: entries that still need a decision.</summary>
    public int AttentionCount => Report.UndatedCount + Report.AmbiguousCount +
                                 Report.OutOfYearCount + Report.OrphanCount;

    /// <summary>The count badge on the day map tab: days whose text is on no page.</summary>
    public int HomelessDayCount => Days.HomelessDayCount;

    /// <summary>Reads the journal and the month afresh.</summary>
    /// <param name="month">The month the day map should show — normally the shell's selected chapter.</param>
    public void Load(int month)
    {
        Report.Load();
        Days.Load(month);
        RaiseBadges();
    }

    /// <summary>
    /// Called after an import finishes. Loads the new document and reports whether the panel should be
    /// put in front of the user: doc 11 opens it whenever the import produced any non-matched entry.
    /// </summary>
    /// <param name="month">The month the day map should show.</param>
    /// <returns>True when there is something the user needs to see.</returns>
    public bool LoadAfterImport(int month)
    {
        Section = JournalReviewSection.Report;
        Load(month);
        return HasFindings;
    }

    /// <summary>Switches half.</summary>
    /// <param name="section">The half to show.</param>
    [RelayCommand]
    private void SelectSection(JournalReviewSection section) => Section = section;

    /// <summary>Closes the review.</summary>
    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    partial void OnSectionChanged(JournalReviewSection value)
    {
        OnPropertyChanged(nameof(IsReportSection));
        OnPropertyChanged(nameof(IsDaysSection));

        // The map is only correct for the pages as they stand now, and a layout run may have happened
        // while the report was open.
        if (value == JournalReviewSection.Days)
        {
            Days.Refresh();
            RaiseBadges();
        }
    }

    private void OnReportChanged()
    {
        Days.Refresh();
        RaiseBadges();
        Changed?.Invoke();
    }

    private void RaiseBadges()
    {
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(HasJournal));
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(AttentionCount));
        OnPropertyChanged(nameof(HomelessDayCount));
    }
}
