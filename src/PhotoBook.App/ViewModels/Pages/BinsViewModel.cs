using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoBook.App.Services;
using PhotoBook.Core.Model;
using PhotoBook.Engine;

namespace PhotoBook.App.ViewModels.Pages;

/// <summary>
/// The three bins of doc 09 §3.5 plus the Outside-book tray of §2.1, in one dockable panel.
/// <list type="bullet">
/// <item><description><b>Unplaced</b> — this month's photos that are on no page (R10).</description></item>
/// <item><description><b>Upcoming</b> — photos placed on <em>later</em> pages of this chapter, so
/// they can be pulled forward (R13). Pulling one forward vacates its later slot, leaving an amber
/// hole there; the source page is deliberately <b>not</b> pinned, so
/// <i>Auto-layout rest of chapter</i> can heal it.</description></item>
/// <item><description><b>Outside year</b> — photos whose date left the book's year (R6), so nothing
/// silently disappears.</description></item>
/// </list>
/// <para>
/// The panel's dock edge and size are a per-user preference persisted by
/// <see cref="EditorSettingsService"/>, never book content (R13, doc 09 §3.5 Decision).
/// </para>
/// </summary>
public sealed partial class BinsViewModel : ObservableObject, IDisposable
{
    private readonly ProjectSession _session;
    private readonly UndoStack _undo;
    private readonly ThumbnailProvider _thumbnails;
    private readonly EditorSettingsService _settings;

    private readonly Dictionary<string, BinItemViewModel> _tiles = new(StringComparer.Ordinal);
    private CancellationTokenSource _thumbnailWork = new();
    private bool _refreshQueued;
    private bool _disposed;

    /// <param name="session">The single writer for the open project.</param>
    /// <param name="undo">The editor's undo history; every bin edit goes through it.</param>
    /// <param name="thumbnails">Shared thumbnail cache.</param>
    /// <param name="settings">Per-user preferences — dock edge, size, open tab.</param>
    public BinsViewModel(
        ProjectSession session,
        UndoStack undo,
        ThumbnailProvider thumbnails,
        EditorSettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(undo);
        ArgumentNullException.ThrowIfNull(thumbnails);
        ArgumentNullException.ThrowIfNull(settings);

        _session = session;
        _undo = undo;
        _thumbnails = thumbnails;
        _settings = settings;

        _session.Changed += QueueRefresh;
        Settings.PropertyChanged += OnSettingsChanged;
    }

    // ---------------------------------------------------------------- context

    /// <summary>The chapter month the bins are showing.</summary>
    public int Month { get; private set; }

    /// <summary>The page being edited; everything after it in the chapter feeds the Upcoming bin.</summary>
    public Page? CurrentPage { get; private set; }

    /// <summary>The current page's 1-based number in the chapter.</summary>
    public int CurrentPageNumber { get; private set; }

    /// <summary>
    /// Points the bins at a month and the page being edited, then rebuilds. Call this whenever the
    /// chapter or the selected page changes — the Upcoming bin is defined relative to that page.
    /// </summary>
    /// <param name="month">The chapter month, 1..12.</param>
    /// <param name="currentPage">The page being edited, or null when the chapter has no pages yet.</param>
    /// <param name="currentPageNumber">Its 1-based number in the chapter.</param>
    public void SetContext(int month, Page? currentPage, int currentPageNumber)
    {
        Month = month;
        CurrentPage = currentPage;
        CurrentPageNumber = currentPageNumber;
        Refresh();
    }

    // ---------------------------------------------------------------- collections

    /// <summary>This month's photos that are on no page (R10).</summary>
    public ObservableCollection<BinItemViewModel> Unplaced { get; } = [];

    /// <summary>Photos placed on pages after the current one, in page order (R13).</summary>
    public ObservableCollection<BinItemViewModel> Upcoming { get; } = [];

    /// <summary>Photos dated outside the book's year (R6).</summary>
    public ObservableCollection<BinItemViewModel> OutsideTray { get; } = [];

