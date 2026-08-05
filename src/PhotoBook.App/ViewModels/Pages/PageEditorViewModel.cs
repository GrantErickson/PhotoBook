using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoBook.App.Controls;
using PhotoBook.App.Services;
using PhotoBook.Core.Model;

namespace PhotoBook.App.ViewModels.Pages;

/// <summary>
/// The Pages-tab page editor (doc 09 §3.1–§3.3, §3.6): the Single|Spread view toggle, drag-drop with
/// swap semantics, crop pan/zoom on the canonical <see cref="CropState"/>, the empty-slot fill
/// affordance, viewport zoom and the page navigator.
/// <para>
/// Every model change goes through <see cref="UndoStack"/> and through the shared factories in
/// <see cref="BinPlacementCommands"/>, so a drop here and a drop from the bin panel produce the same
/// edit. A whole pan or zoom gesture folds into one undo entry; a drop is one composite entry
/// including the pin flags it flips (doc 09 §4).
/// </para>
/// <para>
/// The view owns no model logic: it forwards <see cref="PageCanvas"/> events to the gesture methods
/// here and re-renders the canvases when <see cref="PageInvalidated"/> fires.
/// </para>
/// </summary>
public sealed partial class PageEditorViewModel : ObservableObject
{
    /// <summary>Preflight's soft-image threshold (doc 12) — below this the crop readout warns.</summary>
    public const double SoftDpiThreshold = 200;

    /// <summary>Wheel and <c>+</c>/<c>-</c> zoom step (doc 09 §3.3).</summary>
    public const double ZoomStep = 0.05;

    /// <summary>Arrow-key offset nudge, in slot units (doc 09 §3.3); <c>Shift</c> multiplies by five.</summary>
    public const double NudgeStep = 0.01;

    private readonly ProjectSession _session;
    private readonly UndoStack _undo;
    private readonly ThumbnailProvider? _thumbnails;
    private readonly EditorSettingsService? _settings;

    private Chapter? _chapter;
    private IDisposable? _gesture;
    private bool _pinNoticeShown;
    private bool _writingZoom;
    private bool _restoring;

    /// <param name="session">The open project — the single writer.</param>
    /// <param name="undo">The book's undo stack.</param>
    /// <param name="thumbnails">Cached thumbnails, for the drag ghost; optional.</param>
    /// <param name="settings">Per-user editor preferences, so the guides toggle survives a restart; optional.</param>
    public PageEditorViewModel(
        ProjectSession session,
        UndoStack undo,
        ThumbnailProvider? thumbnails = null,
        EditorSettingsService? settings = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(undo);

        _session = session;
        _undo = undo;
        _thumbnails = thumbnails;
        _settings = settings;
        _undo.Changed += OnUndoStackChanged;
        _showGuides = settings?.Settings.PageGuidesVisible ?? false;

        // Guides are an editor overlay drawn by PageCanvas from this very geometry, never baked into
        // the page bitmap: what the renderer produces here is exactly what the PDF gets (ADR-0003).
        Renderer = context =>
        {
            var chapter = _chapter;
            return chapter is null
                ? null
                : _session.RenderPage(
                    chapter, context.Page, context.PixelWidth, context.PixelHeight,
                    showFlags: false, drawGuides: false);
        };
    }

    // ============================================================== events

    /// <summary>
    /// An empty amber slot was clicked or its <em>Fill from bin…</em> command chosen (R14, doc 09
    /// §3.6). The integrator opens the bin panel and sorts best-fit suggestions with the aspect and
    /// tier affinity carried here.
    /// </summary>
    public event EventHandler<FillSlotRequestedEventArgs>? FillSlotRequested;

    /// <summary>A page's content changed and its canvas must re-render.</summary>
    public event EventHandler<PageEditorPageEventArgs>? PageInvalidated;

    /// <summary>The set of pages, or which page is current, changed — refresh bins and filmstrips.</summary>
    public event EventHandler? PagesChanged;

    /// <summary>A short message worth showing in the status line or a toast.</summary>
    public event EventHandler<string>? StatusRaised;

    // ============================================================== state

    /// <summary>The renderer the canvases draw through — the same one the PDF uses (ADR-0003).</summary>
    public PageCanvasRenderer Renderer { get; }

    /// <summary>The chapter's pages, in order, for the page navigator.</summary>
    public ObservableCollection<PageNavigatorItemViewModel> Pages { get; } = [];

    /// <summary>The chapter being edited, or null before one is loaded.</summary>
    public Chapter? Chapter => _chapter;

    /// <summary>False shows one page, true the facing pair (R8, doc 09 §3.1). A spread is a view only.</summary>
    [ObservableProperty]
    private bool _isSpread;

    /// <summary>The navigator entry for the page every edit targets.</summary>
    [ObservableProperty]
    private PageNavigatorItemViewModel? _selectedPage;

    /// <summary>The selected slot on <see cref="CurrentPage"/>, or null.</summary>
    [ObservableProperty]
    private string? _selectedSlotId;

    /// <summary>True while crop mode is open on the selected slot (doc 09 §3.3).</summary>
    [ObservableProperty]
    private bool _isCropMode;

    /// <summary>Canvas viewport zoom: 1.0 is fit-to-window (doc 09 §3 "Viewport zoom").</summary>
    [ObservableProperty]
    private double _viewportZoom = 1.0;

    /// <summary>
    /// Bleed, trim, safe and gutter guides over the page (the <c>G</c> key, doc 09 §5). Off by
    /// default and remembered per user, like every other editor preference (doc 09 §3.5).
    /// </summary>
    [ObservableProperty]
    private bool _showGuides;

    partial void OnShowGuidesChanged(bool value)
    {
        if (_settings is not null)
        {
            _settings.Settings.PageGuidesVisible = value;
        }

        Refresh();
    }

    /// <summary>Turns the trim/safe/gutter guides on or off.</summary>
    [RelayCommand]
    private void ToggleGuides() => ShowGuides = !ShowGuides;

    /// <summary>The page every edit targets — in Spread view, the one the pointer last touched.</summary>
    public Page? CurrentPage => SelectedPage?.Page;

