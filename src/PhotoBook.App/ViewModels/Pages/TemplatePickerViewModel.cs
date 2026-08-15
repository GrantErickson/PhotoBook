using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoBook.App.Services;
using PhotoBook.Core.Model;
using PhotoBook.Core.Templates;
using PhotoBook.Engine;

namespace PhotoBook.App.ViewModels.Pages;

/// <summary>One cell of the template gallery: a library template rendered with this page's photos.</summary>
public sealed partial class TemplateCandidateViewModel : ObservableObject
{
    /// <param name="template">The library template this cell offers.</param>
    /// <param name="pagePhotoCount">How many photos the page currently holds, for the delta note.</param>
    public TemplateCandidateViewModel(Template template, int pagePhotoCount)
    {
        ArgumentNullException.ThrowIfNull(template);
        Template = template;
        PagePhotoCount = pagePhotoCount;
    }

    /// <summary>The library template.</summary>
    public Template Template { get; }

    /// <summary>Photos on the page this candidate is being offered for.</summary>
    public int PagePhotoCount { get; }

    /// <summary>Its display name.</summary>
    public string Name => Template.Name;

    /// <summary>Its id, shown small for recognizability.</summary>
    public string Id => Template.Id ?? string.Empty;

    /// <summary>How many photos it holds (R20: 1..8).</summary>
    public int PhotoCount => Template.PhotoCount;

    /// <summary>"6 photos" / "1 photo".</summary>
    public string PhotoCountLabel => PhotoCount == 1 ? "1 photo" : $"{PhotoCount} photos";

    /// <summary>Human name of the template kind, for the cell's chip.</summary>
    public string KindLabel => Template.Kind switch
    {
        TemplateKind.MonthTitle => "Month title",
        TemplateKind.FullBleed => "Full bleed",
        TemplateKind.MultiDay => "Multi-day",
        TemplateKind.SpreadPair => "Spread pair",
        _ => "Standard",
    };

    /// <summary>True when the template offers somewhere for the day's journal text to go.</summary>
    public bool HasJournalSlot => Template.HasJournalSlot;

    /// <summary>The consequence of choosing this template, stated before the click (doc 09 §3.4).</summary>
    public string ConsequenceLabel => PhotoCount < PagePhotoCount
        ? $"{PagePhotoCount - PhotoCount} to Unplaced"
        : PhotoCount > PagePhotoCount
            ? $"{PhotoCount - PagePhotoCount} empty slot{(PhotoCount - PagePhotoCount == 1 ? "" : "s")}"
            : "Same photo count";

    /// <summary>True when choosing this template would displace photos into the Unplaced bin (R10).</summary>
    public bool DisplacesPhotos => PhotoCount < PagePhotoCount;

    /// <summary>True when choosing this template would leave amber empty slots (R14).</summary>
    public bool AddsEmptySlots => PhotoCount > PagePhotoCount;

    /// <summary>The live preview: this candidate rendered with the page's actual photos.</summary>
    [ObservableProperty]
    private BitmapSource? _preview;

    /// <summary>True until the preview arrives; the cell shows a wireframe placeholder meanwhile.</summary>
    [ObservableProperty]
    private bool _isRendering = true;

    /// <summary>True for the template the page is already using.</summary>
    [ObservableProperty]
    private bool _isCurrent;
}

/// <summary>
/// The template gallery of doc 09 §3.4 (R10, R12): every candidate layout for the current page,
/// each drawn as a live preview with this page's own photos, assigned by the engine's Hungarian
/// slot assignment and smart-cropped — so what the cell shows is what applying it produces.
/// <para>
/// Applying is one undo entry (a composite): photos that no longer fit go to the <b>Unplaced</b>
/// bin, new slots are left amber and can be filled from the Unplaced or Upcoming bin (R12), the
/// page becomes <b>Pinned</b>, and a detached page's hand-built geometry is only discarded after an
/// explicit confirmation.
/// </para>
/// </summary>
public sealed partial class TemplatePickerViewModel : ObservableObject, IDisposable
{
    private const double PreviewWidthPx = 232;

    private readonly ProjectSession _session;
    private readonly UndoStack _undo;
    private readonly BinsViewModel _bins;
    private readonly EditorSettingsService _settings;

    private CancellationTokenSource _previewWork = new();
    private Chapter? _chapter;
    private bool _disposed;