    /// <summary>The collection the panel is currently showing.</summary>
    public ObservableCollection<BinItemViewModel> Items => Tab switch
    {
        BinTab.Upcoming => Upcoming,
        BinTab.Outside => OutsideTray,
        _ => Unplaced,
    };

    /// <summary>Count badge for the Unplaced segment.</summary>
    public int UnplacedCount => Unplaced.Count;

    /// <summary>Count badge for the Upcoming segment.</summary>
    public int UpcomingCount => Upcoming.Count;

    /// <summary>Count badge for the Outside-year segment.</summary>
    public int OutsideCount => OutsideTray.Count;

    /// <summary>True when the visible bin has nothing in it, so the panel shows its empty state.</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>The empty-state headline for the visible bin.</summary>
    public string EmptyTitle => Tab switch
    {
        BinTab.Upcoming => "Nothing on later pages",
        BinTab.Outside => "Every photo is inside the book's year",
        _ => "Nothing unplaced",
    };

    /// <summary>The empty-state explanation for the visible bin.</summary>
    public string EmptyDetail => Tab switch
    {
        BinTab.Upcoming =>
            "Photos placed after this page appear here so you can pull them forward.",
        BinTab.Outside =>
            "A photo whose date falls outside the book's year waits here instead of vanishing. Change its date to bring it in.",
        _ =>
            "Every photo of this month is on a page. Photos displaced by a template change land here.",
    };

    /// <summary>The bin's selection; the inspector and context menu act on it.</summary>
    [ObservableProperty]
    private BinItemViewModel? _selectedItem;

    /// <summary>The last thing the panel did, shown as a one-line note under the header.</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    // ---------------------------------------------------------------- preferences

    /// <summary>The persisted per-user preferences this panel binds to.</summary>
    public EditorSettings Settings => _settings.Settings;

    /// <summary>Which bin is showing; persisted between sessions.</summary>
    public BinTab Tab
    {
        get => Settings.BinTab;
        set => Settings.BinTab = value;
    }

    /// <summary>True when the panel is docked along the bottom (the default filmstrip, R13).</summary>
    public bool IsBottomDock => Settings.BinDock == BinDock.Bottom;

    /// <summary>True when the panel is docked to the left of the canvas.</summary>
    public bool IsLeftDock => Settings.BinDock == BinDock.Left;

    /// <summary>True when the panel is docked to the right of the canvas.</summary>
    public bool IsRightDock => Settings.BinDock == BinDock.Right;

    /// <summary>True when the panel is visible at all (<c>B</c> toggles it).</summary>
    public bool IsVisible => Settings.BinVisible;

    /// <summary>Docks the panel to an edge and remembers the choice (R13).</summary>
    /// <param name="dock">The edge to dock to.</param>
    [RelayCommand]
    public void SetDock(BinDock dock) => Settings.BinDock = dock;

    /// <summary>Shows or hides the panel.</summary>
    [RelayCommand]
    public void ToggleVisible() => Settings.BinVisible = !Settings.BinVisible;