    /// <summary>The left page of the visible spread, or the single page being edited.</summary>
    public Page? LeftPage => IsSpread ? SpreadPages().Left : CurrentPage;

    /// <summary>The right page of the visible spread; null in Single view or on an odd last page.</summary>
    public Page? RightPage => IsSpread ? SpreadPages().Right : null;

    /// <summary>True when the spread's second page exists — an odd page count leaves the last one alone.</summary>
    public bool HasRightPage => RightPage is not null;

    /// <summary>"Page 7 of 12" for the navigator header.</summary>
    public string PageLabel => SelectedPage is { } page && Pages.Count > 0
        ? $"Page {page.Number} of {Pages.Count}"
        : "No pages";

    /// <summary>True when the chapter has at least one page.</summary>
    public bool HasPages => Pages.Count > 0;

    /// <summary>Viewport zoom as a percentage label, e.g. "140%".</summary>
    public string ViewportZoomLabel =>
        (ViewportZoom * 100).ToString("0", CultureInfo.CurrentCulture) + "%";

    /// <summary>True when the canvas is showing the page fitted to the window.</summary>
    public bool IsFitToWindow => Math.Abs(ViewportZoom - 1.0) < 0.001;

    // ---------------------------------------------------------- crop readout

    /// <summary>The placement crop mode is editing, or null.</summary>
    private Placement? CropPlacement => CurrentPage is { } page && SelectedSlotId is { } slot
        ? page.PlacementFor(slot)
        : null;

    /// <summary>True when a photo is selected that crop mode can edit.</summary>
    public bool HasCropTarget => CropPlacement is not null;

    /// <summary>True when the selected slot is one of the amber empty ones (R14).</summary>
    public bool IsSelectedSlotEmpty =>
        SelectedSlotId is not null && CurrentPage is not null && CropPlacement is null;

    /// <summary>
    /// The selected placement's zoom, bound two-way to the crop slider. Setting it clamps through
    /// <see cref="CropMath"/> and records an undo entry.
    /// </summary>
    public double CropZoom
    {
        get => CropPlacement?.Crop.Zoom ?? 1.0;
        set
        {
            if (_writingZoom)
            {
                return;
            }

            SetCropZoom(value);
        }
    }

    /// <summary>The crop's zoom, e.g. "zoom 1.30" (doc 09 §3.3).</summary>
    public string ZoomReadout => HasCropTarget
        ? "zoom " + CropZoom.ToString("0.00", CultureInfo.CurrentCulture)
        : "zoom —";

    /// <summary>Effective print resolution of the selected placement (<see cref="CropMath.EffectiveDpi"/>).</summary>
    public double CropDpi
    {
        get
        {
            var target = ResolveCrop(CurrentPage, SelectedSlotId);
            return target is null
                ? 0
                : CropMath.EffectiveDpi(
                    target.Placement.Crop, target.ImageWidth, target.ImageHeight,
                    target.SlotWidthIn, target.SlotHeightIn);
        }
    }

    /// <summary>The effective DPI as a label — how the user sees a crop going soft.</summary>
    public string DpiReadout
    {
        get
        {
            var dpi = CropDpi;
            return dpi <= 0
                ? "— dpi"
                : dpi.ToString("0", CultureInfo.CurrentCulture) + " dpi";
        }
    }

    /// <summary>True when the crop has been pushed below the print-quality threshold.</summary>
    public bool IsCropSoft => HasCropTarget && CropDpi is > 0 and < SoftDpiThreshold;

    /// <summary>True when zoom &lt; 1, so the page background shows through as a letterbox (R9).</summary>
    public bool IsCropLetterboxed => HasCropTarget && CropZoom < 1.0;

    /// <summary>The file name of the photo crop mode is editing, for the readout.</summary>
    public string CropPhotoName =>
        CropPlacement is { } placement && _session.Catalog.Find(placement.PhotoId) is { } photo
            ? photo.OriginalFileName
            : string.Empty;

    // ============================================================== loading

    /// <summary>Loads a chapter's pages and selects the first one.</summary>
    /// <param name="month">The chapter month, 1–12.</param>
    public void LoadChapter(int month)
    {
        _chapter = _session.Chapters.FirstOrDefault(c => c.Month == month);
        Rebuild(selectPageId: null);
    }

    /// <summary>Loads a chapter directly — for a host that already holds the model object.</summary>
    /// <param name="chapter">The chapter to edit.</param>
    public void LoadChapter(Chapter? chapter)
    {
        _chapter = chapter;
        Rebuild(selectPageId: null);
    }

    /// <summary>
    /// Re-reads the chapter after an edit made elsewhere (an engine run, a bin drop). The selected
    /// page survives when it still exists.
    /// </summary>
    public void Refresh()
    {
        if (_chapter is null)
        {
            return;
        }

        if (_chapter.Pages.Count != Pages.Count ||
            Pages.Where((item, index) => !ReferenceEquals(item.Page, _chapter.Pages[index])).Any())
        {
            Rebuild(SelectedPage?.Page.Id);
            return;
        }

        foreach (var item in Pages)
        {
            item.Refresh(EmptySlotCount(item.Page));
        }

        InvalidateVisiblePages();
        RefreshCropState();
    }

    private void Rebuild(string? selectPageId)
    {
        var previous = selectPageId ?? SelectedPage?.Page.Id;

        Pages.Clear();
        if (_chapter is not null)
        {
            for (var i = 0; i < _chapter.Pages.Count; i++)
            {
                var page = _chapter.Pages[i];
                Pages.Add(new PageNavigatorItemViewModel(page, i + 1, EmptySlotCount(page)));
            }
        }

        SelectedPage = Pages.FirstOrDefault(p => p.Page.Id == previous) ?? Pages.FirstOrDefault();
        SelectedSlotId = null;
        IsCropMode = false;

        OnPropertyChanged(nameof(HasPages));
        OnPropertyChanged(nameof(PageLabel));
        NotifyPagesChanged();
    }