    /// <param name="session">The single writer for the open project.</param>
    /// <param name="undo">The editor's undo history.</param>
    /// <param name="bins">The bins — overflow goes there, and new slots are filled from there.</param>
    /// <param name="settings">Per-user preferences; the gallery's All/Suggested filter persists.</param>
    public TemplatePickerViewModel(
        ProjectSession session,
        UndoStack undo,
        BinsViewModel bins,
        EditorSettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(undo);
        ArgumentNullException.ThrowIfNull(bins);
        ArgumentNullException.ThrowIfNull(settings);

        _session = session;
        _undo = undo;
        _bins = bins;
        _settings = settings;

        KindFilters =
        [
            new TemplateKindOption("All kinds", null),
            new TemplateKindOption("Standard", TemplateKind.Standard),
            new TemplateKindOption("Full bleed", TemplateKind.FullBleed),
            new TemplateKindOption("Month title", TemplateKind.MonthTitle),
            new TemplateKindOption("Multi-day", TemplateKind.MultiDay),
        ];

        _kindFilter = KindFilters[0];
    }

    /// <summary>One entry of the kind filter.</summary>
    /// <param name="Label">What the user reads.</param>
    /// <param name="Kind">The kind to keep, or null for every kind.</param>
    public sealed record TemplateKindOption(string Label, TemplateKind? Kind)
    {
        /// <summary>The label — the combo box's closed state shows this, not the type name.</summary>
        public override string ToString() => Label;
    }

    // ---------------------------------------------------------------- context

    /// <summary>The page the gallery is offering templates for.</summary>
    public Page? Page { get; private set; }

    /// <summary>That page's 1-based number in the chapter.</summary>
    public int PageNumber { get; private set; }

    /// <summary>The candidates, filtered and ordered.</summary>
    public ObservableCollection<TemplateCandidateViewModel> Candidates { get; } = [];

    /// <summary>The kind filter's options.</summary>
    public IReadOnlyList<TemplateKindOption> KindFilters { get; }

