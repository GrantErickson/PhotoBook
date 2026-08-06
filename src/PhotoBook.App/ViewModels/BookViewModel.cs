using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoBook.App.Services;
using PhotoBook.App.ViewModels.Export;
using PhotoBook.App.ViewModels.Journal;
using PhotoBook.App.ViewModels.Pages;
using PhotoBook.App.ViewModels.Photos;
using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;
using PhotoBook.Core.Persistence;
using PhotoBook.Rendering;

namespace PhotoBook.App.ViewModels;

/// <summary>A month in the chapter rail.</summary>
public sealed partial class ChapterItemViewModel : ObservableObject
{
    public ChapterItemViewModel(int month) => Month = month;

    public int Month { get; }

    public string Name => CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(Month);

    public string ShortName => CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedMonthName(Month);

    [ObservableProperty]
    private int _photoCount;

    [ObservableProperty]
    private int _pageCount;

    [ObservableProperty]
    private bool _isSelected;

    public bool HasPhotos => PhotoCount > 0;

    public void Update(int photos, int pages)
    {
        PhotoCount = photos;
        PageCount = pages;
        OnPropertyChanged(nameof(HasPhotos));
    }
}

/// <summary>The open book: the chapter rail, the Photos grid, the Pages editor, and the commands.</summary>
public sealed partial class BookViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly JobQueue _jobs;
    private readonly ThumbnailProvider _thumbnails;
    private readonly AutoAdjustRunner? _autoAdjust;

    private bool _syncingPageContext;

    public BookViewModel(
        ProjectSession session,
        JobQueue jobs,
        ThumbnailProvider thumbnails,
        UndoStack undo,
        PageEditorViewModel pageEditor,
        BinsViewModel bins,
        TemplatePickerViewModel templates,
        LayoutCommandsViewModel layoutCommands,
        PageOverrideViewModel pageOverride,
        PhotoInspectorViewModel inspector,
        StyleViewModel style,
        ExportViewModel export,
        JournalReviewViewModel journalReview,
        AutoAdjustRunner? autoAdjust = null)
    {
        _autoAdjust = autoAdjust;
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(undo);
        ArgumentNullException.ThrowIfNull(pageEditor);
        ArgumentNullException.ThrowIfNull(bins);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(layoutCommands);
        ArgumentNullException.ThrowIfNull(pageOverride);
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(journalReview);

        _session = session;
        _jobs = jobs;
        _thumbnails = thumbnails;

        Undo = undo;
        PageEditor = pageEditor;
        Bins = bins;
        Templates = templates;
        LayoutCommands = layoutCommands;
        PageOverride = pageOverride;
        Inspector = inspector;
        Style = style;
        Export = export;
        JournalReview = journalReview;

        for (var month = 1; month <= 12; month++)
        {
            Chapters.Add(new ChapterItemViewModel(month));
        }

        WireEditors();
    }

    /// <summary>The book's undo history — one stack, shared by every edit surface (doc 09 §4).</summary>
    public UndoStack Undo { get; }

    /// <summary>The Pages tab canvas, navigator and crop model.</summary>
    public PageEditorViewModel PageEditor { get; }

    /// <summary>Unplaced, Upcoming and the Outside-book tray (doc 09 §3.5).</summary>
    public BinsViewModel Bins { get; }

    /// <summary>The template gallery for the current page (doc 09 §3.4).</summary>
    public TemplatePickerViewModel Templates { get; }

    /// <summary>The three R16 auto-layout commands (doc 09 §3.8).</summary>
    public LayoutCommandsViewModel LayoutCommands { get; }

    /// <summary>Per-page layout override mode (doc 09 §3.7).</summary>
    public PageOverrideViewModel PageOverride { get; }

    /// <summary>The Photos tab inspector: date, focus, adjustments, reorder (doc 09 §2).</summary>
    public PhotoInspectorViewModel Inspector { get; }

    /// <summary>Book / chapter / page style overrides (R23).</summary>
    public StyleViewModel Style { get; }

    /// <summary>Preflight and PDF export (doc 12).</summary>
    public ExportViewModel Export { get; }

    /// <summary>The journal import report and day map (doc 11).</summary>
    public JournalReviewViewModel JournalReview { get; }

    // The bin panel is one control that moves between three cells of the Pages grid rather than
    // three controls with three copies of the thumbnails (doc 09 §3.5 — the dock edge is a user
    // preference, not a different panel).

    /// <summary>Grid column the bin panel occupies for its current dock edge.</summary>
    public int BinColumn => Bins.IsLeftDock ? 0 : Bins.IsRightDock ? 2 : 1;

    /// <summary>Grid row the bin panel occupies: bottom dock is the second row.</summary>
    public int BinRow => Bins.IsBottomDock ? 1 : 0;

    /// <summary>A side-docked bin spans the canvas and the bottom row.</summary>
    public int BinRowSpan => Bins.IsBottomDock ? 1 : 2;

    private void OnBinSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorSettings.BinDock))
        {
            OnPropertyChanged(nameof(BinColumn));
            OnPropertyChanged(nameof(BinRow));
            OnPropertyChanged(nameof(BinRowSpan));
        }
    }

    /// <summary>
    /// Connects the five editing surfaces to each other. Everything below is a one-way wire from a
    /// feature's event to the shell's job of keeping the other surfaces truthful; no feature knows
    /// about any other.
    /// </summary>
    private void WireEditors()
    {
        // Any undoable edit dirties the project, so autosave and the title bar keep up. Undoing or
        // redoing can also move a photo in time (a re-date or a grid reorder), and nothing else is
        // listening on that path, so the grid re-reads itself here.
        Undo.Changed += (_, e) =>
        {
            _session.MarkDirty();

            if (e.Kind is UndoStackChange.Undone or UndoStackChange.Redone)
            {
                foreach (var item in Photos)
                {
                    item.Refresh();
                }

                ResortPhotos();
                RefreshChapters();
            }
        };

        Bins.Settings.PropertyChanged += OnBinSettingsChanged;

        // The save indicator is driven by the autosave loop rather than by whoever last called Save:
        // "Saved 14:32" has to mean a write actually completed (doc 04 §6).
        _session.SaveStatusChanged += _ => RefreshSaveStatus();
        _session.SaveFailed += ex => ErrorRaised?.Invoke(
            $"PhotoBook could not save this project: {ex.Message}\n\n" +
            "Your edits are still here and it will keep trying every 30 seconds. " +
            "Check the project folder is reachable and not read-only.");

        // Journal review edits dates, which moves text between months; nothing re-runs layout, so the
        // pages that already carry text just have to redraw.
        JournalReview.Changed += () =>
        {
            PageEditor.Refresh();
            RefreshChapters();
            RaiseJournalBadges();
        };
        JournalReview.PageActivated += OnJournalPageActivated;

        PageEditor.PropertyChanged += OnPageEditorPropertyChanged;
        PageEditor.PagesChanged += (_, _) => OnPagesChanged();
        PageEditor.StatusRaised += (_, message) => StatusMessage = message;
        PageEditor.FillSlotRequested += (_, e) => OnFillSlotRequested(e);

        Bins.JumpToPageRequested += JumpToPage;
        Bins.InspectorRequested += OnBinInspectorRequested;

        Templates.PageInvalidated += () =>
        {
            PageEditor.Refresh();
            RefreshChapters();
            Bins.Refresh();
            LayoutCommands.Refresh();
        };
        Templates.CloseRequested += () => IsTemplatePickerOpen = false;

        LayoutCommands.ChapterChanged += month =>
        {
            if (SelectedChapter?.Month == month)
            {
                PageEditor.Refresh();
            }

            RebuildPages(month);
            SyncPageContext();
            RefreshChapters();
        };
        LayoutCommands.ErrorRaised += message => ErrorRaised?.Invoke(message);

        PageOverride.Invalidated += () =>
        {
            PageEditor.Refresh();
            Bins.Refresh();
        };

        Style.StyleChanged += () => PageEditor.Refresh();

        Inspector.StatusRaised += message => StatusMessage = message;

        // A grid reorder re-stamps capture times, so the grid has to re-sort or the tiles show
        // their new times in their old order (doc 09 §2.5).
        Inspector.Reorder.Reordered += outcome =>
        {
            if (outcome.Applied)
            {
                ResortPhotos();
            }

            StatusMessage = outcome.Message;
        };
        Inspector.PhotoMoved += outcome =>
        {
            RefreshChapters();
            if (SelectedChapter is { } chapter)
            {
                LoadChapter(chapter.Month);
            }

            StatusMessage = outcome.Message;
        };
    }

    /// <summary>
    /// Puts the grid back in chronological order in place, so tiles slide rather than the whole
    /// collection resetting and losing the selection and the loaded thumbnails.
    /// </summary>
    private void ResortPhotos()
    {
        var ordered = Photos.OrderBy(p => p.TakenAt).ThenBy(p => p.FileName, StringComparer.Ordinal).ToList();
        for (var target = 0; target < ordered.Count; target++)
        {
            var current = Photos.IndexOf(ordered[target]);
            if (current != target)
            {
                Photos.Move(current, target);
            }
        }
    }

    private void OnPageEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PageEditorViewModel.SelectedPage))
        {
            SyncPageContext();
        }
    }

    private void OnPagesChanged()
    {
        if (SelectedChapter is { } chapter)
        {
            RebuildPages(chapter.Month);
        }

        SyncPageContext();
        RefreshChapters();
    }

    /// <summary>
    /// Points the bins, the template gallery, override mode, the style panel and the auto-layout
    /// commands at the page the canvas is showing. Called on every page and chapter change.
    /// </summary>
    private void SyncPageContext()
    {
        if (_syncingPageContext)
        {
            return;
        }

        _syncingPageContext = true;
        try
        {
            var month = SelectedChapter?.Month ?? 1;
            var chapter = _session.Chapters.FirstOrDefault(c => c.Month == month);
            var page = PageEditor.CurrentPage;
            var number = PageEditor.SelectedPage?.Number ?? 0;

            Bins.SetContext(month, page, number);
            Templates.SetContext(chapter, page, number);
            PageOverride.Attach(chapter, page);
            Style.Attach(chapter, page);

            LayoutCommands.Month = month;
            LayoutCommands.CurrentPage = page;
            LayoutCommands.CurrentPageNumber = Math.Max(1, number);
            LayoutCommands.Refresh();

            SelectedPage = Pages.FirstOrDefault(p => ReferenceEquals(p.Page, page));
        }
        finally
        {
            _syncingPageContext = false;
        }
    }

    /// <summary>
    /// R14: an empty amber slot was clicked. The bin opens on the tab that can actually fill it,
    /// with the best-fitting photo preselected, so the next gesture is one drag onto the hole.
    /// </summary>
    private void OnFillSlotRequested(FillSlotRequestedEventArgs e)
    {
        Bins.Settings.BinVisible = true;
        IsTemplatePickerOpen = false;

        var tab = Bins.UnplacedCount > 0 ? BinTab.Unplaced
            : Bins.UpcomingCount > 0 ? BinTab.Upcoming
            : BinTab.Unplaced;
        Bins.Tab = tab;

        var slot = e.Page.ResolveTemplate(_session.FindTemplate)?.FindSlot(e.SlotId);
        var best = slot is null ? [] : Bins.BestFitFor(slot, tab);
        Bins.SelectedItem = best.FirstOrDefault() ?? Bins.Items.FirstOrDefault();

        StatusMessage = Bins.SelectedItem is null
            ? "Nothing in the bin to fill that slot with yet."
            : $"Drag a photo from the bin onto the empty slot — “{Bins.SelectedItem.FileName}” fits it best.";
    }

    private void OnBinInspectorRequested(Photo photo, string section)
    {
        var item = Photos.FirstOrDefault(p => ReferenceEquals(p.Photo, photo));
        if (item is null)
        {
            return;
        }

        IsPagesTab = false;
        SelectedPhoto = item;

        switch (section)
        {
            case "date":
                Inspector.ChangeDateCommand.Execute(null);
                break;
            case "focus":
                Inspector.EditFocusCommand.Execute(null);
                break;
            default:
                StatusMessage = $"{item.FileName} selected — the inspector is on the right.";
                break;
        }
    }

    /// <summary>Selects a chapter page by its 1-based number, switching to the Pages tab.</summary>
    public void JumpToPage(int number)
    {
        var page = PageEditor.Pages.FirstOrDefault(p => p.Number == number);
        if (page is null)
        {
            return;
        }

        IsPagesTab = true;
        PageEditor.SelectedPage = page;
    }

    /// <summary>
    /// Doc 12's promise that every preflight row is a link: goes to the chapter, page and slot the
    /// finding names. Page ids are matched rather than book-wide numbers, because the editor numbers
    /// pages within a chapter.
    /// </summary>
    private void NavigateToFinding(PreflightFinding finding)
    {
        var chapter = _session.Chapters.FirstOrDefault(c => c.Pages.Any(p => p.Id == finding.PageId))
                      ?? _session.Chapters.FirstOrDefault(c => c.Month == finding.ChapterMonth);
        if (chapter is null)
        {
            return;
        }

        var target = Chapters.FirstOrDefault(c => c.Month == chapter.Month);
        if (target is not null && !ReferenceEquals(target, SelectedChapter))
        {
            SelectedChapter = target;
        }

        IsPagesTab = true;

        if (PageEditor.Pages.FirstOrDefault(p => p.Page.Id == finding.PageId) is { } page)
        {
            PageEditor.SelectedPage = page;
            if (finding.SlotId is { } slot)
            {
                PageEditor.SelectSlot(page.Page, slot);
            }
        }

        StatusMessage = finding.Title;
    }

    /// <summary>Clears the undo history and re-points every editor at the freshly opened book.</summary>
    public void OnBookOpened()
    {
        Undo.Clear();
        RefreshChapters();
        RefreshSaveStatus();

        // Reads the journal that came with the project, so the Journal button's badge is honest
        // before the user has opened anything.
        RefreshJournal(reload: true);
    }

    /// <summary>Drops per-book state when the book closes: the stack is never persisted (doc 09 §4).</summary>
    public void OnBookClosed()
    {
        Undo.Clear();
        IsTemplatePickerOpen = false;
        IsStylePanelOpen = false;
        PageOverride.IsActive = false;
        CloseQuickPreview();
        RefreshSaveStatus();
        Photos.Clear();
        Pages.Clear();
        UnplacedBin.Clear();
        PageEditor.LoadChapter(null);
        Inspector.Select(null);
    }

    public ProjectSession Session => _session;

    public ObservableCollection<ChapterItemViewModel> Chapters { get; } = [];

    public ObservableCollection<PhotoItemViewModel> Photos { get; } = [];

    public ObservableCollection<PageItemViewModel> Pages { get; } = [];

    public string BookTitle => _session.Book?.Title ?? string.Empty;

    public int Year => _session.Book?.Year ?? DateTime.Now.Year;

    [ObservableProperty]
    private ChapterItemViewModel? _selectedChapter;

    [ObservableProperty]
    private PhotoItemViewModel? _selectedPhoto;

    [ObservableProperty]
    private PageItemViewModel? _selectedPage;

    [ObservableProperty]
    private bool _isPagesTab;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>True while the template gallery drawer is open (the <c>T</c> key, doc 09 §3.4).</summary>
    [ObservableProperty]
    private bool _isTemplatePickerOpen;

    /// <summary>True while the style drawer is open (R23).</summary>
    [ObservableProperty]
    private bool _isStylePanelOpen;

    partial void OnIsTemplatePickerOpenChanged(bool value)
    {
        if (value)
        {
            IsStylePanelOpen = false;
        }
    }

    partial void OnIsStylePanelOpenChanged(bool value)
    {
        if (value)
        {
            IsTemplatePickerOpen = false;
        }
    }

    partial void OnSelectedPhotoChanged(PhotoItemViewModel? value)
    {
        foreach (var item in Photos)
        {
            item.IsSelected = ReferenceEquals(item, value);
        }

        Inspector.Select(value);
    }

    /// <summary>Photos of the month that no page uses — the Unplaced bin (R10, R13).</summary>
    public ObservableCollection<PhotoItemViewModel> UnplacedBin { get; } = [];

    /// <summary>
    /// Raised for messages too long or too important for the status line — setup instructions and
    /// sign-in failures. The shell shows these in the dismissible toast, which wraps.
    /// </summary>
    public event Action<string>? ErrorRaised;

    /// <summary>
    /// Raised for something the user should know that is <em>not</em> a failure — the shell shows it
    /// in the notice banner. Arguments are the headline and the explanation.
    /// </summary>
    public event Action<string, string>? NoticeRaised;

    public bool HasPhotos => Photos.Count > 0;

    public bool HasPages => Pages.Count > 0;

    // ---------------------------------------------------------------- save indicator

    /// <summary>
    /// What the autosave loop is doing, verbatim from the session. The strip binds to this rather
    /// than to a bool the shell maintains, so it can never claim a save that did not happen.
    /// </summary>
    public AutosaveState SaveState => _session.SaveStatus.State;

    /// <summary>"Saved 14:32" / "Saving…" / "Unsaved changes" / "Not saved — {reason}".</summary>
    public string SaveStatusLabel => _session.IsOpen ? _session.SaveStatus.Label : string.Empty;

    /// <summary>The longer form for the button's tooltip.</summary>
    public string SaveStatusTooltip => SaveState switch
    {
        AutosaveState.Saving => "Writing the project to disk…",
        AutosaveState.Dirty => "There are edits not yet on disk. They save automatically within " +
                               $"{_session.AutosaveInterval.TotalSeconds:0} seconds — or press Ctrl+S now.",
        AutosaveState.Failed => _session.SaveStatus.Error is { } error
            ? $"The last save failed: {error}. PhotoBook keeps retrying; Ctrl+S tries again now."
            : "The last save failed. PhotoBook keeps retrying; Ctrl+S tries again now.",
        _ => "Everything is on disk. Ctrl+S saves now; autosave runs every " +
             $"{_session.AutosaveInterval.TotalSeconds:0} seconds anyway.",
    };

    private void RefreshSaveStatus()
    {
        OnPropertyChanged(nameof(SaveState));
        OnPropertyChanged(nameof(SaveStatusLabel));
        OnPropertyChanged(nameof(SaveStatusTooltip));
    }

    // ---------------------------------------------------------------- journal review

    /// <summary>Entries still wanting a date, for the Journal button's amber badge (doc 11).</summary>
    public int JournalAttentionCount => JournalReview.AttentionCount;

    /// <summary>True when the journal has anything the user should look at.</summary>
    public bool HasJournalFindings => JournalReview.HasFindings;

    /// <summary>Re-reads the journal and re-raises the button's badge.</summary>
    /// <param name="reload">
    /// True when the document or the book's year may have moved under the report — an import, or a
    /// settings change that redefines which entries fall outside the year.
    /// </param>
    private void RefreshJournal(bool reload)
    {
        if (reload && _session.IsOpen)
        {
            JournalReview.Load(SelectedChapter?.Month ?? 1);
        }

        RaiseJournalBadges();
    }

    private void RaiseJournalBadges()
    {
        OnPropertyChanged(nameof(JournalAttentionCount));
        OnPropertyChanged(nameof(HasJournalFindings));
    }

    /// <summary>
    /// A page chip in the day map was clicked: go to that chapter and page. The map speaks in page
    /// ids because a re-layout renumbers pages, and an id survives that.
    /// </summary>
    private void OnJournalPageActivated(int month, string pageId)
    {
        var target = Chapters.FirstOrDefault(c => c.Month == month);
        if (target is not null && !ReferenceEquals(target, SelectedChapter))
        {
            SelectedChapter = target;
        }

        IsPagesTab = true;

        if (PageEditor.Pages.FirstOrDefault(p => string.Equals(p.Page.Id, pageId, StringComparison.Ordinal))
            is { } page)
        {
            PageEditor.SelectedPage = page;
        }

        System.Windows.Application.Current?.MainWindow?.Activate();
    }

    /// <summary>
    /// Opens journal review at any time (doc 11: the report is not a modal moment — dating a long
    /// journal is work the user comes back to).
    /// </summary>
    [RelayCommand]
    private void OpenJournalReview()
    {
        if (!_session.IsOpen || System.Windows.Application.Current?.MainWindow is not { } owner)
        {
            return;
        }

        Views.Journal.JournalReviewWindow.ShowReview(owner, JournalReview, SelectedChapter?.Month ?? 1);
        RaiseJournalBadges();
    }

    // ---------------------------------------------------------------- book settings

    /// <summary>
    /// Title, year, page size, print profile and layout seed (R3, R19). A staging form: nothing moves
    /// until Apply, and Apply is one undo entry including any re-layout it made necessary.
    /// </summary>
    [RelayCommand]
    private void OpenBookSettings()
    {
        if (!_session.IsOpen)
        {
            return;
        }

        var model = new BookSettingsViewModel(_session, Undo, _jobs)
        {
            Look = _autoAdjust is null ? null : new LookViewModel(_session, _autoAdjust),
        };
        var touched = new HashSet<int>();
        model.ChapterChanged += month => touched.Add(month);

        if (!Views.BookSettingsWindow.Show(System.Windows.Application.Current?.MainWindow, model))
        {
            return;
        }

        // A page-size change re-lays out chapters and a year change moves every chapter, so the whole
        // shell is re-read rather than patched: cheaper to be sure than to be clever.
        RefreshChapters();
        if (SelectedChapter is { } chapter)
        {
            LoadChapter(chapter.Month);
        }

        // A year change redefines which journal entries fall outside the book, so the report's
        // counts — and the badge on the Journal button — are stale until it re-reads.
        RefreshJournal(reload: true);

        StatusMessage = touched.Count > 0
            ? $"Book settings applied; {touched.Count} month{(touched.Count == 1 ? "" : "s")} re-laid out. Ctrl+Z undoes all of it."
            : "Book settings applied. Ctrl+Z undoes it.";
    }

    /// <summary>Doc 09 §5's keyboard map, where a user can actually find it.</summary>
    [RelayCommand]
    private void ShowShortcuts() =>
        Views.ShortcutsWindow.Show(System.Windows.Application.Current?.MainWindow);

    // ---------------------------------------------------------------- quick preview

    /// <summary>True while <c>Space</c>'s full-size preview is over the grid (doc 09 §2).</summary>
    [ObservableProperty]
    private bool _isQuickPreviewOpen;

    /// <summary>The bitmap the quick preview is showing — the grid tile first, then the 1024 px tier.</summary>
    [ObservableProperty]
    private BitmapSource? _quickPreviewImage;

    /// <summary>The previewed photo's file name.</summary>
    [ObservableProperty]
    private string _quickPreviewTitle = string.Empty;

    /// <summary>When it was taken and what tier it sits in.</summary>
    [ObservableProperty]
    private string _quickPreviewSubtitle = string.Empty;

    /// <summary>
    /// <c>Space</c> on the Photos tab: a look at the photo big, without leaving the grid or opening a
    /// window. Pressing it again (or <c>Esc</c>, or a click) puts it away.
    /// </summary>
    [RelayCommand]
    private void ToggleQuickPreview()
    {
        if (IsQuickPreviewOpen || SelectedPhoto is not { } item)
        {
            CloseQuickPreview();
            return;
        }

        QuickPreviewTitle = item.FileName;
        QuickPreviewSubtitle = $"{item.TakenAtDisplay}  ·  tier {item.TierLabel}" +
                               (item.DateUncertain ? "  ·  date guessed from the file" : string.Empty);

        // Show the grid thumbnail at once so the key feels instant, then swap in the preview tier
        // when it arrives. The UI thread never decodes (doc 09 §6).
        QuickPreviewImage = item.Thumbnail;
        IsQuickPreviewOpen = true;
        _ = LoadQuickPreviewAsync(item);
    }

    private async Task LoadQuickPreviewAsync(PhotoItemViewModel item)
    {
        BitmapSource? full = null;
        try
        {
            full = await _thumbnails
                .GetAsync(item.Photo.ContentHash, ThumbnailTier.Preview1024)
                .ConfigureAwait(true);
        }
        catch
        {
            // An unreadable original leaves the grid thumbnail on screen, which is still a preview.
        }

        if (full is not null && IsQuickPreviewOpen && ReferenceEquals(SelectedPhoto, item))
        {
            QuickPreviewImage = full;
        }
    }

    /// <summary>Puts the quick preview away.</summary>
    [RelayCommand]
    private void CloseQuickPreview()
    {
        IsQuickPreviewOpen = false;
        QuickPreviewImage = null;
    }

    // ---------------------------------------------------------------- loading

    /// <summary>Rebuilds the rail counts from the model.</summary>
    public void RefreshChapters()
    {
        var book = _session.Book;
        if (book is null)
        {
            return;
        }

        foreach (var chapter in Chapters)
        {
            var photos = _session.Catalog.InChapter(book.Year, chapter.Month).Count(p => !p.Excluded);
            var pages = _session.Chapters.FirstOrDefault(c => c.Month == chapter.Month)?.Pages.Count ?? 0;
            chapter.Update(photos, pages);
        }

        OnPropertyChanged(nameof(BookTitle));
        OnPropertyChanged(nameof(Year));
        OnPropertyChanged(nameof(HasOneDriveSource));
        OnPropertyChanged(nameof(OneDriveSourceLabel));
    }

    partial void OnSelectedChapterChanged(ChapterItemViewModel? value)
    {
        foreach (var chapter in Chapters)
        {
            chapter.IsSelected = ReferenceEquals(chapter, value);
        }

        if (value is not null)
        {
            LoadChapter(value.Month);
        }
    }

    /// <summary>Populates the grid and page list for a month.</summary>
    public void LoadChapter(int month)
    {
        var book = _session.Book;
        if (book is null)
        {
            return;
        }

        Photos.Clear();
        foreach (var photo in _session.Catalog.InChapter(book.Year, month).OrderBy(p => p.TakenAt))
        {
            Photos.Add(new PhotoItemViewModel(photo, _thumbnails));
        }

        RebuildPages(month);
        SelectedPhoto = Photos.FirstOrDefault();
        SelectedPage = Pages.FirstOrDefault();

        // The page editor owns its own navigator and renders its own canvas from here on.
        PageEditor.LoadChapter(month);
        SyncPageContext();

        // A month that is already laid out is more useful opened on its pages than its grid.
        IsPagesTab = Pages.Count > 0;

        OnPropertyChanged(nameof(HasPhotos));
        OnPropertyChanged(nameof(HasPages));
        _ = LoadThumbnailsAsync();
    }

    private void RebuildPages(int month)
    {
        Pages.Clear();
        var chapter = _session.Chapters.FirstOrDefault(c => c.Month == month);
        if (chapter is not null)
        {
            for (var i = 0; i < chapter.Pages.Count; i++)
            {
                Pages.Add(new PageItemViewModel(chapter.Pages[i], i + 1));
            }
        }

        RebuildUnplacedBin(month);
        OnPropertyChanged(nameof(HasPages));
    }

    private void RebuildUnplacedBin(int month)
    {
        UnplacedBin.Clear();
        var chapter = _session.Chapters.FirstOrDefault(c => c.Month == month);
        if (chapter is null)
        {
            return;
        }

        var placed = chapter.Pages
            .SelectMany(p => p.Placements)
            .Select(p => p.PhotoId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var item in Photos.Where(p => !p.Excluded && !placed.Contains(p.Photo.Id)))
        {
            UnplacedBin.Add(item);
        }
    }

    private async Task LoadThumbnailsAsync()
    {
        foreach (var item in Photos.ToList())
        {
            try
            {
                await item.EnsureThumbnailAsync().ConfigureAwait(false);
            }
            catch
            {
                // A single unreadable file must not stop the grid filling in.
            }
        }
    }

    // ---------------------------------------------------------------- commands

    [RelayCommand]
    private void SelectChapter(ChapterItemViewModel? chapter)
    {
        if (chapter is not null)
        {
            SelectedChapter = chapter;
        }
    }

    [RelayCommand]
    private void ShowPhotos() => IsPagesTab = false;

    [RelayCommand]
    private void ShowPages() => IsPagesTab = true;

    /// <summary>Ctrl+Tab: the two halves of the month workspace (doc 09 §1).</summary>
    [RelayCommand]
    private void ToggleTab() => IsPagesTab = !IsPagesTab;

    /// <summary>The template gallery (the <c>T</c> key).</summary>
    [RelayCommand]
    private void ToggleTemplatePicker()
    {
        if (!IsTemplatePickerOpen && PageEditor.CurrentPage is null)
        {
            StatusMessage = "Lay the month out first — a template applies to a page.";
            return;
        }

        IsPagesTab = true;
        IsTemplatePickerOpen = !IsTemplatePickerOpen;
    }

    /// <summary>The style panel (R23).</summary>
    [RelayCommand]
    private void ToggleStylePanel() => IsStylePanelOpen = !IsStylePanelOpen;

    /// <summary>The bin panel (the <c>B</c> key).</summary>
    [RelayCommand]
    private void ToggleBins() => Bins.ToggleVisibleCommand.Execute(null);

    /// <summary>Preflight + PDF export as a modal (doc 12).</summary>
    [RelayCommand]
    private void OpenExport()
    {
        if (!_session.IsOpen)
        {
            return;
        }

        var window = new Views.Export.ExportWindow(Export, SelectedChapter?.Month)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };

        window.NavigationRequested += NavigateToFinding;

        window.ShowDialog();
        StatusMessage = Export.ResultMessage.Length > 0 ? Export.ResultMessage : StatusMessage;
    }

    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a folder of photos to import",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;
        var photosBefore = _session.PhotoCount;
        await _jobs.RunAsync("Importing photos", async job =>
        {
            job.Status = "Counting photos…";
            var expected = CountSupportedFiles(dialog.FolderName, job.Cancellation.Token);

            var progress = new Progress<Ingestion.PhotoImportProgress>(p =>
                ReportImportProgress(job, p, expected));

            var report = await _session
                .ImportFolderAsync(dialog.FolderName, progress, job.Cancellation.Token)
                .ConfigureAwait(false);

            var added = report.Entries.Count(e => e.Outcome == Ingestion.PhotoImportOutcome.Added);
            var skipped = report.Entries.Count - added;

            JobQueue.PostUi(() =>
            {
                StatusMessage = skipped > 0
                    ? $"Imported {added} photo{(added == 1 ? "" : "s")}; skipped {skipped} already in the book."
                    : $"Imported {added} photo{(added == 1 ? "" : "s")}.";

                if (_session.ReconcileYearAfterImport(photosBefore) is { } note)
                {
                    ErrorRaised?.Invoke(note);
                }

                RefreshChapters();
                JumpToFirstMonthWithPhotos();
            });
        }).ConfigureAwait(true);

        IsBusy = false;
        await AnalyzeAsync().ConfigureAwait(true);
        await AutoAdjustImportedAsync().ConfigureAwait(true);
        await SaveAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Auto-adjusts what an import just brought in, when the book asks for it.
    /// <para>
    /// Off by default and opted into per book, because correcting photos someone has not looked at
    /// yet is a surprise the first time and a convenience only once they trust the result. It reuses
    /// the ordinary batch, which by construction touches nothing already on the shelf: an existing
    /// photo is either up to date or hand-edited.
    /// </para>
    /// </summary>
    private async Task AutoAdjustImportedAsync()
    {
        if (_autoAdjust is null || _session.Book?.Look.AdjustOnImport != true)
        {
            return;
        }

        var plan = _autoAdjust.Describe();
        if (plan.Work(includeManual: false) == 0)
        {
            return;
        }

        await _jobs.RunAsync("Auto-adjusting new photos", async job =>
        {
            job.IsIndeterminate = false;
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                job.Progress = p.Total <= 0 ? 100 : Math.Min(100, p.Done * 100.0 / p.Total);
                job.Status = $"{p.Done} of {p.Total}";
            });

            var changed = await _autoAdjust
                .RunAsync(includeManual: false, progress, job.Cancellation.Token)
                .ConfigureAwait(false);

            JobQueue.PostUi(() =>
            {
                if (changed > 0)
                {
                    StatusMessage = $"Auto-adjusted {changed} new photo{(changed == 1 ? "" : "s")}.";
                }

                foreach (var item in Photos)
                {
                    item.InvalidateThumbnail();
                    item.Refresh();
                }
            });
        }).ConfigureAwait(true);
    }

    private void JumpToFirstMonthWithPhotos()
    {
        var target = Chapters.FirstOrDefault(c => c.PhotoCount > 0);
        if (target is not null && SelectedChapter?.PhotoCount is null or 0)
        {
            SelectedChapter = target;
        }
        else if (SelectedChapter is not null)
        {
            LoadChapter(SelectedChapter.Month);
        }
    }

    // ================================================================= auto-adjust (R6, R11)

    /// <summary>
    /// Corrects every photo the user has not edited by hand, and re-derives the ones auto-adjust
    /// wrote before, so changing the book's look settings and running again brings the whole book up
    /// to date.
    /// <para>
    /// Follows doc 09 §3.8's discipline for engine batch commands: the counts are put in front of the
    /// user before anything runs, hand-edited photos are opted in explicitly rather than by default,
    /// progress is on the job queue, cancelling changes nothing, and the whole run is one composite
    /// undo entry.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task AutoAdjustAllAsync()
    {
        if (_autoAdjust is null || _session.Book is null)
        {
            return;
        }

        var plan = _autoAdjust.Describe();
        if (plan.Scope(includeManual: false) == 0 && plan.Manual == 0)
        {
            StatusMessage = "There are no photos to adjust yet.";
            return;
        }

        var choice = AutoAdjustPrompt.Ask(plan);
        if (choice is not { } includeManual)
        {
            return;
        }

        var total = plan.Scope(includeManual);
        if (total == 0)
        {
            StatusMessage = "Every photo has been edited by hand; nothing was changed.";
            return;
        }

        IsBusy = true;
        var changed = 0;

        await _jobs.RunAsync("Auto-adjusting photos", async job =>
        {
            job.IsIndeterminate = false;
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                job.Progress = p.Total <= 0 ? 100 : Math.Min(100, p.Done * 100.0 / p.Total);
                job.Status = $"{p.Done} of {p.Total}";
            });

            changed = await _autoAdjust
                .RunAsync(includeManual, progress, job.Cancellation.Token)
                .ConfigureAwait(false);

            JobQueue.PostUi(() =>
            {
                StatusMessage = changed == 0
                    ? "Auto-adjust found nothing to change."
                    : $"Auto-adjusted {changed} photo{(changed == 1 ? "" : "s")}" +
                      (includeManual || plan.Manual == 0
                          ? "."
                          : $", leaving {plan.Manual} you edited by hand alone.");

                foreach (var item in Photos)
                {
                    item.InvalidateThumbnail();
                    item.Refresh();
                }
            });
        }).ConfigureAwait(true);

        IsBusy = false;
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        var pending = _session.Catalog.Photos.Where(p => !p.Excluded && p.Quality is null).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        IsBusy = true;
        await _jobs.RunAsync("Analyzing photos", async job =>
        {
            job.IsIndeterminate = false;
            var done = 0;
            var progress = new Progress<Analysis.Running.AnalysisProgress>(_ =>
            {
                done++;
                job.Progress = Math.Min(100, done * 100.0 / pending.Count);
                job.Status = $"{done} of {pending.Count}";
            });

            await _session.AnalyzeAsync(pending, progress, job.Cancellation.Token).ConfigureAwait(false);

            JobQueue.PostUi(() =>
            {
                StatusMessage = $"Analyzed {pending.Count} photo{(pending.Count == 1 ? "" : "s")}.";
                foreach (var item in Photos)
                {
                    item.Refresh();
                }
            });
        }).ConfigureAwait(true);

        IsBusy = false;
    }

    /// <summary>True once this book is bound to a OneDrive album or folder, enabling "Sync now".</summary>
    public bool HasOneDriveSource => _session.OneDriveSource is not null;

    /// <summary>What this book syncs from, for the button's tooltip.</summary>
    public string OneDriveSourceLabel => _session.OneDriveSource?.Path ?? string.Empty;

    /// <summary>
    /// Signs in through the WAM broker, lets the user pick an album or folder, then downloads it
    /// into the project exactly as a folder import would. The choice is remembered on the book so
    /// later syncs skip the picker. Before an app registration exists this surfaces the actionable
    /// setup message rather than an exception.
    /// </summary>
    [RelayCommand]
    private Task SyncOneDriveAsync() => ConnectOneDriveAsync(reuseStoredSource: false);

    /// <summary>Re-syncs the remembered album or folder, picking up anything added since.</summary>
    [RelayCommand]
    private Task ResyncOneDriveAsync() => ConnectOneDriveAsync(reuseStoredSource: true);

    private async Task ConnectOneDriveAsync(bool reuseStoredSource)
    {
        Ingestion.OneDrive.OneDriveClient? client = null;
        try
        {
            client = Ingestion.OneDrive.OneDriveClient.CreateFromConfiguration(
                configurationFilePath: null, parentWindow: WindowHandles.MainHandle);

            // Sign in first: the picker cannot list anything without a token, and the WAM prompt
            // must not appear from underneath a modal dialog.
            StatusMessage = "Signing in to OneDrive…";
            await client.Authenticator.GetAccessTokenAsync().ConfigureAwait(true);

            var source = reuseStoredSource ? _session.OneDriveSource : null;
            int? expectedItems;

            if (source is null)
            {
                var picker = new OneDrivePickerViewModel(client, client.Authenticator.SignedInAccount);
                var window = new Views.OneDrivePickerWindow(picker)
                {
                    Owner = System.Windows.Application.Current?.MainWindow,
                };

                if (window.ShowDialog() != true || picker.Result is null)
                {
                    StatusMessage = "OneDrive sync cancelled.";
                    return;
                }

                source = picker.Result;
                expectedItems = picker.ResultItemCount;
            }
            else
            {
                // "Sync now" skipped the picker, so re-read the album's size for the progress bar.
                expectedItems = await CountItemsAsync(client, source).ConfigureAwait(true);
            }

            await ImportFromOneDriveAsync(client, source, expectedItems).ConfigureAwait(true);
        }
        catch (Ingestion.OneDrive.OneDriveNotConfiguredException ex)
        {
            ErrorRaised?.Invoke(ex.Message);
        }
        catch (Ingestion.OneDrive.OneDriveSignInException ex)
        {
            ErrorRaised?.Invoke(ex.Message);
        }
        finally
        {
            client?.Dispose();
        }
    }

    /// <summary>
    /// How many items the stored album holds, for the progress bar. One cheap Graph call; a folder
    /// source has no equivalent, and a failure here must never stop the sync.
    /// </summary>
    private static async Task<int?> CountItemsAsync(
        Ingestion.OneDrive.IOneDriveClient client, BookSource source)
    {
        if (source.Kind != BookSourceKind.OneDriveAlbum || source.Id is null)
        {
            return null;
        }

        try
        {
            var albums = await client.ListAlbumsAsync().ConfigureAwait(true);
            return albums.FirstOrDefault(a => a.Id == source.Id)?.ItemCount;
        }
        catch
        {
            return null;
        }
    }

    private async Task ImportFromOneDriveAsync(
        Ingestion.OneDrive.IOneDriveClient client, BookSource source, int? expectedItems)
    {
        IsBusy = true;
        var photosBefore = _session.PhotoCount;
        try
        {
            await _jobs.RunAsync($"Syncing “{source.Path}”", async job =>
            {
                var progress = new Progress<Ingestion.PhotoImportProgress>(p =>
                    ReportImportProgress(job, p, expectedItems));

                var report = await _session
                    .ImportOneDriveAsync(client, source, progress, job.Cancellation.Token)
                    .ConfigureAwait(false);

                var added = report.Entries.Count(e => e.Outcome == Ingestion.PhotoImportOutcome.Added);
                var skipped = report.Entries.Count - added;

                JobQueue.PostUi(() =>
                {
                    StatusMessage = added == 0
                        ? $"“{source.Path}” is already up to date ({skipped} item(s) skipped)."
                        : $"Downloaded {added} photo{(added == 1 ? "" : "s")} from “{source.Path}”" +
                          (skipped > 0 ? $"; skipped {skipped} (already imported, or not a photo)." : ".");

                    // A book covers one year, so photos outside it vanish into the tray unless we say so.
                    if (_session.ReconcileYearAfterImport(photosBefore) is { } note)
                    {
                        ErrorRaised?.Invoke(note);
                    }

                    OnPropertyChanged(nameof(HasOneDriveSource));
                    OnPropertyChanged(nameof(OneDriveSourceLabel));
                    RefreshChapters();
                    JumpToFirstMonthWithPhotos();
                });
            }).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }

        await AnalyzeAsync().ConfigureAwait(true);
        await SaveAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ImportJournalAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a Word journal",
            Filter = "Word documents (*.docx)|*.docx",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await _jobs.RunAsync("Reading journal", async job =>
        {
            var report = await _session
                .ImportJournalAsync(dialog.FileName, job.Cancellation.Token)
                .ConfigureAwait(false);

            JobQueue.PostUi(() =>
                StatusMessage = $"Journal: {report.Matched} entries dated, " +
                                $"{report.Unmatched} needing a date.");
        }).ConfigureAwait(true);

        await SaveAsync().ConfigureAwait(true);

        // Doc 11: the report opens itself when the import produced anything non-matched, because the
        // alternative is journal text silently missing from the book. A clean import says nothing.
        if (System.Windows.Application.Current?.MainWindow is { } owner)
        {
            var shown = Views.Journal.JournalReviewWindow
                .ShowAfterImport(owner, JournalReview, SelectedChapter?.Month ?? 1);
            if (shown is null)
            {
                StatusMessage += " Every entry found a date.";
            }
        }

        // Importing a journal does not re-flow pages that already exist, so text can be perfectly
        // dated and still be printed nowhere. The day map flags it, but only for someone who opens
        // it; this is too important for the status strip, which trims.
        if (JournalReview.HomelessDayCount is > 0 and var homeless)
        {
            NoticeRaised?.Invoke(
                $"{homeless} day{(homeless == 1 ? "" : "s")} of journal text " +
                $"{(homeless == 1 ? "is" : "are")} dated but on no page.",
                "Importing a journal does not rebuild pages that already exist, so this month's " +
                "layout does not carry the new text yet. Run “Lay out this month” — or " +
                "“Auto-layout rest of chapter” to keep the pages you have pinned — and the engine " +
                "will place each day's text with its photos.");
        }

        PageEditor.Refresh();
        RaiseJournalBadges();
    }

    /// <summary>Lays out the selected month from scratch (R7).</summary>
    [RelayCommand]
    private async Task LayoutChapterAsync()
    {
        if (SelectedChapter is null)
        {
            return;
        }

        var month = SelectedChapter.Month;
        IsBusy = true;

        await _jobs.RunAsync($"Laying out {SelectedChapter.Name}", job => Task.Run(() =>
        {
            var result = _session.Layout(month);
            JobQueue.PostUi(() =>
            {
                // One undo entry for the whole run, as doc 09 §4 requires of an engine command.
                var chapter = _session.Chapters.First(c => c.Month == month);
                var before = ChapterPagesSnapshot.Capture(chapter);
                _session.ApplyLayout(month, result);
                var after = ChapterPagesSnapshot.Capture(chapter);

                Undo.Push(new ChapterPagesCommand(
                    $"Lay out {SelectedChapter?.Name ?? "month"}", chapter, before, after,
                    () => JobQueue.PostUi(() =>
                    {
                        RebuildPages(month);
                        PageEditor.Refresh();
                        SyncPageContext();
                        RefreshChapters();
                    })));

                RebuildPages(month);
                PageEditor.LoadChapter(month);
                SyncPageContext();
                SelectedPage = Pages.FirstOrDefault();
                RefreshChapters();
                IsPagesTab = true;
                StatusMessage = $"Laid out {result.Pages.Count} pages.";
            });
        })).ConfigureAwait(true);

        IsBusy = false;
        await SaveAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Counts the importable files in a folder so the bar can fill. A directory walk is far cheaper
    /// than the decode-and-copy that follows, and returning null on any failure just falls back to
    /// an indeterminate bar rather than failing the import.
    /// </summary>
    private static int? CountSupportedFiles(string folder, CancellationToken ct)
    {
        try
        {
            var count = 0;
            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (Imaging.ImageFormats.IsSupported(path))
                {
                    count++;
                }
            }

            return count;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Drives the job strip during an import. With a known total the bar fills and the status reads
    /// "12 of 47"; without one it stays indeterminate rather than inventing a percentage.
    /// <paramref name="expected"/> counts every item in the source, including videos and duplicates
    /// that get skipped, so it is matched against items *seen* rather than photos added.
    /// </summary>
    private static void ReportImportProgress(Job job, Ingestion.PhotoImportProgress p, int? expected)
    {
        if (expected is > 0)
        {
            job.IsIndeterminate = false;
            job.Progress = Math.Clamp(100.0 * p.ItemsSeen / expected.Value, 0, 100);
            job.Status = $"{p.ItemsSeen} of {expected} · {p.CurrentFileName}";
        }
        else
        {
            job.IsIndeterminate = true;
            job.Status = p.ItemsSeen > 0
                ? $"{p.ItemsSeen} seen · {p.CurrentFileName}"
                : p.CurrentFileName;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!_session.IsOpen)
        {
            return;
        }

        // No "Saved." status line: the save indicator beside this button already says so, with the
        // time, and overwriting the status would throw away whatever the last operation reported.
        await _jobs.RunAsync("Saving", _ => _session.SaveAsync()).ConfigureAwait(true);
    }

    // ---------------------------------------------------------------- photo edits

    [RelayCommand]
    private void Promote() => ShiftTier(-1);

    [RelayCommand]
    private void Demote() => ShiftTier(1);

    private void ShiftTier(int delta)
    {
        if (SelectedPhoto is not { } item)
        {
            return;
        }

        var order = new[] { Core.Model.Tier.S, Core.Model.Tier.A, Core.Model.Tier.B, Core.Model.Tier.C };
        var current = Array.IndexOf(order, item.EffectiveTier);
        var next = order[Math.Clamp(current + delta, 0, order.Length - 1)];

        SetTier(item, next, $"{(delta < 0 ? "Promote" : "Demote")} {item.FileName} to tier {next}");
        StatusMessage = $"{item.FileName} set to tier {next}.";
    }

    [RelayCommand]
    private void ResetTier()
    {
        if (SelectedPhoto is { } item)
        {
            SetTier(item, null, $"Reset tier of {item.FileName}");
            StatusMessage = $"{item.FileName} is back to its analyzed tier.";
        }
    }

    /// <summary>
    /// R26's promote/demote, as an undoable edit like everything else. The override is captured by
    /// value so <c>Ctrl+Z</c> restores "no override" rather than freezing the analyzed tier in place.
    /// </summary>
    private void SetTier(PhotoItemViewModel item, Tier? tier, string description)
    {
        var photo = item.Photo;
        var before = photo.UserTierOverride;
        if (before == tier)
        {
            return;
        }

        Undo.ExecuteValue(
            description,
            before,
            tier,
            value =>
            {
                photo.UserTierOverride = value;
                _session.MarkDirty();
                item.Refresh();
            },
            $"tier:{photo.Id}");
    }

    /// <summary>Excluding removes the photo from the book but never from disk (R17, doc 09 §3.9).</summary>
    [RelayCommand]
    private void ToggleExclude()
    {
        if (SelectedPhoto is not { } item)
        {
            return;
        }

        var photo = item.Photo;
        var excluded = !photo.Excluded;

        Undo.Execute(new CompositeCommand(
            excluded ? $"Exclude {item.FileName}" : $"Restore {item.FileName}",
            BinPlacementCommands.SetExcluded(_session, photo, excluded, "Exclude"),
            new EditCommand(
                "Refresh",
                () => AfterExcludeChanged(item),
                () => AfterExcludeChanged(item))));

        StatusMessage = photo.Excluded
            ? $"{item.FileName} excluded from the book. The original file is untouched."
            : $"{item.FileName} is back in the book.";
    }

    private void AfterExcludeChanged(PhotoItemViewModel item)
    {
        item.Refresh();
        RefreshChapters();
        Bins.Refresh();
    }
}