    private int EmptySlotCount(Page page)
    {
        var slots = page.ResolveTemplate(_session.FindTemplate)?.Slots.Count ?? 0;
        var filled = page.Placements.Count(p => !string.IsNullOrEmpty(p.PhotoId));
        return Math.Max(0, slots - filled);
    }

    partial void OnSelectedPageChanged(PageNavigatorItemViewModel? value)
    {
        foreach (var item in Pages)
        {
            item.IsSelected = ReferenceEquals(item, value);
        }

        EndCropGesture();
        IsCropMode = false;
        SelectedSlotId = null;

        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(LeftPage));
        OnPropertyChanged(nameof(RightPage));
        OnPropertyChanged(nameof(HasRightPage));
        OnPropertyChanged(nameof(PageLabel));
        RefreshCropState();
        PagesChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnIsSpreadChanged(bool value)
    {
        OnPropertyChanged(nameof(LeftPage));
        OnPropertyChanged(nameof(RightPage));
        OnPropertyChanged(nameof(HasRightPage));
    }

    partial void OnSelectedSlotIdChanged(string? value) => RefreshCropState();

    partial void OnIsCropModeChanged(bool value)
    {
        if (!value)
        {
            EndCropGesture();
        }

        RefreshCropState();
    }

    partial void OnViewportZoomChanged(double value)
    {
        OnPropertyChanged(nameof(ViewportZoomLabel));
        OnPropertyChanged(nameof(IsFitToWindow));
    }

    /// <summary>
    /// The facing pair containing the current page. A spread is a view over pages <c>2k</c> and
    /// <c>2k+1</c>, never a stored entity — <see cref="Spreads"/> is the only place that pairing lives.
    /// </summary>
    private (Page? Left, Page? Right) SpreadPages()
    {
        if (_chapter is null || SelectedPage is null)
        {
            return (CurrentPage, null);
        }

        var pages = _chapter.Pages as IReadOnlyList<Page> ?? [.. _chapter.Pages];
        return Spreads.ContainingPage(pages, SelectedPage.Number - 1) is { } spread
            ? (spread.Left, spread.Right)
            : (CurrentPage, null);
    }

    // ============================================================== navigation

    /// <summary>Selects a page by model reference — keeps an external filmstrip in sync.</summary>
    /// <param name="page">The page to select; ignored when it is not in this chapter.</param>
    public void SelectPage(Page? page)
    {
        if (page is null)
        {
            return;
        }

        var item = Pages.FirstOrDefault(p => ReferenceEquals(p.Page, page) || p.Page.Id == page.Id);
        if (item is not null && !ReferenceEquals(item, SelectedPage))
        {
            SelectedPage = item;
        }
    }

    [RelayCommand]
    private void SelectNavigatorPage(PageNavigatorItemViewModel? item)
    {
        if (item is not null)
        {
            SelectedPage = item;
        }
    }

    /// <summary>Next page, or next spread when the facing pair is shown (doc 09 §5, <c>Page Down</c>).</summary>
    [RelayCommand]
    private void NextPage() => StepPage(IsSpread ? 2 : 1);

    /// <summary>Previous page or spread (<c>Page Up</c>).</summary>
    [RelayCommand]
    private void PreviousPage() => StepPage(IsSpread ? -2 : -1);

    /// <summary>First page of the chapter (<c>Home</c>).</summary>
    [RelayCommand]
    private void FirstPage() => SelectedPage = Pages.FirstOrDefault();

    /// <summary>Last page of the chapter (<c>End</c>).</summary>
    [RelayCommand]
    private void LastPage() => SelectedPage = Pages.LastOrDefault();

    private void StepPage(int delta)
    {
        if (SelectedPage is null || Pages.Count == 0)
        {
            return;
        }

        var index = Math.Clamp(Pages.IndexOf(SelectedPage) + delta, 0, Pages.Count - 1);
        SelectedPage = Pages[index];
    }

    /// <summary>Shows one page (doc 09 §3.1).</summary>
    [RelayCommand]
    private void ShowSinglePage() => IsSpread = false;

    /// <summary>Shows the facing pair with the gutter band (R8, doc 09 §3.1).</summary>
    [RelayCommand]
    private void ShowSpread() => IsSpread = true;

    /// <summary>Toggles Single ↔ Spread (<c>S</c>).</summary>
    [RelayCommand]
    private void ToggleSpread() => IsSpread = !IsSpread;

    // ============================================================== viewport zoom

    /// <summary>Zooms the canvas viewport in (<c>Ctrl+=</c>).</summary>
    [RelayCommand]
    private void ZoomIn() => SetViewportZoom(ViewportZoom * 1.25);

    /// <summary>Zooms the canvas viewport out (<c>Ctrl+-</c>).</summary>
    [RelayCommand]
    private void ZoomOut() => SetViewportZoom(ViewportZoom / 1.25);

    /// <summary>Fits the page to the window (<c>F11</c>).</summary>
    [RelayCommand]
    private void ZoomToFit() => SetViewportZoom(1.0);

    /// <summary>Steps the viewport zoom by wheel notches (<c>Ctrl+wheel</c>).</summary>
    /// <param name="steps">Signed wheel notches.</param>
    public void ZoomViewportBy(double steps) => SetViewportZoom(ViewportZoom * Math.Pow(1.1, steps));

    private void SetViewportZoom(double zoom) => ViewportZoom = Math.Clamp(zoom, 1.0, 6.0);

    // ============================================================== selection

    /// <summary>
    /// A slot was clicked: it becomes the selection, and the page it belongs to becomes the edit
    /// target — the Spread rule that "every edit still targets exactly one underlying Page,
    /// determined by cursor position" (doc 09 §3.1).
    /// </summary>
    /// <param name="page">The page under the cursor.</param>
    /// <param name="slotId">The slot under the cursor, or null for bare page.</param>
    public void SelectSlot(Page? page, string? slotId)
    {
        SelectPage(page);

        if (!string.Equals(SelectedSlotId, slotId, StringComparison.Ordinal))
        {
            SelectedSlotId = slotId;
        }

        // Clicking another slot commits the crop and moves the selection (doc 09 §3.3); crop mode
        // survives only if the new slot holds a photo.
        if (IsCropMode && CropPlacement is null)
        {
            IsCropMode = false;
        }
    }

