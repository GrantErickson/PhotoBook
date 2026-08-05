using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PhotoBook.App.Controls;
using PhotoBook.App.ViewModels.Pages;
using CoreRect = PhotoBook.Core.Model.Rect;
using CropMath = PhotoBook.Core.Model.CropMath;
using PageSide = PhotoBook.Core.Model.PageSide;

namespace PhotoBook.App.Views.Pages;

/// <summary>
/// The Pages-tab editing surface (doc 09 §3.1–§3.3, §3.6). It owns no model logic: it turns
/// <see cref="PageCanvas"/> pointer events into calls on <see cref="PageEditorViewModel"/>, keeps the
/// two canvases of a Spread in step, and draws the guide overlay and drag ghost.
/// </summary>
public partial class PageEditorView : UserControl
{
    /// <summary>
    /// The layout-override model driving the handles drawn over the page (doc 09 §3.7). The shell
    /// supplies it; when it is null or inactive the surface is invisible and does not hit-test.
    /// </summary>
    public static readonly DependencyProperty OverrideModelProperty = DependencyProperty.Register(
        nameof(OverrideModel), typeof(PageOverrideViewModel), typeof(PageEditorView),
        new PropertyMetadata(null));

    private PageEditorViewModel? _viewModel;
    private PageCanvas? _panCanvas;
    private bool _panning;