    /// <summary>True when the whole library is listed rather than the suggestions; persisted.</summary>
    public bool ShowAll
    {
        get => _settings.Settings.TemplateGalleryShowAll;
        set
        {
            if (_settings.Settings.TemplateGalleryShowAll == value)
            {
                return;
            }

            _settings.Settings.TemplateGalleryShowAll = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowSuggested));
            Rebuild();
        }
    }

    /// <summary>The inverse of <see cref="ShowAll"/>, for the segmented control's other half.</summary>
    public bool ShowSuggested
    {
        get => !ShowAll;
        set
        {
            if (value)
            {
                ShowAll = false;
            }
        }
    }

    /// <summary>The kind filter.</summary>
    [ObservableProperty]
    private TemplateKindOption _kindFilter;

    /// <summary>A one-line note under the header: what just happened, or what will.</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>
    /// Set when the user picked a template for a <b>detached</b> page: the gallery shows an inline
    /// confirmation rather than silently discarding hand-built geometry (doc 09 §3.4).
    /// </summary>
    [ObservableProperty]
    private TemplateCandidateViewModel? _pendingDetachedConfirm;

    /// <summary>How many slots were left empty by the last apply — drives the fill offer (R12).</summary>
    [ObservableProperty]
    private int _emptySlotCount;

    /// <summary>True while the "fill the new slots" offer is showing.</summary>
    public bool HasFillOffer => EmptySlotCount > 0;

    /// <summary>The offer's wording.</summary>
    public string FillOfferLabel => EmptySlotCount == 1
        ? "1 slot is empty — fill it from a bin?"
        : $"{EmptySlotCount} slots are empty — fill them from a bin?";

    /// <summary>Photos available in the Unplaced bin, for the offer's button.</summary>
    public int UnplacedAvailable => _bins.UnplacedCount;

    /// <summary>Photos available in the Upcoming bin, for the offer's button.</summary>
    public int UpcomingAvailable => _bins.UpcomingCount;

    /// <summary>Raised when the page's content changed and the canvas should re-render.</summary>
    public event Action? PageInvalidated;

    /// <summary>Raised when the gallery has done its job and the popover can close.</summary>
    public event Action? CloseRequested;

    /// <summary>
    /// Points the gallery at a page and rebuilds the candidate list and its previews.
    /// </summary>
    /// <param name="chapter">The chapter the page belongs to (the renderer needs it).</param>
    /// <param name="page">The page to offer templates for.</param>
    /// <param name="pageNumber">Its 1-based number in the chapter.</param>
    public void SetContext(Chapter? chapter, Page? page, int pageNumber)
    {
        _chapter = chapter;
        Page = page;
        PageNumber = pageNumber;

        PendingDetachedConfirm = null;
        EmptySlotCount = 0;
        Status = string.Empty;

        OnPropertyChanged(nameof(Page));
        OnPropertyChanged(nameof(PageNumber));
        OnPropertyChanged(nameof(HeaderDetail));
        OnPropertyChanged(nameof(IsDetachedPage));
        Rebuild();
    }

    /// <summary>"Page 7 · 4 photos · Four up with journal" — the gallery's subtitle.</summary>
    public string HeaderDetail
    {
        get
        {
            if (Page is null)
            {
                return "No page selected";
            }

            var name = Page.DetachedTemplate?.Name
                       ?? (Page.TemplateRef is { } id ? _session.FindTemplate(id)?.Name ?? id : "—");
            var count = Page.Placements.Count;
            return $"Page {PageNumber} · {count} photo{(count == 1 ? "" : "s")} · {name}";
        }
    }

    /// <summary>True when the page owns a hand-built inline layout (R15) that applying would discard.</summary>
    public bool IsDetachedPage => Page?.IsDetached == true;

    partial void OnKindFilterChanged(TemplateKindOption value) => Rebuild();

    partial void OnEmptySlotCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasFillOffer));
        OnPropertyChanged(nameof(FillOfferLabel));
        OnPropertyChanged(nameof(UnplacedAvailable));
        OnPropertyChanged(nameof(UpcomingAvailable));
    }

    // ---------------------------------------------------------------- candidates

    private void Rebuild()
    {
        CancelPreviews();
        Candidates.Clear();

        var page = Page;
        var book = _session.Book;
        if (page is null || book is null)
        {
            return;
        }

        var photoCount = page.Placements.Count;
        var currentKind = page.ResolveTemplate(_session.FindTemplate)?.Kind ?? TemplateKind.Standard;
        var currentId = page.TemplateRef ?? page.DetachedTemplate?.BasedOn;

        var candidates = TemplateLibrary.Default.Templates
            .Where(t => string.Equals(t.PageSize, book.PageSize, StringComparison.Ordinal))
            .Where(t => t.Kind != TemplateKind.SpreadPair)      // applied from Spread view only (§3.1)
            .Where(t => KindFilter.Kind is null || t.Kind == KindFilter.Kind)
            .Where(t => ShowAll || IsSuggested(t, photoCount, currentKind))
            .OrderBy(t => Math.Abs(t.PhotoCount - photoCount))
            .ThenBy(t => t.PhotoCount)
            .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var template in candidates)
        {
            Candidates.Add(new TemplateCandidateViewModel(template, photoCount)
            {
                IsCurrent = string.Equals(template.Id, currentId, StringComparison.Ordinal),
            });
        }

        StartPreviews();
    }

    /// <summary>
    /// The default filter of doc 09 §3.4: the page's photo count ± 2, in the page's own kind (plus
    /// plain standard layouts, which always make sense). <i>All</i> lifts it.
    /// </summary>
    private static bool IsSuggested(Template template, int photoCount, TemplateKind currentKind) =>
        Math.Abs(template.PhotoCount - photoCount) <= 2 &&
        (template.Kind == currentKind || template.Kind == TemplateKind.Standard);

    // ---------------------------------------------------------------- previews

    private void CancelPreviews()
    {
        _previewWork.Cancel();
        _previewWork.Dispose();
        _previewWork = new CancellationTokenSource();
    }

    /// <summary>
    /// Streams the live previews in on a background thread, newest context wins. Each one renders
    /// the candidate through the same renderer as the page canvas and the PDF, so the cell is not an
    /// approximation of the result — it is the result (doc 09 §6).
    /// </summary>
    private void StartPreviews()
    {
        var chapter = _chapter;
        var page = Page;
        if (chapter is null || page is null || Candidates.Count == 0)
        {
            return;
        }

        var token = _previewWork.Token;
        var pending = Candidates.ToList();
        var number = PageNumber;

        var (trimW, trimH) = BinPlacementCommands.TrimSizeOf(_session.Book);
        var width = (int)PreviewWidthPx;
        var height = Math.Max(1, (int)Math.Round(PreviewWidthPx * trimH / trimW));

        _ = Task.Run(
            () =>
            {
                foreach (var candidate in pending)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    BitmapSource? bitmap = null;
                    try
                    {
                        var probe = BuildProbePage(page, number, candidate.Template);
                        var preview = _session.RenderPage(chapter, probe, width, height, showFlags: true);
                        bitmap = PixelBridge.ToBitmap(preview.Image);
                    }
                    catch
                    {
                        // A candidate that cannot render keeps its wireframe placeholder rather than
                        // taking the gallery down.
                    }

                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    JobQueue.PostUi(() =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        candidate.Preview = bitmap;
                        candidate.IsRendering = false;
                    });
                }
            },
            token);
    }

    /// <summary>A throwaway page used only to draw a preview; the model page is never touched.</summary>
    private Page BuildProbePage(Page page, int pageNumber, Template template)
    {
        var plan = Plan(page, pageNumber, template);
        return new Page
        {
            Id = page.Id,
            TemplateRef = template.Id,
            Mirrored = plan.Mirrored,
            Pinned = page.Pinned,
            StyleOverride = page.StyleOverride,
            Placements = [.. plan.Placements],
            JournalAssignments = [.. page.JournalAssignments],
        };
    }

    // ---------------------------------------------------------------- planning

    /// <summary>What applying a template would produce.</summary>
    /// <param name="Mirrored">Whether the template is mirrored for this page's side.</param>
    /// <param name="Placements">The new placements, each with a fresh smart crop.</param>
    /// <param name="Overflow">Photos with no slot left; they go to the Unplaced bin (R10).</param>
    /// <param name="EmptySlotIds">Slots left empty, flagged amber (R14).</param>
    private sealed record TemplatePlan(
        bool Mirrored,
        IReadOnlyList<Placement> Placements,
        IReadOnlyList<Photo> Overflow,
        IReadOnlyList<string> EmptySlotIds);

    private TemplatePlan Plan(Page page, int pageNumber, Template template)
    {
        var side = BinPlacementCommands.SideOf(pageNumber);
        var mirrored = template.Mirrorable && side == PageSide.Left;
        var oriented = mirrored ? template.Mirrored() : template;
        var slots = oriented.Slots.ToList();

        // Chronological order is the engine's timeIndex (doc 08 §7), so the photos arrive sorted.
        var photos = page.Placements
            .Select(p => _session.Catalog.Find(p.PhotoId))
            .OfType<Photo>()
            .Where(p => !p.Excluded)
            .OrderBy(p => p.TakenAt)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToList();

        var (pairs, overflow, empty) = Match(photos, slots);

        var placements = new List<Placement>(pairs.Count);
        foreach (var (photo, slot) in pairs)
        {
            placements.Add(new Placement
            {
                SlotId = slot.Id,
                PhotoId = photo.Id,
                Crop = BinPlacementCommands.SmartCropFor(_session, pageNumber, photo, slot),
            });
        }

        // Keep the stored order matching the template's reading order, so the JSON reads naturally.
        var order = slots.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        placements.Sort((a, b) => order.GetValueOrDefault(a.SlotId).CompareTo(order.GetValueOrDefault(b.SlotId)));

        return new TemplatePlan(mirrored, placements, overflow, empty);
    }

    /// <summary>
    /// The lowest-cost photo → slot matching (doc 08 §7), padded to a square so it also answers the
    /// rectangular cases doc 09 §3.4 cares about: extra photos overflow to the Unplaced bin, extra
    /// slots stay empty and amber.
    /// </summary>
    private static (List<(Photo Photo, ImageSlot Slot)> Pairs, List<Photo> Overflow, List<string> Empty) Match(
        IReadOnlyList<Photo> photos, IReadOnlyList<ImageSlot> slots)
    {
        var pairs = new List<(Photo, ImageSlot)>();
        var overflow = new List<Photo>();
        var empty = new List<string>();

        if (photos.Count == 0)
        {
            empty.AddRange(slots.Select(s => s.Id));
            return (pairs, overflow, empty);
        }

        if (slots.Count == 0)
        {
            overflow.AddRange(photos);
            return (pairs, overflow, empty);
        }

        var weights = LayoutWeights.Default;
        var reading = AssignmentCost.ReadingOrder(slots, weights);
        var n = Math.Max(photos.Count, slots.Count);

        // Dummy rows/columns cost far more than any real pair, so the solver spends its real
        // assignments on the best fits and dumps the remainder into the padding.
        const double DummyCost = 1000.0;
        var matrix = new double[n, n];
        for (var p = 0; p < n; p++)
        {
            for (var s = 0; s < n; s++)
            {
                matrix[p, s] = p < photos.Count && s < slots.Count
                    ? AssignmentCost.Cost(photos[p], slots[s], p, reading[s], Math.Max(photos.Count, slots.Count), weights)
                    : DummyCost;
            }
        }

        var solution = Hungarian.Solve(matrix);
        var filled = new bool[slots.Count];

        for (var p = 0; p < photos.Count; p++)
        {
            var s = solution[p];
            if (s >= 0 && s < slots.Count)
            {
                pairs.Add((photos[p], slots[s]));
                filled[s] = true;
            }
            else
            {
                overflow.Add(photos[p]);
            }
        }

        for (var s = 0; s < slots.Count; s++)
        {
            if (!filled[s])
            {
                empty.Add(slots[s].Id);
            }
        }

        return (pairs, overflow, empty);
    }

    // ---------------------------------------------------------------- apply

    /// <summary>
    /// Applies a candidate to the current page (doc 09 §3.4). One composite undo entry covers the
    /// template swap, every re-crop, the overflow into the Unplaced bin and the pin.
    /// </summary>
    /// <param name="candidate">The gallery cell that was clicked.</param>
    [RelayCommand]
    public void Apply(TemplateCandidateViewModel? candidate)
    {
        if (candidate is null || Page is not { } page || _session.Book is null)
        {
            return;
        }

        // A detached page's geometry is the user's own work: never replace it without asking.
        if (page.IsDetached && !ReferenceEquals(PendingDetachedConfirm, candidate))
        {
            PendingDetachedConfirm = candidate;
            return;
        }

        PendingDetachedConfirm = null;

        var template = candidate.Template;
        if (template.Id is not { } templateId)
        {
            return;
        }

        var plan = Plan(page, PageNumber, template);

        using (_undo.BeginBatch($"Change template to {template.Name}"))
        {
            _undo.Execute(BinPlacementCommands.ApplyTemplate(
                _session, page, templateId, plan.Mirrored, plan.Placements,
                $"Apply “{template.Name}” to page {PageNumber}"));

            if (BinPlacementCommands.SetPinned(_session, page, true, $"Pin page {PageNumber}") is { } pin)
            {
                _undo.Execute(pin);
            }
        }

        Status = plan.Overflow.Count switch
        {
            0 => $"Page {PageNumber} now uses “{template.Name}”.",
            1 => $"“{template.Name}” applied. 1 photo moved to Unplaced.",
            _ => $"“{template.Name}” applied. {plan.Overflow.Count} photos moved to Unplaced.",
        };

        EmptySlotCount = plan.EmptySlotIds.Count;
        _bins.Refresh();
        foreach (var candidateViewModel in Candidates)
        {
            candidateViewModel.IsCurrent = ReferenceEquals(candidateViewModel, candidate);
        }

        OnPropertyChanged(nameof(HeaderDetail));
        OnPropertyChanged(nameof(IsDetachedPage));
        PageInvalidated?.Invoke();

        // Nothing left to decide when the photo count matched exactly: get out of the user's way.
        if (EmptySlotCount == 0)
        {
            CloseRequested?.Invoke();
        }
    }

    /// <summary>Dismisses the detached-page confirmation without changing anything.</summary>
    [RelayCommand]
    public void CancelDetachedConfirm() => PendingDetachedConfirm = null;

    /// <summary>Closes the gallery popover, leaving the page as it is.</summary>
    [RelayCommand]
    public void Close()
    {
        PendingDetachedConfirm = null;
        EmptySlotCount = 0;
        CloseRequested?.Invoke();
    }

    /// <summary>Fills every empty slot on the page from the Unplaced bin (R12).</summary>
    [RelayCommand]
    public void FillFromUnplaced() => Fill(BinTab.Unplaced);

    /// <summary>
    /// Fills every empty slot from the Upcoming bin (R12, R13). Each photo pulled forward vacates
    /// its slot on the later page, leaving an amber hole there; that page stays unpinned so
    /// <i>Auto-layout rest of chapter</i> can heal it (doc 09 §3.5).
    /// </summary>
    [RelayCommand]
    public void FillFromUpcoming() => Fill(BinTab.Upcoming);

    /// <summary>Dismisses the fill offer, leaving the slots amber (R14).</summary>
    [RelayCommand]
    public void DismissFillOffer()
    {
        EmptySlotCount = 0;
        CloseRequested?.Invoke();
    }

    /// <summary>
    /// Fills one named empty slot from a bin — the Slot context menu's <i>Fill from bin…</i>
    /// (doc 09 §3.4, §3.6), which offers the best-fit photo by aspect and tier.
    /// </summary>
    /// <param name="slotId">The empty slot to fill.</param>
    /// <param name="tab">Which bin to draw from.</param>
    /// <returns>True when a photo was placed.</returns>
    public bool FillSlot(string slotId, BinTab tab) => Fill(tab, [slotId]) > 0;

    private void Fill(BinTab tab)
    {
        var empty = EmptySlotIds();
        var placed = Fill(tab, empty);

        EmptySlotCount = Math.Max(0, EmptySlotCount - placed);
        if (EmptySlotCount == 0)
        {
            CloseRequested?.Invoke();
        }
    }

    private int Fill(BinTab tab, IReadOnlyList<string> slotIds)
    {
        if (Page is not { } page || slotIds.Count == 0)
        {
            return 0;
        }

        var template = page.ResolveTemplate(_session.FindTemplate);
        if (template is null)
        {
            return 0;
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        var work = new List<(string SlotId, BinItemViewModel Item)>();

        foreach (var slotId in slotIds)
        {
            var slot = template.FindSlot(slotId);
            if (slot is null)
            {
                continue;
            }

            var pick = _bins.BestFitFor(slot, tab).FirstOrDefault(i => used.Add(i.Photo.Id));
            if (pick is not null)
            {
                work.Add((slotId, pick));
            }
        }

        if (work.Count == 0)
        {
            Status = tab == BinTab.Upcoming
                ? "The Upcoming bin has nothing to pull forward."
                : "The Unplaced bin is empty.";
            return 0;
        }

        var label = tab == BinTab.Upcoming ? "Upcoming" : "Unplaced";
        using (_undo.BeginBatch($"Fill {work.Count} slot{(work.Count == 1 ? "" : "s")} from {label}"))
        {
            foreach (var (slotId, item) in work)
            {
                // Pulling from Upcoming leaves an amber hole on the later page, and leaves that page
                // unpinned on purpose (doc 09 §3.5 Decision).
                if (tab == BinTab.Upcoming &&
                    _bins.CreateVacateSourceCommand(item.ToDragPayload()) is { } vacate)
                {
                    _undo.Execute(vacate);
                }

                var slot = template.FindSlot(slotId);
                var crop = slot is null
                    ? CropState.Default
                    : BinPlacementCommands.SmartCropFor(_session, PageNumber, item.Photo, slot);

                _undo.Execute(BinPlacementCommands.AddPlacement(
                    _session,
                    page,
                    new Placement { SlotId = slotId, PhotoId = item.Photo.Id, Crop = crop },
                    $"Place {item.FileName} in slot {slotId}"));
            }

            if (BinPlacementCommands.SetPinned(_session, page, true, $"Pin page {PageNumber}") is { } pin)
            {
                _undo.Execute(pin);
            }
        }

        var holes = tab == BinTab.Upcoming
            ? " Their old slots on later pages are now flagged empty."
            : string.Empty;

        Status = $"Filled {work.Count} slot{(work.Count == 1 ? "" : "s")} from {label}.{holes}";
        _bins.Refresh();
        OnPropertyChanged(nameof(HeaderDetail));
        OnPropertyChanged(nameof(UnplacedAvailable));
        OnPropertyChanged(nameof(UpcomingAvailable));
        PageInvalidated?.Invoke();
        return work.Count;
    }

    /// <summary>The current page's empty slots, in the template's reading order (R14).</summary>
    public IReadOnlyList<string> EmptySlotIds()
    {
        if (Page is not { } page)
        {
            return [];
        }

        var template = page.ResolveTemplate(_session.FindTemplate);
        if (template is null)
        {
            return [];
        }

        var filled = page.Placements.Select(p => p.SlotId).ToHashSet(StringComparer.Ordinal);
        return [.. template.Slots.Select(s => s.Id).Where(id => !filled.Contains(id))];
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _previewWork.Cancel();
        _previewWork.Dispose();
    }
}