    /// <summary>Double-click, or <c>Enter</c> on a selected slot: enter crop mode (doc 09 §3.3).</summary>
    /// <param name="page">The page under the cursor.</param>
    /// <param name="slotId">The slot to crop.</param>
    public void ActivateSlot(Page? page, string? slotId)
    {
        SelectSlot(page, slotId);

        if (CropPlacement is not null)
        {
            IsCropMode = true;
        }
        else if (page is not null && slotId is not null)
        {
            RequestFill(page, slotId);
        }
    }

    /// <summary>Selects the next slot in reading order (<c>Tab</c>); the canvas supplies the order.</summary>
    /// <param name="slotId">The slot the canvas moved to.</param>
    public void SelectNextSlot(string? slotId)
    {
        SelectedSlotId = slotId;
        if (IsCropMode && CropPlacement is null)
        {
            IsCropMode = false;
        }
    }

    /// <summary>Enters crop mode on the current selection (<c>Enter</c>).</summary>
    [RelayCommand]
    private void EnterCropMode()
    {
        if (CropPlacement is not null)
        {
            IsCropMode = true;
        }
    }

    /// <summary>Leaves crop mode (<c>Esc</c>).</summary>
    [RelayCommand]
    private void ExitCropMode() => IsCropMode = false;

    /// <summary>
    /// Asks the host to fill an empty slot from the bins (R14, doc 09 §3.6) — the click-to-fill
    /// affordance and the slot's <em>Fill from bin…</em> command.
    /// </summary>
    /// <param name="page">The page holding the empty slot.</param>
    /// <param name="slotId">The empty slot.</param>
    public void RequestFill(Page? page, string? slotId)
    {
        if (page is null || slotId is null || _chapter is null)
        {
            return;
        }

        var slot = page.ResolveTemplate(_session.FindTemplate)?.FindSlot(slotId);
        if (slot is null)
        {
            return;
        }

        SelectSlot(page, slotId);
        FillSlotRequested?.Invoke(this, new FillSlotRequestedEventArgs
        {
            Chapter = _chapter,
            Page = page,
            PageNumber = NumberOf(page),
            SlotId = slotId,
            SlotAspect = slot.Aspect > 0 ? slot.Aspect : 1.0,
            TierAffinity = slot.TierAffinity,
        });
    }

    /// <summary>Raises <see cref="FillSlotRequested"/> for the selected empty slot.</summary>
    [RelayCommand]
    private void FillSelectedSlot() => RequestFill(CurrentPage, SelectedSlotId);

    // ============================================================== crop editing

    /// <summary>True when a pointer drag on this slot should pan its crop rather than start a move.</summary>
    /// <param name="page">The page under the cursor.</param>
    /// <param name="slotId">The slot under the cursor.</param>
    public bool CanPanCrop(Page? page, string? slotId) =>
        IsCropMode &&
        page is not null && slotId is not null &&
        ReferenceEquals(page, CurrentPage) &&
        string.Equals(slotId, SelectedSlotId, StringComparison.Ordinal) &&
        page.PlacementFor(slotId) is not null;

    /// <summary>
    /// Opens the pan gesture. Every command executed until <see cref="EndCropGesture"/> folds into a
    /// single undo entry, whatever the pointer-move count (doc 09 §4).
    /// </summary>
    /// <param name="page">The page being edited.</param>
    /// <param name="slotId">The slot being panned.</param>
    public void BeginCropGesture(Page? page, string? slotId)
    {
        EndCropGesture();
        if (page is not null && slotId is not null)
        {
            _gesture = _undo.BeginGesture($"crop.pan:{page.Id}:{slotId}");
        }
    }

    /// <summary>Closes the pan gesture and seals its undo entry.</summary>
    public void EndCropGesture()
    {
        _gesture?.Dispose();
        _gesture = null;
    }

    /// <summary>
    /// Pans the crop by a pointer delta already expressed in slot-width/height units — exactly what
    /// <c>CropState.OffsetX/OffsetY</c> are measured in (doc 09 §3.3).
    /// </summary>
    /// <param name="page">The page being edited.</param>
    /// <param name="slotId">The slot being panned.</param>
    /// <param name="slotDelta">Pointer movement in slot units (<c>PageCanvasDragEventArgs.SlotDelta</c>).</param>
    public void PanCrop(Page? page, string? slotId, Vector slotDelta)
    {
        if (ResolveCrop(page, slotId) is not { } target)
        {
            return;
        }

        var before = target.Placement.Crop;
        var panned = new CropState(before.Zoom, before.OffsetX + slotDelta.X, before.OffsetY + slotDelta.Y);
        Apply(target, before, Clamp(target, panned), "Pan crop", $"crop.pan:{target.Page.Id}:{target.SlotId}");
    }

    /// <summary>
    /// Nudges the crop offsets (arrow keys: 0.01, <c>Shift</c> 0.05 — doc 09 §3.3).
    /// </summary>
    /// <param name="dx">Horizontal steps.</param>
    /// <param name="dy">Vertical steps.</param>
    /// <param name="large">True for the <c>Shift</c> multiplier.</param>
    public void NudgeCrop(int dx, int dy, bool large)
    {
        if (ResolveCrop(CurrentPage, SelectedSlotId) is not { } target)
        {
            return;
        }

        var step = large ? NudgeStep * 5 : NudgeStep;
        var before = target.Placement.Crop;
        var moved = new CropState(before.Zoom, before.OffsetX + (dx * step), before.OffsetY + (dy * step));
        Apply(target, before, Clamp(target, moved), "Nudge crop", $"crop.nudge:{target.Page.Id}:{target.SlotId}");
    }