    /// <summary>Creates the view and wires both page canvases.</summary>
    public PageEditorView()
    {
        InitializeComponent();

        Wire(LeftCanvas, LeftOverlay);
        Wire(RightCanvas, RightOverlay);

        DataContextChanged += OnDataContextChanged;
        Viewport.SizeChanged += (_, _) => UpdateViewportSize();

        // A slider drag is one undo entry, so the gesture is opened at thumb-press (doc 09 §4).
        ZoomSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnZoomThumbDragStarted));
        ZoomSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnZoomThumbDragCompleted));
    }

    /// <summary>Per-page layout override mode, or null when the shell does not offer it.</summary>
    public PageOverrideViewModel? OverrideModel
    {
        get => (PageOverrideViewModel?)GetValue(OverrideModelProperty);
        set => SetValue(OverrideModelProperty, value);
    }

    /// <summary>True while layout override mode owns the pointer, so page gestures stand down.</summary>
    private bool IsOverrideActive => OverrideModel is { IsActive: true };

    /// <summary>The canvas showing the page every edit targets.</summary>
    private PageCanvas ActiveCanvas =>
        RightHost.Visibility == Visibility.Visible &&
        _viewModel?.CurrentPage is { } current && ReferenceEquals(RightCanvas.Page, current)
            ? RightCanvas
            : LeftCanvas;

    // ============================================================ view model

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.PageInvalidated -= OnPageInvalidated;
        }

        _viewModel = DataContext as PageEditorViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.PageInvalidated += OnPageInvalidated;
        }

        SyncLayout();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PageEditorViewModel.IsSpread):
            case nameof(PageEditorViewModel.LeftPage):
            case nameof(PageEditorViewModel.RightPage):
            case nameof(PageEditorViewModel.HasRightPage):
            case nameof(PageEditorViewModel.SelectedPage):
                SyncLayout();
                break;

            case nameof(PageEditorViewModel.SelectedSlotId):
            case nameof(PageEditorViewModel.IsCropMode):
            case nameof(PageEditorViewModel.CropZoom):
                SyncSelection();
                UpdateOverlays();
                break;

            case nameof(PageEditorViewModel.ViewportZoom):
                UpdateViewportSize();
                break;
        }
    }

    private void OnPageInvalidated(object? sender, PageEditorPageEventArgs e)
    {
        foreach (var canvas in Canvases())
        {
            if (ReferenceEquals(canvas.Page, e.Page))
            {
                canvas.RequestRender();
            }
        }

        UpdateOverlays();
    }

    private IEnumerable<PageCanvas> Canvases()
    {
        yield return LeftCanvas;
        yield return RightCanvas;
    }

    private PageEditorOverlay OverlayFor(PageCanvas canvas) =>
        ReferenceEquals(canvas, RightCanvas) ? RightOverlay : LeftOverlay;

    // ============================================================ layout

    /// <summary>
    /// Applies the Single|Spread choice. Only the column widths and one visibility change: the two
    /// canvases are always the same control doing the same job on their own page (doc 09 §3.1).
    /// </summary>
    private void SyncLayout()
    {
        var spread = _viewModel?.IsSpread == true;
        var hasRight = _viewModel?.HasRightPage == true;

        RightHost.Visibility = spread && hasRight ? Visibility.Visible : Visibility.Collapsed;
        SeamColumn.Width = new GridLength(spread && hasRight ? 2 : 0);
        RightColumn.Width = spread && hasRight ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        SyncSelection();
        UpdateViewportSize();
        UpdateOverlays();
    }

    private void SyncSelection()
    {
        var current = _viewModel?.CurrentPage;
        var slot = _viewModel?.SelectedSlotId;

        foreach (var canvas in Canvases())
        {
            var wanted = current is not null && ReferenceEquals(canvas.Page, current) ? slot : null;
            if (!string.Equals(canvas.SelectedSlotId, wanted, StringComparison.Ordinal))
            {
                canvas.SelectedSlotId = wanted;
            }
        }
    }

    /// <summary>
    /// Sizes the canvas host for the viewport zoom. The size is always explicit: inside a scrolling
    /// viewport an auto-sized star grid would measure against infinity and collapse to nothing.
    /// </summary>
    private void UpdateViewportSize()
    {
        var zoom = _viewModel?.ViewportZoom ?? 1.0;
        var width = Math.Max(120, Viewport.ActualWidth - 50);
        var height = Math.Max(120, Viewport.ActualHeight - 42);

        CanvasHost.Width = width * zoom;
        CanvasHost.Height = height * zoom;
    }

    // ============================================================ canvas wiring

    private void Wire(PageCanvas canvas, PageEditorOverlay overlay)
    {
        canvas.SlotClicked += (_, e) => OnSlotClicked(canvas, e);
        canvas.SlotActivated += (_, e) => OnSlotActivated(canvas, e);
        canvas.EmptySlotActivated += (_, e) => _viewModel?.RequestFill(canvas.Page, e.SlotId);
        canvas.DragStarted += (_, e) => OnDragStarted(canvas, e);
        canvas.DragDelta += (_, e) => OnDragDelta(canvas, e);
        canvas.DragCompleted += (_, e) => OnDragCompleted(canvas, e);
        canvas.SlotDragOver += (_, e) => OnSlotDragOver(canvas, e);
        canvas.SlotDrop += (_, e) => OnSlotDrop(canvas, e);
        canvas.Wheel += (_, e) => OnCanvasWheel(canvas, e);
        canvas.PreviewRendered += (_, _) => UpdateOverlay(canvas, overlay);
        canvas.SizeChanged += (_, _) => UpdateOverlay(canvas, overlay);
    }

    private void OnSlotClicked(PageCanvas canvas, PageCanvasClickEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (e.Button == MouseButton.Right)
        {
            _viewModel.SelectSlot(canvas.Page, e.SlotId);
            ShowSlotMenu(canvas, e.SlotId);
            e.Handled = true;
            return;
        }

        _viewModel.SelectSlot(canvas.Page, e.SlotId);
    }

    private void OnSlotActivated(PageCanvas canvas, PageCanvasClickEventArgs e)
    {
        _viewModel?.ActivateSlot(canvas.Page, e.SlotId);
        SyncSelection();
        UpdateOverlays();
    }

    /// <summary>
    /// A drag inside a slot: in crop mode it pans the crop (doc 09 §3.3), otherwise it hands the
    /// gesture to WPF drag-and-drop to move or swap the photo (doc 09 §3.2).
    /// </summary>
    private void OnDragStarted(PageCanvas canvas, PageCanvasDragEventArgs e)
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        if (vm.CanPanCrop(canvas.Page, e.OriginSlotId))
        {
            _panning = true;
            _panCanvas = canvas;
            vm.BeginCropGesture(canvas.Page, e.OriginSlotId);
            canvas.BeginInteractiveGesture();
            return;
        }

        if (vm.CreateDragPayload(canvas.Page, e.OriginSlotId) is not { } payload)
        {
            return;
        }

        ShowGhost(payload.PhotoId, canvas.TranslatePoint(e.ControlPoint, this));
        try
        {
            canvas.BeginDragDrop(payload.ToDataObject(), DragDropEffects.Move);
        }
        finally
        {
            HideGhost();
            UpdateOverlays();
        }
    }

    private void OnDragDelta(PageCanvas canvas, PageCanvasDragEventArgs e)
    {
        if (!_panning || !ReferenceEquals(canvas, _panCanvas) || _viewModel is not { } vm)
        {
            return;
        }

        vm.PanCrop(canvas.Page, e.OriginSlotId, e.SlotDelta);
        canvas.RequestRender();
        UpdateOverlay(canvas, OverlayFor(canvas));
    }

    private void OnDragCompleted(PageCanvas canvas, PageCanvasDragEventArgs e)
    {
        if (!_panning || !ReferenceEquals(canvas, _panCanvas))
        {
            return;
        }

        _panning = false;
        _panCanvas = null;
        _viewModel?.EndCropGesture();
        canvas.EndInteractiveGesture();
        UpdateOverlay(canvas, OverlayFor(canvas));
    }

    private void OnSlotDragOver(PageCanvas canvas, PageCanvasDropEventArgs e)
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        e.Effects = vm.EvaluateDrop(canvas.Page, e.SlotId, e.Data);
        MoveGhost(canvas.TranslatePoint(e.ControlPoint, this));
    }

    private void OnSlotDrop(PageCanvas canvas, PageCanvasDropEventArgs e)
    {
        HideGhost();

        if (_viewModel is not { } vm)
        {
            return;
        }

        e.Effects = vm.ApplyDrop(canvas.Page, e.SlotId, e.Data, e.Modifiers)
            ? DragDropEffects.Move
            : DragDropEffects.None;

        SyncSelection();
        UpdateOverlays();
    }

    /// <summary>
    /// <c>Ctrl+wheel</c> zooms the viewport; a bare wheel in crop mode zooms the crop about the
    /// cursor, so the pixel under the pointer stays put (doc 09 §3.3).
    /// </summary>
    private void OnCanvasWheel(PageCanvas canvas, PageCanvasWheelEventArgs e)
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        if ((e.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            vm.ZoomViewportBy(e.Steps);
            e.Handled = true;
            return;
        }

        if (!vm.IsCropMode || e.SlotId is null || !ReferenceEquals(canvas.Page, vm.CurrentPage) ||
            !string.Equals(e.SlotId, vm.SelectedSlotId, StringComparison.Ordinal))
        {
            return;
        }

        vm.ZoomCropAt(canvas.Page, e.SlotId, e.Steps, AnchorInSlotUnits(canvas, e.SlotId, e.ControlPoint));
        canvas.RequestRender();
        UpdateOverlay(canvas, OverlayFor(canvas));
        e.Handled = true;
    }

    /// <summary>The cursor's offset from the slot centre, in the slot-width units the crop model uses.</summary>
    private static Vector AnchorInSlotUnits(PageCanvas canvas, string slotId, Point controlPoint)
    {
        var rect = canvas.SlotRectInControl(slotId);
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
        {
            return default;
        }

        var centre = new Point(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2));
        return canvas.ControlDeltaToSlotUnits(slotId, controlPoint - centre);
    }

    // ============================================================ slot menu

    /// <summary>
    /// The slot context menu of doc 09 §3.3 and §3.6: crop commands on a photo, <em>Fill from bin…</em>
    /// on an empty amber slot.
    /// </summary>
    private void ShowSlotMenu(PageCanvas canvas, string? slotId)
    {
        if (_viewModel is not { } vm || canvas.Page is not { } page || slotId is null)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = canvas };
        var occupied = page.PlacementFor(slotId) is not null;

        if (occupied)
        {
            menu.Items.Add(MenuItemFor("Edit crop", "Enter", () => vm.ActivateSlot(page, slotId)));
            menu.Items.Add(MenuItemFor("Re-run smart crop", "0", () => vm.ResetCropCommand.Execute(null)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItemFor("Unplace photo", "Del", () => vm.UnplaceSelectedCommand.Execute(null)));
        }
        else
        {
            menu.Items.Add(MenuItemFor("Fill from bin…", string.Empty, () => vm.RequestFill(page, slotId)));
        }

        menu.IsOpen = true;
    }

    private static MenuItem MenuItemFor(string header, string gesture, Action action)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture };
        item.Click += (_, _) => action();
        return item;
    }

    // ============================================================ overlay

    private void UpdateOverlays()
    {
        foreach (var canvas in Canvases())
        {
            UpdateOverlay(canvas, OverlayFor(canvas));
        }
    }

    private void UpdateOverlay(PageCanvas canvas, PageEditorOverlay overlay)
    {
        UpdateGutter(canvas, overlay);
        UpdateCropGuides(canvas, overlay);
    }

    /// <summary>
    /// The Spread view's gutter caution band: half an inch of page either side of the spine, taken
    /// from the geometry the renderer actually drew with (doc 09 §3.1).
    /// </summary>
    private void UpdateGutter(PageCanvas canvas, PageEditorOverlay overlay)
    {
        if (_viewModel?.IsSpread != true || RightHost.Visibility != Visibility.Visible ||
            canvas.Preview is not { } preview || canvas.PreviewScale <= 0)
        {
            overlay.SetGutter(Rect.Empty, null, 0);
            return;
        }

        var width = preview.Geometry.Inches(preview.Geometry.GutterCautionIn) * canvas.PreviewScale;
        var side = ReferenceEquals(canvas, LeftCanvas) ? PageSide.Left : PageSide.Right;
        overlay.SetGutter(canvas.TrimRect, side, width);
    }

    /// <summary>
    /// Crop-mode guides for the selected slot. The image rect comes from <see cref="CropMath"/>, so
    /// when the user zooms below 1.0 the outline shrinks inside the slot and the letterbox where the
    /// page background shows through is unmistakable (R9).
    /// </summary>
    private void UpdateCropGuides(PageCanvas canvas, PageEditorOverlay overlay)
    {
        if (_viewModel is not { IsCropMode: true } vm ||
            vm.SelectedSlotId is not { } slotId ||
            canvas.Page is not { } page ||
            !ReferenceEquals(page, vm.CurrentPage) ||
            page.PlacementFor(slotId) is not { } placement)
        {
            overlay.ClearCrop();
            return;
        }

        var slotRect = canvas.SlotRectInControl(slotId);
        if (slotRect.IsEmpty || slotRect.Width <= 2 || slotRect.Height <= 2)
        {
            overlay.ClearCrop();
            return;
        }

        var photo = vm.PhotoFor(placement.PhotoId);
        var imageW = photo is { Width: > 0 } ? photo.Width : 1000.0;
        var imageH = photo is { Height: > 0 } ? photo.Height : 1000.0;

        var drawn = CropMath.ImageRect(
            placement.Crop, imageW, imageH,
            new CoreRect(slotRect.X, slotRect.Y, slotRect.Width, slotRect.Height));
        var imageRect = new Rect(drawn.X, drawn.Y, Math.Max(0, drawn.W), Math.Max(0, drawn.H));

        IReadOnlyList<Rect> focus = photo is null
            ? []
            : photo.FocusRegions
                .Where(region => region.Rect.IsWellFormed)
                .Select(region => new Rect(
                    imageRect.X + (region.Rect.X * imageRect.Width),
                    imageRect.Y + (region.Rect.Y * imageRect.Height),
                    region.Rect.W * imageRect.Width,
                    region.Rect.H * imageRect.Height))
                .ToList();

        overlay.ShowCrop(slotRect, imageRect, focus, placement.Crop.Zoom < 1.0);
    }

    // ============================================================ drag ghost

    private void ShowGhost(string photoId, Point position)
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        var image = vm.GhostImageFor(photoId);
        GhostImage.Source = image;
        GhostImage.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        GhostLabel.Text = image is null ? vm.GhostLabelFor(photoId) : string.Empty;

        DragGhost.Visibility = Visibility.Visible;
        MoveGhost(position);
    }

    private void MoveGhost(Point position)
    {
        if (DragGhost.Visibility != Visibility.Visible)
        {
            return;
        }

        Canvas.SetLeft(DragGhost, position.X + 14);
        Canvas.SetTop(DragGhost, position.Y + 14);
    }

    private void HideGhost() => DragGhost.Visibility = Visibility.Collapsed;

    // ============================================================ keyboard

    private void OnZoomThumbDragStarted(object sender, DragStartedEventArgs e) =>
        _viewModel?.BeginZoomGesture();

    private void OnZoomThumbDragCompleted(object sender, DragCompletedEventArgs e) =>
        _viewModel?.EndCropGesture();

    /// <inheritdoc/>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPreviewKeyDown(e);

        // Layout override mode owns Esc, Del and Tab while it is open (doc 09 §3.7), and this is a
        // preview handler, so it would otherwise swallow them before the surface ever saw them.
        if (_viewModel is not { } vm || e.OriginalSource is TextBoxBase || IsOverrideActive)
        {
            return;
        }

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        switch (e.Key)
        {
            case Key.S when !ctrl:
                vm.ToggleSpreadCommand.Execute(null);
                break;

            case Key.Enter:
                vm.EnterCropModeCommand.Execute(null);
                break;

            case Key.Escape:
                if (vm.IsCropMode)
                {
                    vm.ExitCropModeCommand.Execute(null);
                }
                else
                {
                    vm.SelectSlot(vm.CurrentPage, null);
                }

                break;

            case Key.D0 or Key.NumPad0 when vm.IsCropMode:
                vm.ResetCropCommand.Execute(null);
                break;

            case Key.OemPlus or Key.Add:
                if (ctrl)
                {
                    vm.ZoomInCommand.Execute(null);
                }
                else
                {
                    vm.StepCropZoom(1);
                    ActiveCanvas.RequestRender();
                }

                break;

            case Key.OemMinus or Key.Subtract:
                if (ctrl)
                {
                    vm.ZoomOutCommand.Execute(null);
                }
                else
                {
                    vm.StepCropZoom(-1);
                    ActiveCanvas.RequestRender();
                }

                break;

            case Key.Left or Key.Right or Key.Up or Key.Down when vm.IsCropMode:
                vm.NudgeCrop(
                    e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0,
                    e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0,
                    shift);
                ActiveCanvas.RequestRender();
                break;

            case Key.Tab:
                vm.SelectNextSlot(ActiveCanvas.SelectNextSlot(!shift));
                break;

            case Key.Delete:
                vm.UnplaceSelectedCommand.Execute(null);
                break;

            case Key.PageDown:
                vm.NextPageCommand.Execute(null);
                break;

            case Key.PageUp:
                vm.PreviousPageCommand.Execute(null);
                break;

            case Key.Home:
                vm.FirstPageCommand.Execute(null);
                break;

            case Key.End:
                vm.LastPageCommand.Execute(null);
                break;

            case Key.F11:
                vm.ZoomToFitCommand.Execute(null);
                break;

            default:
                return;
        }

        UpdateOverlays();
        e.Handled = true;
    }
}