    /// <summary>Switches the visible bin.</summary>
    /// <param name="tab">The bin to show.</param>
    [RelayCommand]
    public void SelectTab(BinTab tab) => Tab = tab;

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(EditorSettings.BinDock):
                OnPropertyChanged(nameof(IsBottomDock));
                OnPropertyChanged(nameof(IsLeftDock));
                OnPropertyChanged(nameof(IsRightDock));
                break;
            case nameof(EditorSettings.BinVisible):
                OnPropertyChanged(nameof(IsVisible));
                break;
            case nameof(EditorSettings.BinTab):
                OnPropertyChanged(nameof(Tab));
                OnPropertyChanged(nameof(Items));
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(EmptyTitle));
                OnPropertyChanged(nameof(EmptyDetail));
                SelectedItem = null;
                break;
        }
    }

    // ---------------------------------------------------------------- rebuild

    private void QueueRefresh()
    {
        if (_refreshQueued || _disposed)
        {
            return;
        }

        _refreshQueued = true;
        JobQueue.PostUi(() =>
        {
            _refreshQueued = false;
            if (!_disposed)
            {
                Refresh();
            }
        });
    }

    /// <summary>
    /// Rebuilds all three bins from the model. Tiles are reused by photo id, so a refresh after an
    /// edit does not throw away thumbnails that are already decoded.
    /// </summary>
    public void Refresh()
    {
        var book = _session.Book;
        if (book is null)
        {
            Fill(Unplaced, []);
            Fill(Upcoming, []);
            Fill(OutsideTray, []);
            RaiseCounts();
            return;
        }

        var chapter = _session.Chapters.FirstOrDefault(c => c.Month == Month);
        var pages = chapter?.Pages ?? [];

        // Where every placed photo of this chapter currently sits.
        var placed = new Dictionary<string, (Page Page, int Number, string SlotId)>(StringComparer.Ordinal);
        for (var i = 0; i < pages.Count; i++)
        {
            foreach (var placement in pages[i].Placements)
            {
                placed[placement.PhotoId] = (pages[i], i + 1, placement.SlotId);
            }
        }

        var currentIndex = CurrentIndex(pages);

        var unplaced = _session.Catalog
            .InChapter(book.Year, Month)
            .Where(p => !placed.ContainsKey(p.Id))
            .Select(p => Tile(p, BinTab.Unplaced))
            .ToList();

        var upcoming = new List<BinItemViewModel>();
        for (var i = currentIndex + 1; i < pages.Count; i++)
        {
            foreach (var placement in pages[i].Placements)
            {
                var photo = _session.Catalog.Find(placement.PhotoId);
                if (photo is null || photo.Excluded)
                {
                    continue;
                }

                upcoming.Add(Tile(photo, BinTab.Upcoming, i + 1, pages[i].Id, placement.SlotId));
            }
        }

        var tray = _session.Catalog
            .OutsideBookTray(book.Year)
            .Select(p => Tile(p, BinTab.Outside))
            .ToList();

        Fill(Unplaced, unplaced);
        Fill(Upcoming, upcoming);
        Fill(OutsideTray, tray);
        RaiseCounts();

        if (SelectedItem is { } selected && !Items.Contains(selected))
        {
            SelectedItem = null;
        }

        StartThumbnails();
    }

    private int CurrentIndex(IList<Page> pages)
    {
        if (CurrentPage is null)
        {
            return CurrentPageNumber > 0 ? CurrentPageNumber - 1 : -1;
        }

        for (var i = 0; i < pages.Count; i++)
        {
            if (ReferenceEquals(pages[i], CurrentPage) ||
                (pages[i].Id.Length > 0 && string.Equals(pages[i].Id, CurrentPage.Id, StringComparison.Ordinal)))
            {
                return i;
            }
        }

        return CurrentPageNumber > 0 ? CurrentPageNumber - 1 : -1;
    }

    private BinItemViewModel Tile(
        Photo photo, BinTab bin, int? pageNumber = null, string? pageId = null, string? slotId = null)
    {
        if (!_tiles.TryGetValue(photo.Id, out var tile) || !ReferenceEquals(tile.Photo, photo))
        {
            tile = new BinItemViewModel(photo, bin, _thumbnails);
            _tiles[photo.Id] = tile;
        }

        tile.Retarget(bin, pageNumber, pageId, slotId);
        return tile;
    }

    private static void Fill(ObservableCollection<BinItemViewModel> target, List<BinItemViewModel> desired)
    {
        if (target.Count == desired.Count)
        {
            var same = true;
            for (var i = 0; i < desired.Count; i++)
            {
                if (!ReferenceEquals(target[i], desired[i]))
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                return;
            }
        }

        target.Clear();
        foreach (var item in desired)
        {
            target.Add(item);
        }
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(UnplacedCount));
        OnPropertyChanged(nameof(UpcomingCount));
        OnPropertyChanged(nameof(OutsideCount));
        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void StartThumbnails()
    {
        _thumbnailWork.Cancel();
        _thumbnailWork.Dispose();
        _thumbnailWork = new CancellationTokenSource();
        var token = _thumbnailWork.Token;

        var pending = Unplaced.Concat(Upcoming).Concat(OutsideTray).Distinct().ToList();
        _ = Task.Run(async () =>
        {
            foreach (var item in pending)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                await item.EnsureThumbnailAsync(token).ConfigureAwait(false);
            }
        }, token);
    }

    // ---------------------------------------------------------------- edits

    /// <summary>
    /// R17: removing a photo from a bin excludes it from the book. The catalog row stays as a
    /// tombstone so a re-scan or OneDrive re-sync never resurrects it, and the original file in
    /// <c>originals/</c> is untouched. Fully undoable in-session (doc 09 §3.9, §4).
    /// </summary>
    /// <param name="item">The bin tile to exclude; defaults to the selection.</param>
    [RelayCommand]
    public void Exclude(BinItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null || _session.Book is null)
        {
            return;
        }

        var name = item.FileName;
        var page = FindPage(item.PageId);

        using (_undo.BeginBatch($"Exclude {name} from the book"))
        {
            // An Upcoming photo is still on a later page: vacate that slot first so the exclusion
            // cannot leave a placement pointing at a photo the book no longer contains.
            if (page is not null && item.SlotId is { } slotId &&
                BinPlacementCommands.RemovePlacement(
                    _session, page, slotId, $"Remove {name} from page {item.PageNumber}") is { } vacate)
            {
                _undo.Execute(vacate);
            }

            _undo.Execute(BinPlacementCommands.SetExcluded(_session, item.Photo, true, $"Exclude {name}"));
        }

        Status = page is null
            ? $"{name} excluded — it stays in originals/ and will not come back on a re-scan. Ctrl+Z undoes this."
            : $"{name} excluded, leaving an empty slot on page {item.PageNumber}. The original file is untouched.";

        Refresh();
    }

    /// <summary>
    /// Unplaces a photo dropped onto the bin panel from a slot (doc 09 §3.2, last row): the slot
    /// becomes an empty amber one and the photo joins the Unplaced bin. The page is pinned, because
    /// the user just edited it.
    /// </summary>
    /// <param name="payload">The drag that landed on the panel.</param>
    /// <returns>True when something was actually unplaced.</returns>
    public bool Unplace(PhotoDragPayload? payload)
    {
        if (payload is null || !payload.IsPlaced || _session.Book is null)
        {
            return false;
        }

        var page = FindPage(payload.SourcePageId);
        if (page is null || payload.SourceSlotId is not { } slotId)
        {
            return false;
        }

        var photo = _session.Catalog.Find(payload.PhotoId);
        var name = photo?.OriginalFileName ?? "photo";
        var number = payload.SourcePageNumber;

        var remove = BinPlacementCommands.RemovePlacement(
            _session, page, slotId, $"Move {name} to the Unplaced bin");
        if (remove is null)
        {
            return false;
        }

        using (_undo.BeginBatch($"Move {name} to the Unplaced bin"))
        {
            _undo.Execute(remove);
            if (BinPlacementCommands.SetPinned(_session, page, true, "Pin page") is { } pin)
            {
                _undo.Execute(pin);
            }
        }

        Status = number is null
            ? $"{name} moved to the Unplaced bin; its slot is now flagged empty."
            : $"{name} moved to the Unplaced bin — page {number} now has an empty slot.";

        Tab = BinTab.Unplaced;
        Refresh();
        return true;
    }

    /// <summary>
    /// The command that vacates an Upcoming photo's slot on its later page, for the page-gesture
    /// code to fold into its own drop composite (doc 09 §3.5). Returns null when the payload is not
    /// an Upcoming pull. The source page is deliberately left <b>unpinned</b>.
    /// </summary>
    /// <param name="payload">The drag being dropped on the current page.</param>
    public IUndoableCommand? CreateVacateSourceCommand(PhotoDragPayload? payload)
    {
        if (payload is null || !payload.IsPlaced)
        {
            return null;
        }

        var page = FindPage(payload.SourcePageId);
        if (page is null || payload.SourceSlotId is not { } slotId)
        {
            return null;
        }

        var name = _session.Catalog.Find(payload.PhotoId)?.OriginalFileName ?? "photo";
        return BinPlacementCommands.RemovePlacement(
            _session, page, slotId, $"Vacate {name}'s slot on page {payload.SourcePageNumber}");
    }

    /// <summary>Asks the shell to navigate to an Upcoming item's page (the hover jump-to-page link, R13).</summary>
    /// <param name="item">The tile whose page to open.</param>
    [RelayCommand]
    public void JumpToPage(BinItemViewModel? item)
    {
        if ((item ?? SelectedItem)?.PageNumber is { } number)
        {
            JumpToPageRequested?.Invoke(number);
        }
    }

    /// <summary>Raised when the user asks to go to an Upcoming item's page; the argument is the page number.</summary>
    public event Action<int>? JumpToPageRequested;

    /// <summary>
    /// Raised when a bin item should open the photo inspector (date, tier, adjustments) — the shared
    /// context menu of doc 09 §3.5. The shell owns those panels, so the bins only ask.
    /// </summary>
    public event Action<Photo, string>? InspectorRequested;

    /// <summary>Opens the re-date dialog for a bin item (doc 09 §2.1).</summary>
    /// <param name="item">The tile to act on; defaults to the selection.</param>
    [RelayCommand]
    public void ChangeDate(BinItemViewModel? item) => RequestInspector(item, "date");

    /// <summary>Opens the adjustments panel for a bin item (R11, doc 09 §2.4).</summary>
    /// <param name="item">The tile to act on; defaults to the selection.</param>
    [RelayCommand]
    public void Adjust(BinItemViewModel? item) => RequestInspector(item, "adjust");

    /// <summary>Opens the tier section for a bin item (R26, doc 09 §2.3).</summary>
    /// <param name="item">The tile to act on; defaults to the selection.</param>
    [RelayCommand]
    public void SetTier(BinItemViewModel? item) => RequestInspector(item, "tier");

    private void RequestInspector(BinItemViewModel? item, string section)
    {
        if ((item ?? SelectedItem) is { } target)
        {
            InspectorRequested?.Invoke(target.Photo, section);
        }
    }

    // ---------------------------------------------------------------- suggestions

    /// <summary>
    /// The bin's photos ordered as best-fit suggestions for one empty slot — aspect first, then tier
    /// affinity, then chronology (doc 09 §3.4's "Fill from bin…" ordering).
    /// </summary>
    /// <param name="slot">The empty slot being filled.</param>
    /// <param name="tab">Which bin to draw from.</param>
    public IReadOnlyList<BinItemViewModel> BestFitFor(ImageSlot slot, BinTab tab)
    {
        ArgumentNullException.ThrowIfNull(slot);

        var source = tab switch
        {
            BinTab.Upcoming => Upcoming,
            BinTab.Outside => OutsideTray,
            _ => Unplaced,
        };

        return [.. source
            .OrderBy(i => AssignmentCost.AspectCrop(i.Photo, slot, LayoutWeights.Default))
            .ThenBy(i => AssignmentCost.TierDistance(i.Photo, slot))
            .ThenBy(i => i.Photo.TakenAt)
            .ThenBy(i => i.Photo.Id, StringComparer.Ordinal)];
    }

    private Page? FindPage(string? pageId)
    {
        if (string.IsNullOrEmpty(pageId))
        {
            return null;
        }

        return _session.Chapters
            .FirstOrDefault(c => c.Month == Month)?.Pages
            .FirstOrDefault(p => string.Equals(p.Id, pageId, StringComparison.Ordinal));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Changed -= QueueRefresh;
        Settings.PropertyChanged -= OnSettingsChanged;
        _thumbnailWork.Cancel();
        _thumbnailWork.Dispose();
    }
}