    /// <summary>
    /// Zooms about a point: the pixel under the cursor stays put while the crop scales in 5% steps
    /// (doc 09 §3.3). Zoom below 1 is legal and letterboxes the slot (R9).
    /// </summary>
    /// <param name="page">The page being edited.</param>
    /// <param name="slotId">The slot being zoomed.</param>
    /// <param name="steps">Wheel notches, signed.</param>
    /// <param name="anchor">
    /// The cursor's offset from the slot centre in slot units; <see cref="Vector"/> zero zooms about
    /// the centre (slider and keyboard).
    /// </param>
    public void ZoomCropAt(Page? page, string? slotId, double steps, Vector anchor)
    {
        if (ResolveCrop(page, slotId) is not { } target || Math.Abs(steps) < 1e-6)
        {
            return;
        }

        var before = target.Placement.Crop;
        var zoom = CropMath.ClampZoom(before.Zoom * Math.Pow(1 + ZoomStep, steps));
        Apply(
            target,
            before,
            Clamp(target, ZoomAbout(before, zoom, anchor)),
            "Zoom crop",
            $"crop.zoom:{target.Page.Id}:{target.SlotId}");
    }

    /// <summary>Steps the crop zoom by ±0.05 about the slot centre (<c>+</c> / <c>-</c>).</summary>
    /// <param name="direction">+1 to zoom in, -1 to zoom out.</param>
    public void StepCropZoom(int direction)
    {
        if (ResolveCrop(CurrentPage, SelectedSlotId) is not { } target || direction == 0)
        {
            return;
        }

        var before = target.Placement.Crop;
        var zoom = CropMath.ClampZoom(before.Zoom + (direction * ZoomStep));
        Apply(
            target,
            before,
            Clamp(target, ZoomAbout(before, zoom, default)),
            "Zoom crop",
            $"crop.zoom:{target.Page.Id}:{target.SlotId}");
    }

    /// <summary>Sets an absolute zoom — the crop slider.</summary>
    /// <param name="zoom">The new zoom; clamped to <c>[0.25, 4.0]</c>.</param>
    public void SetCropZoom(double zoom)
    {
        if (ResolveCrop(CurrentPage, SelectedSlotId) is not { } target)
        {
            return;
        }

        var before = target.Placement.Crop;
        var clamped = CropMath.ClampZoom(zoom);
        if (Math.Abs(clamped - before.Zoom) < 1e-6)
        {
            return;
        }

        Apply(
            target,
            before,
            Clamp(target, ZoomAbout(before, clamped, default)),
            "Zoom crop",
            $"crop.zoom:{target.Page.Id}:{target.SlotId}");
    }

    /// <summary>
    /// Opens the slider's coalescing scope so a whole thumb drag is one undo entry (doc 09 §4).
    /// </summary>
    public void BeginZoomGesture()
    {
        EndCropGesture();
        if (CurrentPage is { } page && SelectedSlotId is { } slot)
        {
            _gesture = _undo.BeginGesture($"crop.zoom:{page.Id}:{slot}");
        }
    }

    /// <summary>Resets the crop to the engine's smart crop (<c>0</c>, doc 09 §3.3).</summary>
    [RelayCommand]
    private void ResetCrop()
    {
        if (ResolveCrop(CurrentPage, SelectedSlotId) is not { } target)
        {
            return;
        }

        var photo = _session.Catalog.Find(target.Placement.PhotoId);
        if (photo is null)
        {
            return;
        }

        var smart = BinPlacementCommands.SmartCropFor(
            _session, target.Page, NumberOf(target.Page), photo, target.SlotId);

        Apply(target, target.Placement.Crop, smart, "Re-run smart crop", coalesceKey: null);
        StatusRaised?.Invoke(this, "Crop reset to the engine's smart crop.");
    }

    /// <summary>
    /// Unplaces the selected photo (<c>Del</c>): the slot becomes an amber hole and the photo returns
    /// to the Unplaced bin (doc 09 §3.2, R14).
    /// </summary>
    [RelayCommand]
    private void UnplaceSelected()
    {
        if (CurrentPage is not { } page || SelectedSlotId is not { } slotId ||
            page.PlacementFor(slotId) is null)
        {
            return;
        }

        var number = NumberOf(page);
        var children = new List<IUndoableCommand>();
        var remove = BinPlacementCommands.RemovePlacement(
            _session, page, slotId, $"Unplace photo from page {number}");
        if (remove is null)
        {
            return;
        }

        children.Add(remove);
        if (BinPlacementCommands.SetPinned(_session, page, true, "Pin page") is { } pin)
        {
            children.Add(pin);
        }

        _undo.Execute(new CompositeCommand($"Unplace photo from page {number}", children));
        IsCropMode = false;
        AfterPageEdit(page);
        StatusRaised?.Invoke(this, "Photo moved to the Unplaced bin.");
    }

    private static CropState ZoomAbout(CropState crop, double zoom, Vector anchor)
    {
        if (crop.Zoom <= 0)
        {
            return crop with { Zoom = zoom };
        }

        // Keep the image point under the anchor fixed: o' = a − (a − o)·(z'/z). Both axes use the
        // same expression because the anchor is already in slot units.
        var ratio = zoom / crop.Zoom;
        return new CropState(
            zoom,
            anchor.X - ((anchor.X - crop.OffsetX) * ratio),
            anchor.Y - ((anchor.Y - crop.OffsetY) * ratio));
    }

    private static CropState Clamp(CropTarget target, CropState crop) =>
        CropMath.Clamp(crop, target.SlotAspect, target.ImageAspect);

    /// <summary>
    /// Writes a crop as one undoable edit. The pin transition rides inside the command (doc 09 §4),
    /// and both actions look the placement up by slot so a coalesced gesture and a later undo of a
    /// drop can never fight over a stale object.
    /// </summary>
    private void Apply(CropTarget target, CropState before, CropState after, string description, string? coalesceKey)
    {
        if (after == before)
        {
            return;
        }

        var page = target.Page;
        var slotId = target.SlotId;
        var pinnedBefore = page.Pinned;

        _undo.Execute(new EditCommand(
            description,
            () => WriteCrop(page, slotId, after, pinned: true),
            () => WriteCrop(page, slotId, before, pinnedBefore),
            coalesceKey));

        if (!pinnedBefore)
        {
            NotePinned(page);
        }
    }

    private void WriteCrop(Page page, string slotId, CropState crop, bool pinned)
    {
        if (page.PlacementFor(slotId) is { } placement)
        {
            placement.Crop = crop;
        }

        page.Pinned = pinned;
        _session.MarkDirty();
        AfterPageEdit(page);
    }

    // ============================================================== drag and drop

    /// <summary>
    /// The payload for dragging a placed photo out of a slot (doc 09 §3.2). Null when the slot is
    /// empty — an empty slot is a drop target, never a drag source.
    /// </summary>
    /// <param name="page">The page the drag started on.</param>
    /// <param name="slotId">The slot the drag started on.</param>
    public PhotoDragPayload? CreateDragPayload(Page? page, string? slotId)
    {
        if (page is null || slotId is null || page.PlacementFor(slotId) is not { } placement)
        {
            return null;
        }

        return new PhotoDragPayload(
            placement.PhotoId, PhotoDragOrigin.Slot, page.Id, slotId, NumberOf(page));
    }

    /// <summary>
    /// Whether a drag may drop here, for the canvas's live drop-target ring and the no-drop cursor.
    /// </summary>
    /// <param name="page">The page under the cursor.</param>
    /// <param name="slotId">The slot under the cursor.</param>
    /// <param name="data">The drag data.</param>
    public DragDropEffects EvaluateDrop(Page? page, string? slotId, IDataObject? data) =>
        Interpret(page, slotId, ReadPayload(data)) is null ? DragDropEffects.None : DragDropEffects.Move;

    /// <summary>
    /// Applies doc 09 §3.2's outcome table as <b>one</b> undo entry: move, swap, place or replace,
    /// with a fresh smart crop for every photo that lands in a new slot and the touched pages pinned.
    /// </summary>
    /// <param name="page">The page dropped on.</param>
    /// <param name="slotId">The slot dropped on.</param>
    /// <param name="data">The drag data.</param>
    /// <param name="modifiers">Modifier keys at the drop — <c>Ctrl</c> forces replace over swap.</param>
    /// <returns>True when the model changed.</returns>
    public bool ApplyDrop(Page? page, string? slotId, IDataObject? data, ModifierKeys modifiers)
    {
        var payload = ReadPayload(data);
        if (Interpret(page, slotId, payload) is not { } plan || payload is null)
        {
            return false;
        }

        var forceReplace = (modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        return Commit(plan, forceReplace);
    }

    /// <summary>What a drop would do, or null when it is not a legal drop.</summary>
    private DropPlan? Interpret(Page? page, string? slotId, PhotoDragPayload? payload)
    {
        if (_chapter is null || page is null || slotId is null || payload is null || _restoring)
        {
            return null;
        }

        if (_session.Catalog.Find(payload.PhotoId) is not { Excluded: false } photo)
        {
            return null;
        }

        if (page.ResolveTemplate(_session.FindTemplate)?.FindSlot(slotId) is null)
        {
            return null;
        }

        // Where the photo currently lives, wherever the drag claims it came from: the model invariant
        // is one placement per photo in the whole book, so this is the authority.
        var (sourcePage, sourcePlacement) = FindPlacement(payload.PhotoId);
        if (sourcePage is not null && ReferenceEquals(sourcePage, page) &&
            string.Equals(sourcePlacement?.SlotId, slotId, StringComparison.Ordinal))
        {
            return null;
        }

        return new DropPlan(page, slotId, photo, sourcePage, sourcePlacement, payload.Origin);
    }

    private bool Commit(DropPlan plan, bool forceReplace)
    {
        var target = plan.Page;
        var displaced = target.PlacementFor(plan.SlotId);
        var displacedPhoto = displaced is null ? null : _session.Catalog.Find(displaced.PhotoId);
        var swap = displaced is not null && plan.SourcePage is not null && plan.SourcePlacement is not null &&
                   !forceReplace;

        var targetNumber = NumberOf(target);
        var targetWasPinned = target.Pinned;
        var children = new List<IUndoableCommand>();

        // 1. Vacate: the incoming photo's old slot, then the target slot.
        if (plan.SourcePage is not null && plan.SourcePlacement is not null &&
            BinPlacementCommands.RemovePlacement(
                _session, plan.SourcePage, plan.SourcePlacement.SlotId, "Vacate slot") is { } vacate)
        {
            children.Add(vacate);
        }

        if (displaced is not null &&
            BinPlacementCommands.RemovePlacement(_session, target, plan.SlotId, "Vacate slot") is { } clear)
        {
            children.Add(clear);
        }

        // 2. Place the incoming photo, always with a crop computed for its new slot (doc 09 §3.2).
        children.Add(BinPlacementCommands.AddPlacement(
            _session,
            target,
            new Placement
            {
                SlotId = plan.SlotId,
                PhotoId = plan.Photo.Id,
                Crop = BinPlacementCommands.SmartCropFor(_session, target, targetNumber, plan.Photo, plan.SlotId),
            },
            "Place photo"));

        // 3. The displaced photo either swaps into the vacated source slot or falls to the bin.
        if (swap && displacedPhoto is not null && plan.SourcePage is not null && plan.SourcePlacement is not null)
        {
            children.Add(BinPlacementCommands.AddPlacement(
                _session,
                plan.SourcePage,
                new Placement
                {
                    SlotId = plan.SourcePlacement.SlotId,
                    PhotoId = displacedPhoto.Id,
                    Crop = BinPlacementCommands.SmartCropFor(
                        _session, plan.SourcePage, NumberOf(plan.SourcePage), displacedPhoto,
                        plan.SourcePlacement.SlotId),
                },
                "Place displaced photo"));
        }

        // 4. Pin every page the drop changed. Pulling from Upcoming deliberately leaves the source
        //    page unpinned so auto-layout can heal the hole (doc 09 §3.5 Decision).
        if (BinPlacementCommands.SetPinned(_session, target, true, "Pin page") is { } pinTarget)
        {
            children.Add(pinTarget);
        }

        if (plan.SourcePage is not null && !ReferenceEquals(plan.SourcePage, target) &&
            plan.Origin != PhotoDragOrigin.UpcomingBin &&
            BinPlacementCommands.SetPinned(_session, plan.SourcePage, true, "Pin page") is { } pinSource)
        {
            children.Add(pinSource);
        }

        var description = Describe(plan, displaced is not null, swap, targetNumber);
        _undo.Execute(new CompositeCommand(description, children));

        AfterPageEdit(target);
        if (plan.SourcePage is not null)
        {
            AfterPageEdit(plan.SourcePage);
        }

        SelectSlot(target, plan.SlotId);
        NotifyPagesChanged();

        if (!swap && displaced is not null)
        {
            StatusRaised?.Invoke(
                this,
                $"{(displacedPhoto?.OriginalFileName is { Length: > 0 } name ? name : "The displaced photo")} " +
                "moved to the Unplaced bin.");
        }
        else if (plan.Origin == PhotoDragOrigin.UpcomingBin && plan.SourcePage is not null)
        {
            StatusRaised?.Invoke(
                this,
                $"Page {NumberOf(plan.SourcePage)} now has an empty slot — re-flow the later pages when you are ready.");
        }

        if (!targetWasPinned)
        {
            NotePinned(target);
        }

        return true;
    }

    private static string Describe(DropPlan plan, bool occupied, bool swap, int pageNumber)
    {
        if (swap)
        {
            return $"Swap photos on page {pageNumber}";
        }

        if (occupied)
        {
            return $"Replace photo in slot {plan.SlotId} on page {pageNumber}";
        }

        return plan.SourcePage is null
            ? $"Place photo in slot {plan.SlotId} on page {pageNumber}"
            : $"Move photo to slot {plan.SlotId} on page {pageNumber}";
    }

    private static PhotoDragPayload? ReadPayload(IDataObject? data)
    {
        try
        {
            if (PhotoDragPayload.From(data) is { } payload)
            {
                return payload;
            }

            // A source that only publishes the photo id still works; it reads as an unplaced photo,
            // which is exactly the semantics of a bin drag.
            if (data?.GetDataPresent(DataFormats.UnicodeText) == true &&
                data.GetData(DataFormats.UnicodeText) is string text &&
                text.StartsWith("ph-", StringComparison.Ordinal))
            {
                return new PhotoDragPayload(text.Trim(), PhotoDragOrigin.UnplacedBin);
            }
        }
        catch (Exception ex) when (ex is COMException or NotSupportedException or InvalidOperationException)
        {
            // A foreign drag whose data cannot be materialized is simply not a photo drop.
        }

        return null;
    }

    /// <summary>The catalog entry for a placement, for the view's crop overlay.</summary>
    /// <param name="photoId">The photo id held by a placement.</param>
    public Photo? PhotoFor(string? photoId) => photoId is null ? null : _session.Catalog.Find(photoId);

    /// <summary>
    /// The already-cached thumbnail for the drag ghost (doc 09 §3.2), or null when the photo has not
    /// been decoded yet. Deliberately synchronous: a drag must not wait on a decode.
    /// </summary>
    /// <param name="photoId">The photo being dragged.</param>
    public System.Windows.Media.Imaging.BitmapSource? GhostImageFor(string? photoId) =>
        photoId is not null && _thumbnails is not null && _session.Catalog.Find(photoId) is { } photo
            ? _thumbnails.Peek(photo.ContentHash)
            : null;

    /// <summary>The dragged photo's file name, for the ghost's caption.</summary>
    /// <param name="photoId">The photo being dragged.</param>
    public string GhostLabelFor(string? photoId) =>
        photoId is not null && _session.Catalog.Find(photoId) is { } photo ? photo.OriginalFileName : string.Empty;

    private (Page? Page, Placement? Placement) FindPlacement(string photoId)
    {
        if (_chapter is null)
        {
            return (null, null);
        }

        foreach (var page in _chapter.Pages)
        {
            foreach (var placement in page.Placements)
            {
                if (string.Equals(placement.PhotoId, photoId, StringComparison.Ordinal))
                {
                    return (page, placement);
                }
            }
        }

        return (null, null);
    }

    // ============================================================== plumbing

    /// <summary>The page's 1-based number in the chapter; 1 when it is not found.</summary>
    private int NumberOf(Page page)
    {
        var item = Pages.FirstOrDefault(p => ReferenceEquals(p.Page, page));
        if (item is not null)
        {
            return item.Number;
        }

        var index = _chapter?.Pages.IndexOf(page) ?? -1;
        return index < 0 ? 1 : index + 1;
    }

    private CropTarget? ResolveCrop(Page? page, string? slotId)
    {
        if (page is null || slotId is null || page.PlacementFor(slotId) is not { } placement)
        {
            return null;
        }

        var slot = page.ResolveTemplate(_session.FindTemplate)?.FindSlot(slotId);
        var photo = _session.Catalog.Find(placement.PhotoId);
        if (slot is null || photo is null)
        {
            return null;
        }

        var (trimW, trimH) = BinPlacementCommands.TrimSizeOf(_session.Book);
        var imageW = photo.Width > 0 ? photo.Width : 1000.0;
        var imageH = photo.Height > 0 ? photo.Height : 1000.0;

        // Pan, zoom, the letterbox badge and the DPI readout all work in the frame the renderer
        // actually draws into — the bleed-extended, caption-band-shortened box, not the nominal slot.
        // Clamping against the nominal box let the user pan into a gap the render then showed as page
        // background (CropMath.PhotoBox).
        var (boxWidthIn, boxHeightIn) = CropMath.PhotoBox(
            slot.Rect,
            trimW,
            trimH,
            captionBelow: slot.CaptionPolicy == CaptionPolicy.Below && !string.IsNullOrWhiteSpace(photo.Caption),
            bleedIn: slot.SpanId is null ? PageGeometry.BleedIn : 0.0);

        return new CropTarget(page, slotId, placement, boxWidthIn, boxHeightIn, imageW, imageH);
    }

    private void AfterPageEdit(Page page)
    {
        var item = Pages.FirstOrDefault(p => ReferenceEquals(p.Page, page));
        item?.Refresh(EmptySlotCount(page));

        PageInvalidated?.Invoke(this, new PageEditorPageEventArgs(page));
        RefreshCropState();
    }

    private void InvalidateVisiblePages()
    {
        if (LeftPage is { } left)
        {
            PageInvalidated?.Invoke(this, new PageEditorPageEventArgs(left));
        }

        if (RightPage is { } right)
        {
            PageInvalidated?.Invoke(this, new PageEditorPageEventArgs(right));
        }
    }

    private void NotifyPagesChanged()
    {
        OnPropertyChanged(nameof(LeftPage));
        OnPropertyChanged(nameof(RightPage));
        OnPropertyChanged(nameof(HasRightPage));
        PagesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Doc 09 §3.2's first-time explanation of what pinning costs the user.</summary>
    private void NotePinned(Page page)
    {
        if (_pinNoticeShown || !page.Pinned)
        {
            return;
        }

        _pinNoticeShown = true;
        StatusRaised?.Invoke(this, "This page is now pinned — auto-layout will leave it alone.");
    }

    private void RefreshCropState()
    {
        _writingZoom = true;
        try
        {
            OnPropertyChanged(nameof(HasCropTarget));
            OnPropertyChanged(nameof(IsSelectedSlotEmpty));
            OnPropertyChanged(nameof(CropZoom));
            OnPropertyChanged(nameof(ZoomReadout));
            OnPropertyChanged(nameof(CropDpi));
            OnPropertyChanged(nameof(DpiReadout));
            OnPropertyChanged(nameof(IsCropSoft));
            OnPropertyChanged(nameof(IsCropLetterboxed));
            OnPropertyChanged(nameof(CropPhotoName));
        }
        finally
        {
            _writingZoom = false;
        }
    }

    /// <summary>
    /// An undo or redo elsewhere can have changed any of this chapter's pages, so the canvases and
    /// the navigator re-read the model rather than trusting their last state.
    /// </summary>
    private void OnUndoStackChanged(object? sender, UndoStackChangedEventArgs e)
    {
        if (e.Kind is not (UndoStackChange.Undone or UndoStackChange.Redone) || _chapter is null)
        {
            return;
        }

        _restoring = true;
        try
        {
            Refresh();
        }
        finally
        {
            _restoring = false;
        }
    }

    /// <summary>Everything a crop gesture needs about one placement, resolved once.</summary>
    private sealed record CropTarget(
        Page Page,
        string SlotId,
        Placement Placement,
        double SlotWidthIn,
        double SlotHeightIn,
        double ImageWidth,
        double ImageHeight)
    {
        public double SlotAspect => SlotWidthIn / SlotHeightIn;

        public double ImageAspect => ImageWidth / ImageHeight;
    }

    /// <summary>A legal drop, resolved from the payload against the model.</summary>
    private sealed record DropPlan(
        Page Page,
        string SlotId,
        Photo Photo,
        Page? SourcePage,
        Placement? SourcePlacement,
        PhotoDragOrigin Origin);
}

/// <summary>One page in the editor's navigator strip.</summary>
public sealed partial class PageNavigatorItemViewModel : ObservableObject
{
    /// <param name="page">The page model.</param>
    /// <param name="number">Its 1-based number in the chapter.</param>
    /// <param name="emptySlots">How many of its slots are empty (R14).</param>
    public PageNavigatorItemViewModel(Page page, int number, int emptySlots)
    {
        ArgumentNullException.ThrowIfNull(page);
        Page = page;
        Number = number;
        EmptySlotCount = emptySlots;
    }

    /// <summary>The page this entry stands for.</summary>
    public Page Page { get; }

    /// <summary>1-based page number within the chapter.</summary>
    [ObservableProperty]
    private int _number;

    /// <summary>True when this is the page the editor targets.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Empty slots on the page — the amber roll-up badge (R14, doc 09 §3.6).</summary>
    [ObservableProperty]
    private int _emptySlotCount;

    /// <summary>True when the page has at least one empty slot.</summary>
    public bool HasEmptySlots => EmptySlotCount > 0;

    /// <summary>True when the user has touched the page, so auto-layout leaves it alone (R16).</summary>
    public bool IsPinned => Page.Pinned;

    /// <summary>True when the page owns hand-edited geometry (R15).</summary>
    public bool IsDetached => Page.IsDetached;

    /// <summary>Which side of the spread the page falls on — <see cref="Spreads"/> owns the pairing.</summary>
    public PageSide Side => Spreads.SideOf(Number - 1);

    /// <summary>Re-reads everything derived from the model.</summary>
    /// <param name="emptySlots">The page's current empty-slot count.</param>
    public void Refresh(int emptySlots)
    {
        EmptySlotCount = emptySlots;
        OnPropertyChanged(nameof(HasEmptySlots));
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(IsDetached));
        OnPropertyChanged(nameof(Side));
    }

    partial void OnEmptySlotCountChanged(int value) => OnPropertyChanged(nameof(HasEmptySlots));
}

/// <summary>Carries the page a canvas must re-render.</summary>
/// <param name="page">The page whose content changed.</param>
public sealed class PageEditorPageEventArgs(Page page) : EventArgs
{
    /// <summary>The page that changed.</summary>
    public Page Page { get; } = page;
}

/// <summary>
/// The "fill this empty slot from the bins" request of R14 / doc 09 §3.6. The slot's aspect and tier
/// affinity travel with it so the bin panel can sort best-fit suggestions first (doc 09 §3.4).
/// </summary>
public sealed class FillSlotRequestedEventArgs : EventArgs
{
    /// <summary>The chapter being edited.</summary>
    public required Chapter Chapter { get; init; }

    /// <summary>The page holding the empty slot.</summary>
    public required Page Page { get; init; }

    /// <summary>The page's 1-based number in the chapter.</summary>
    public int PageNumber { get; init; }

    /// <summary>The empty slot to fill.</summary>
    public required string SlotId { get; init; }

    /// <summary>The slot's physical width/height — the first sort key for suggestions.</summary>
    public double SlotAspect { get; init; }

    /// <summary>The tier the slot prefers — the second sort key (R26).</summary>
    public TierAffinity TierAffinity { get; init; }
}
