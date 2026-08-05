using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoBook.App.Services;
using PhotoBook.Rendering;
using SkiaSharp;
using CorePage = PhotoBook.Core.Model.Page;

namespace PhotoBook.App.Controls;

/// <summary>
/// The interactive page surface of the Pages tab (doc 09 §3): it shows the page exactly as the PDF
/// will print it — the pixels come from <c>PhotoBook.Rendering</c>, never from a second drawing path
/// (ADR-0003) — and turns pointer input into slot-addressed events the editing features act on.
///
/// <para><b>Three coordinate spaces.</b> Everything the renderer reports (<c>SlotRects</c>,
/// <c>VisibleImageRects</c>, <c>TextSlotRects</c>) is in <i>preview-bitmap pixels</i>. The bitmap is
/// drawn letterboxed and aspect-preserved inside the control, giving <i>control pixels</i>. Slot
/// geometry and drag deltas are most useful in <i>normalized page coordinates</i> over the trim box
/// (kernel §3). The mapping helpers on this control are the only place that conversion is written:
/// <see cref="ControlToBitmap"/>, <see cref="BitmapToControl"/>, <see cref="ControlToNormalized"/>,
/// <see cref="NormalizedToControl"/>, <see cref="ControlDeltaToNormalized"/> and
/// <see cref="ControlDeltaToSlotUnits"/>.</para>
///
/// <para><b>Rendering.</b> Give it a <see cref="Renderer"/> and it keeps itself current: renders run
/// on a background thread at the control's own pixel size, are coalesced by a debounce timer so a
/// drag stays smooth, and are blitted through <see cref="PixelBridge"/>. A caller that renders
/// elsewhere can set <see cref="Preview"/> directly instead.</para>
///
/// <para><b>The sheet.</b> A v1 page is solid black (kernel §3), so it is drawn as a physical sheet
/// standing on a lighter <c>PageTableBrush</c> table: a soft drop shadow, then the page pixels
/// clipped to the trim box, then a <c>PageEdgeBrush</c> hairline on the cut line. Nothing outside
/// trim is shown unless <see cref="ShowGuides"/> is on, because nothing outside trim is printed —
/// the sheet on screen is exactly the sheet that comes back from the printer. With guides on the
/// sheet grows to the bleed box and the bleed, trim and safe rects are drawn over it.
/// <see cref="SpreadSide"/> tells a canvas it is one half of a facing pair, which suppresses the
/// bleed and the shadow at the spine and shades the gutter, so two canvases read as one open book.</para>
///
/// <para><b>Interaction layer.</b> Over the bitmap the canvas draws hover and selection outlines and
/// the amber empty-slot affordance of R14 — dashed <c>AmberFlagBrush</c> border, amber wash, a
/// photo-plus glyph and a "Click to fill" pill on hover. Empty slots are template slots with no
/// <c>Placement</c>.</para>
/// </summary>
public sealed class PageCanvas : FrameworkElement
{
    private static readonly IReadOnlyDictionary<string, SKRect> NoRects =
        new Dictionary<string, SKRect>(StringComparer.Ordinal);

    private readonly DispatcherTimer _debounce;
    private readonly HashSet<string> _emptySlots = new(StringComparer.Ordinal);

    private BitmapSource? _bitmap;
    private CancellationTokenSource? _renderCts;
    private bool _renderInFlight;
    private bool _renderPending;
    private bool _settingPreview;
    private int _gestureDepth;

    private Point _pressPoint;
    private Point _lastPoint;
    private string? _pressSlotId;
    private bool _pressed;
    private bool _dragging;

    /// <summary>Creates the canvas. It is a drop target and focusable from the moment it exists.</summary>
    public PageCanvas()
    {
        Focusable = true;
        AllowDrop = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);

        _debounce = new DispatcherTimer(DispatcherPriority.Render) { Interval = DebounceInterval };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            StartRender();
        };

        Loaded += (_, _) => RequestRender();
        Unloaded += (_, _) =>
        {
            _debounce.Stop();
            _renderCts?.Cancel();
        };
    }

    // ================================================================ properties

    /// <summary>The page being edited. Setting it re-renders and clears hover.</summary>
    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page), typeof(CorePage), typeof(PageCanvas),
        new FrameworkPropertyMetadata(null, OnPageChanged));

    /// <summary>Produces the preview. Runs on a background thread; see <see cref="PageCanvasRenderer"/>.</summary>
    public static readonly DependencyProperty RendererProperty = DependencyProperty.Register(
        nameof(Renderer), typeof(PageCanvasRenderer), typeof(PageCanvas),
        new FrameworkPropertyMetadata(null, (d, _) => ((PageCanvas)d).RequestRender(immediate: true)));

    /// <summary>The rendered page: its pixels, its <c>SlotRects</c> and the geometry it was drawn with.</summary>
    public static readonly DependencyProperty PreviewProperty = DependencyProperty.Register(
        nameof(Preview), typeof(PagePreview), typeof(PageCanvas),
        new FrameworkPropertyMetadata(null, OnPreviewChanged));

    /// <summary>The selected slot (doc 09 §3.3: click selects, double-click crops). Two-way by default.</summary>
    public static readonly DependencyProperty SelectedSlotIdProperty = DependencyProperty.Register(
        nameof(SelectedSlotId), typeof(string), typeof(PageCanvas),
        new FrameworkPropertyMetadata(
            null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyPropertyKey HoveredSlotIdKey = DependencyProperty.RegisterReadOnly(
        nameof(HoveredSlotId), typeof(string), typeof(PageCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The slot under the pointer, or null. Read-only; the canvas maintains it.</summary>
    public static readonly DependencyProperty HoveredSlotIdProperty = HoveredSlotIdKey.DependencyProperty;

    private static readonly DependencyPropertyKey DropTargetSlotIdKey = DependencyProperty.RegisterReadOnly(
        nameof(DropTargetSlotId), typeof(string), typeof(PageCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The slot a drag is currently over, drawn with the accent inset ring (doc 09 §3.2).</summary>
    public static readonly DependencyProperty DropTargetSlotIdProperty = DropTargetSlotIdKey.DependencyProperty;

    /// <summary>The table the sheet stands on; defaults to the palette's <c>PageTableBrush</c>.</summary>
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(PageCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Whether the bleed, trim and safe-margin guides are drawn over the sheet (kernel §3, the
    /// <c>G</c> key). Off by default; turning them on also grows the visible sheet from the trim box
    /// to the bleed box, so the strip that will be cut away becomes visible.
    /// </summary>
    public static readonly DependencyProperty ShowGuidesProperty = DependencyProperty.Register(
        nameof(ShowGuides), typeof(bool), typeof(PageCanvas),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Which half of a facing pair this canvas is drawing; <c>None</c> for a single page.</summary>
    public static readonly DependencyProperty SpreadSideProperty = DependencyProperty.Register(
        nameof(SpreadSide), typeof(PageSheetSide), typeof(PageCanvas),
        new FrameworkPropertyMetadata(PageSheetSide.None, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Whether the amber empty-slot affordance is drawn over the bitmap (R14). Default true.</summary>
    public static readonly DependencyProperty ShowEmptySlotAffordanceProperty = DependencyProperty.Register(
        nameof(ShowEmptySlotAffordance), typeof(bool), typeof(PageCanvas),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Whether hover, selection and drop outlines are drawn and pointer events raised. Default true.</summary>
    public static readonly DependencyProperty IsInteractiveProperty = DependencyProperty.Register(
        nameof(IsInteractive), typeof(bool), typeof(PageCanvas),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Cap on the preview's pixel width, so a maximized window does not render a 4K page per drag tick.</summary>
    public static readonly DependencyProperty MaxPreviewWidthProperty = DependencyProperty.Register(
        nameof(MaxPreviewWidth), typeof(int), typeof(PageCanvas),
        new FrameworkPropertyMetadata(2000, (d, _) => ((PageCanvas)d).RequestRender()));

    /// <summary>Idle delay before an ordinary re-render request runs. Default 16 ms — one frame.</summary>
    public static readonly DependencyProperty DebounceIntervalProperty = DependencyProperty.Register(
        nameof(DebounceInterval), typeof(TimeSpan), typeof(PageCanvas),
        new FrameworkPropertyMetadata(TimeSpan.FromMilliseconds(16)));

    /// <summary>
    /// Idle delay used between <see cref="BeginInteractiveGesture"/> and
    /// <see cref="EndInteractiveGesture"/>. Default 70 ms: the model keeps changing every pointer
    /// move, but the page is re-rendered at most ~14 times a second while it does.
    /// </summary>
    public static readonly DependencyProperty GestureDebounceIntervalProperty = DependencyProperty.Register(
        nameof(GestureDebounceInterval), typeof(TimeSpan), typeof(PageCanvas),
        new FrameworkPropertyMetadata(TimeSpan.FromMilliseconds(70)));

    /// <inheritdoc cref="PageProperty"/>
    public CorePage? Page
    {
        get => (CorePage?)GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    /// <inheritdoc cref="RendererProperty"/>
    public PageCanvasRenderer? Renderer
    {
        get => (PageCanvasRenderer?)GetValue(RendererProperty);
        set => SetValue(RendererProperty, value);
    }

    /// <inheritdoc cref="PreviewProperty"/>
    public PagePreview? Preview
    {
        get => (PagePreview?)GetValue(PreviewProperty);
        set => SetValue(PreviewProperty, value);
    }

    /// <inheritdoc cref="SelectedSlotIdProperty"/>
    public string? SelectedSlotId
    {
        get => (string?)GetValue(SelectedSlotIdProperty);
        set => SetValue(SelectedSlotIdProperty, value);
    }

    /// <inheritdoc cref="HoveredSlotIdProperty"/>
    public string? HoveredSlotId
    {
        get => (string?)GetValue(HoveredSlotIdProperty);
        private set => SetValue(HoveredSlotIdKey, value);
    }

    /// <inheritdoc cref="DropTargetSlotIdProperty"/>
    public string? DropTargetSlotId
    {
        get => (string?)GetValue(DropTargetSlotIdProperty);
        private set => SetValue(DropTargetSlotIdKey, value);
    }

    /// <inheritdoc cref="BackgroundProperty"/>
    public Brush? Background
    {
        get => (Brush?)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <inheritdoc cref="ShowGuidesProperty"/>
    public bool ShowGuides
    {
        get => (bool)GetValue(ShowGuidesProperty);
        set => SetValue(ShowGuidesProperty, value);
    }

    /// <inheritdoc cref="SpreadSideProperty"/>
    public PageSheetSide SpreadSide
    {
        get => (PageSheetSide)GetValue(SpreadSideProperty);
        set => SetValue(SpreadSideProperty, value);
    }

    /// <inheritdoc cref="ShowEmptySlotAffordanceProperty"/>
    public bool ShowEmptySlotAffordance
    {
        get => (bool)GetValue(ShowEmptySlotAffordanceProperty);
        set => SetValue(ShowEmptySlotAffordanceProperty, value);
    }

    /// <inheritdoc cref="IsInteractiveProperty"/>
    public bool IsInteractive
    {
        get => (bool)GetValue(IsInteractiveProperty);
        set => SetValue(IsInteractiveProperty, value);
    }

    /// <inheritdoc cref="MaxPreviewWidthProperty"/>
    public int MaxPreviewWidth
    {
        get => (int)GetValue(MaxPreviewWidthProperty);
        set => SetValue(MaxPreviewWidthProperty, value);
    }

    /// <inheritdoc cref="DebounceIntervalProperty"/>
    public TimeSpan DebounceInterval
    {
        get => (TimeSpan)GetValue(DebounceIntervalProperty);
        set => SetValue(DebounceIntervalProperty, value);
    }

    /// <inheritdoc cref="GestureDebounceIntervalProperty"/>
    public TimeSpan GestureDebounceInterval
    {
        get => (TimeSpan)GetValue(GestureDebounceIntervalProperty);
        set => SetValue(GestureDebounceIntervalProperty, value);
    }

    // ==================================================================== events

    /// <summary>A slot (or empty page area, with a null slot id) was clicked. Right-clicks come through here too.</summary>
    public event EventHandler<PageCanvasClickEventArgs>? SlotClicked;

    /// <summary>A slot was double-clicked — the gesture that enters crop mode (doc 09 §3.3).</summary>
    public event EventHandler<PageCanvasClickEventArgs>? SlotActivated;

    /// <summary>An empty amber slot was clicked: the "click to fill" affordance (R14, doc 09 §3.6).</summary>
    public event EventHandler<PageCanvasClickEventArgs>? EmptySlotActivated;

    /// <summary>
    /// A pointer drag passed the system drag threshold. Handlers that want a WPF drag-and-drop
    /// instead of pan deltas call <see cref="BeginDragDrop"/> here.
    /// </summary>
    public event EventHandler<PageCanvasDragEventArgs>? DragStarted;

    /// <summary>The pointer moved during a drag; deltas are supplied in normalized and slot units.</summary>
    public event EventHandler<PageCanvasDragEventArgs>? DragDelta;

    /// <summary>The drag ended — pointer-up, or capture lost with <c>Canceled</c> set.</summary>
    public event EventHandler<PageCanvasDragEventArgs>? DragCompleted;

    /// <summary>A drag-and-drop is passing over the canvas. Set <c>Effects</c> to accept it.</summary>
    public event EventHandler<PageCanvasDropEventArgs>? SlotDragOver;

    /// <summary>Something was dropped on the canvas (doc 09 §3.2's swap/replace table).</summary>
    public event EventHandler<PageCanvasDropEventArgs>? SlotDrop;

    /// <summary>The wheel turned over the canvas — crop zoom, or viewport zoom with <c>Ctrl</c>.</summary>
    public event EventHandler<PageCanvasWheelEventArgs>? Wheel;

    /// <summary>A new preview finished rendering and is on screen.</summary>
    public event EventHandler<PagePreview>? PreviewRendered;

    /// <summary>A render threw. The canvas keeps showing the previous preview.</summary>
    public event EventHandler<Exception>? RenderFailed;

    // ================================================================== geometry

    /// <summary>Slot id → rect in preview-bitmap pixels, straight from the renderer. Never recomputed.</summary>
    public IReadOnlyDictionary<string, SKRect> SlotRects => Preview?.Result.SlotRects ?? NoRects;

    /// <summary>Slot id → the rect the photo's visible pixels occupy (smaller than the slot when zoom &lt; 1).</summary>
    public IReadOnlyDictionary<string, SKRect> VisibleImageRects => Preview?.Result.VisibleImageRects ?? NoRects;

    /// <summary>Text slot id → rect in preview-bitmap pixels.</summary>
    public IReadOnlyDictionary<string, SKRect> TextSlotRects => Preview?.Result.TextSlotRects ?? NoRects;

    /// <summary>The slots on this page with no placement — the amber ones (R14).</summary>
    public IReadOnlyCollection<string> EmptySlotIds => _emptySlots;

    /// <summary>Slot ids in reading order (top to bottom, then left to right) — the <c>Tab</c> order.</summary>
    public IReadOnlyList<string> SlotIdsInReadingOrder =>
    [
        .. SlotRects.OrderBy(kv => Math.Round(kv.Value.Top, 3))
            .ThenBy(kv => Math.Round(kv.Value.Left, 3))
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key),
    ];

    /// <summary>Control pixels per preview-bitmap pixel; 0 when nothing is rendered.</summary>
    public double PreviewScale
    {
        get
        {
            var bitmap = _bitmap;
            if (bitmap is null || ActualWidth <= 0 || ActualHeight <= 0)
            {
                return 0;
            }

            return Math.Min(ActualWidth / bitmap.PixelWidth, ActualHeight / bitmap.PixelHeight);
        }
    }

    /// <summary>
    /// Where the preview bitmap is drawn inside the control — letterboxed and centered, except on a
    /// half of a facing pair, where it slides sideways until its spine edge meets the control's inner
    /// edge so the two halves butt-join into one spread. Every other coordinate helper derives from
    /// this, so the shift moves hit testing and drag deltas with it.
    /// </summary>
    public Rect PageRect
    {
        get
        {
            var bitmap = _bitmap;
            var scale = PreviewScale;
            if (bitmap is null || scale <= 0)
            {
                return Rect.Empty;
            }

            var width = bitmap.PixelWidth * scale;
            var height = bitmap.PixelHeight * scale;
            var rect = new Rect((ActualWidth - width) / 2, (ActualHeight - height) / 2, width, height);

            if (SpreadSide == PageSheetSide.None || Preview is not { } preview)
            {
                return rect;
            }

            var trim = preview.Geometry.TrimRect;
            var shift = SpreadSide == PageSheetSide.Left
                ? ActualWidth - (rect.X + (trim.Right * scale))
                : -(rect.X + (trim.Left * scale));

            return double.IsFinite(shift) ? Rect.Offset(rect, shift, 0) : rect;
        }
    }

    /// <summary>The trim box (the sheet's cut edge) in control pixels — normalized <c>[0,1]²</c> maps onto this.</summary>
    public Rect TrimRect =>
        Preview is { } preview ? BitmapRectToControl(preview.Geometry.TrimRect) : Rect.Empty;

    /// <summary>The bleed box in control pixels — the trim box plus the strip that gets cut away.</summary>
    public Rect MediaRect =>
        Preview is { } preview ? BitmapRectToControl(preview.Geometry.MediaRect) : Rect.Empty;

    /// <summary>
    /// The sheet as it is shown: the trim box, or the bleed box while <see cref="ShowGuides"/> is on,
    /// never crossing the spine on a half of a facing pair. Snapped to whole device-independent
    /// pixels so its hairline edge stays crisp. The page pixels are clipped to it.
    /// </summary>
    public Rect SheetRect
    {
        get
        {
            if (Preview is not { } preview || PreviewScale <= 0)
            {
                return PageRect;
            }

            var rect = BitmapRectToControl(ShowGuides ? preview.Geometry.MediaRect : preview.Geometry.TrimRect);
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
            {
                return PageRect;
            }

            // At the spine the two halves butt-join with no inner bleed (doc 12 "Spreads"), so the
            // sheet stops at trim on that side however the guides are set.
            if (SpreadSide != PageSheetSide.None)
            {
                var trim = BitmapRectToControl(preview.Geometry.TrimRect);
                rect = SpreadSide == PageSheetSide.Left
                    ? new Rect(rect.X, rect.Y, Math.Max(1, trim.Right - rect.X), rect.Height)
                    : new Rect(trim.X, rect.Y, Math.Max(1, rect.Right - trim.X), rect.Height);
            }

            var left = Math.Round(rect.X);
            var top = Math.Round(rect.Y);
            return new Rect(left, top, Math.Max(1, Math.Round(rect.Right) - left), Math.Max(1, Math.Round(rect.Bottom) - top));
        }
    }

    /// <summary>Converts a control point to preview-bitmap pixels, where <c>SlotRects</c> lives.</summary>
    public Point ControlToBitmap(Point point)
    {
        var scale = PreviewScale;
        var page = PageRect;
        return scale <= 0
            ? new Point(double.NaN, double.NaN)
            : new Point((point.X - page.X) / scale, (point.Y - page.Y) / scale);
    }

    /// <summary>Converts a preview-bitmap point to control pixels.</summary>
    public Point BitmapToControl(Point point)
    {
        var scale = PreviewScale;
        var page = PageRect;
        return scale <= 0
            ? new Point(double.NaN, double.NaN)
            : new Point(page.X + (point.X * scale), page.Y + (point.Y * scale));
    }

    /// <summary>Converts a preview-bitmap rect to control pixels.</summary>
    public Rect BitmapRectToControl(SKRect rect)
    {
        var scale = PreviewScale;
        var page = PageRect;
        if (scale <= 0)
        {
            return Rect.Empty;
        }

        return new Rect(
            page.X + (rect.Left * scale),
            page.Y + (rect.Top * scale),
            Math.Max(0, rect.Width * scale),
            Math.Max(0, rect.Height * scale));
    }

    /// <summary>
    /// Converts a control point to normalized page coordinates over the trim box (kernel §3), or null
    /// when nothing is rendered. Values outside <c>[0,1]</c> mean the point is off the page.
    /// </summary>
    public Point? ControlToNormalized(Point point)
    {
        if (Preview is not { } preview)
        {
            return null;
        }

        var trim = preview.Geometry.TrimRect;
        if (trim.Width <= 0 || trim.Height <= 0)
        {
            return null;
        }

        var bitmap = ControlToBitmap(point);
        return double.IsNaN(bitmap.X)
            ? null
            : new Point((bitmap.X - trim.Left) / trim.Width, (bitmap.Y - trim.Top) / trim.Height);
    }

    /// <summary>Converts normalized page coordinates to a control point, or null when nothing is rendered.</summary>
    public Point? NormalizedToControl(Point normalized)
    {
        if (Preview is not { } preview)
        {
            return null;
        }

        var trim = preview.Geometry.TrimRect;
        return BitmapToControl(new Point(
            trim.Left + (normalized.X * trim.Width),
            trim.Top + (normalized.Y * trim.Height)));
    }

    /// <summary>
    /// Converts a control-pixel movement to normalized page units — the unit slot geometry is stored
    /// in (doc 09 §3.7). Zero when nothing is rendered.
    /// </summary>
    public Vector ControlDeltaToNormalized(Vector delta)
    {
        if (Preview is not { } preview)
        {
            return default;
        }

        var scale = PreviewScale;
        var trim = preview.Geometry.TrimRect;
        if (scale <= 0 || trim.Width <= 0 || trim.Height <= 0)
        {
            return default;
        }

        return new Vector(delta.X / scale / trim.Width, delta.Y / scale / trim.Height);
    }

    /// <summary>
    /// Converts a control-pixel movement to the slot's own width/height units — exactly what a crop
    /// pan adds to <c>CropState.OffsetX/OffsetY</c> (doc 09 §3.3). Zero when the slot is unknown.
    /// </summary>
    /// <param name="slotId">The slot being panned.</param>
    /// <param name="delta">Pointer movement in control pixels.</param>
    public Vector ControlDeltaToSlotUnits(string? slotId, Vector delta)
    {
        if (slotId is null || !SlotRects.TryGetValue(slotId, out var rect))
        {
            return default;
        }

        var scale = PreviewScale;
        if (scale <= 0 || rect.Width <= 0 || rect.Height <= 0)
        {
            return default;
        }

        return new Vector(delta.X / scale / rect.Width, delta.Y / scale / rect.Height);
    }

    /// <summary>The slot under a control point, or null. Overlapping slots resolve to the smallest (topmost).</summary>
    public string? SlotAt(Point controlPoint)
    {
        var bitmap = ControlToBitmap(controlPoint);
        if (double.IsNaN(bitmap.X))
        {
            return null;
        }

        var x = (float)bitmap.X;
        var y = (float)bitmap.Y;

        string? best = null;
        var bestArea = double.MaxValue;

        foreach (var (id, rect) in SlotRects)
        {
            if (x < rect.Left || x > rect.Right || y < rect.Top || y > rect.Bottom)
            {
                continue;
            }

            double area = rect.Width * rect.Height;
            if (area < bestArea || (area == bestArea && string.CompareOrdinal(id, best) < 0))
            {
                best = id;
                bestArea = area;
            }
        }

        return best;
    }

    /// <summary>The text slot under a control point, or null — for layout-override mode (doc 09 §3.7).</summary>
    public string? TextSlotAt(Point controlPoint)
    {
        var bitmap = ControlToBitmap(controlPoint);
        if (double.IsNaN(bitmap.X))
        {
            return null;
        }

        foreach (var (id, rect) in TextSlotRects)
        {
            if (rect.Contains((float)bitmap.X, (float)bitmap.Y))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>The slot's rect in control pixels, or <see cref="Rect.Empty"/>.</summary>
    public Rect SlotRectInControl(string? slotId) =>
        slotId is not null && SlotRects.TryGetValue(slotId, out var rect) ? BitmapRectToControl(rect) : Rect.Empty;

    /// <summary>The rect the slot's visible photo pixels occupy in control pixels, or <see cref="Rect.Empty"/>.</summary>
    public Rect VisibleImageRectInControl(string? slotId) =>
        slotId is not null && VisibleImageRects.TryGetValue(slotId, out var rect) ? BitmapRectToControl(rect) : Rect.Empty;

    /// <summary>The slot's rect in normalized page coordinates, or null.</summary>
    public Rect? SlotRectNormalized(string? slotId)
    {
        if (slotId is null || Preview is not { } preview || !SlotRects.TryGetValue(slotId, out var rect))
        {
            return null;
        }

        var trim = preview.Geometry.TrimRect;
        if (trim.Width <= 0 || trim.Height <= 0)
        {
            return null;
        }

        return new Rect(
            (rect.Left - trim.Left) / trim.Width,
            (rect.Top - trim.Top) / trim.Height,
            rect.Width / trim.Width,
            rect.Height / trim.Height);
    }

    /// <summary>True when the slot exists on this page and holds no photo (R14).</summary>
    public bool IsSlotEmpty(string? slotId) => slotId is not null && _emptySlots.Contains(slotId);

    /// <summary>
    /// Moves the selection to the next slot in reading order and returns its id — the <c>Tab</c>
    /// gesture of doc 09 §5.
    /// </summary>
    /// <param name="forward">False to step backwards.</param>
    public string? SelectNextSlot(bool forward = true)
    {
        var order = new List<string>(SlotIdsInReadingOrder);
        if (order.Count == 0)
        {
            return null;
        }

        var index = SelectedSlotId is null ? -1 : order.IndexOf(SelectedSlotId);
        var next = index < 0
            ? (forward ? 0 : order.Count - 1)
            : ((index + (forward ? 1 : -1)) % order.Count + order.Count) % order.Count;

        SelectedSlotId = order[next];
        return SelectedSlotId;
    }

    // =================================================================== rendering

    /// <summary>
    /// Asks for a fresh render. Requests are coalesced by the debounce timer, so calling this on
    /// every pointer move during a drag is the intended usage.
    /// </summary>
    /// <param name="immediate">True to skip the debounce — a page change, not a gesture tick.</param>
    public void RequestRender(bool immediate = false)
    {
        if (Renderer is null || Page is null)
        {
            return;
        }

        if (immediate)
        {
            _debounce.Stop();
            StartRender();
            return;
        }

        _debounce.Interval = _gestureDepth > 0 ? GestureDebounceInterval : DebounceInterval;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>
    /// Marks the start of a continuous gesture: re-renders are throttled to
    /// <see cref="GestureDebounceInterval"/> until the matching <see cref="EndInteractiveGesture"/>,
    /// which then renders once more so the final state is exact.
    /// </summary>
    public void BeginInteractiveGesture() => _gestureDepth++;

    /// <inheritdoc cref="BeginInteractiveGesture"/>
    public void EndInteractiveGesture()
    {
        if (_gestureDepth > 0)
        {
            _gestureDepth--;
        }

        if (_gestureDepth == 0)
        {
            RequestRender(immediate: true);
        }
    }

    private void StartRender()
    {
        if (Renderer is not { } renderer || Page is not { } page)
        {
            return;
        }

        if (_renderInFlight)
        {
            _renderPending = true;
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        if (width < 16 || height < 16)
        {
            return;
        }

        var cap = Math.Max(64, MaxPreviewWidth);
        if (width > cap)
        {
            height = Math.Max(16, (int)Math.Round(height * (cap / (double)width)));
            width = cap;
        }

        _renderCts?.Cancel();
        _renderCts?.Dispose();
        var cts = new CancellationTokenSource();
        _renderCts = cts;
        _renderInFlight = true;

        _ = RenderAsync(renderer, page, width, height, cts);
    }

    private async Task RenderAsync(
        PageCanvasRenderer renderer, CorePage page, int width, int height, CancellationTokenSource cts)
    {
        try
        {
            var token = cts.Token;
            var rendered = await Task.Run<(PagePreview? Preview, BitmapSource? Bitmap)>(
                () =>
                {
                    var drawn = renderer(new PageCanvasRenderContext(page, width, height, token));

                    // Blitting on the worker keeps the UI thread free; PixelBridge freezes the
                    // bitmap, so it is safe to hand across.
                    return drawn is null ? (null, null) : (drawn, PixelBridge.ToBitmap(drawn.Image));
                },
                token).ConfigureAwait(true);

            if (token.IsCancellationRequested || rendered.Preview is not { } preview || !ReferenceEquals(page, Page))
            {
                return;
            }

            _bitmap = rendered.Bitmap;
            _settingPreview = true;
            try
            {
                Preview = preview;
            }
            finally
            {
                _settingPreview = false;
            }

            RebuildEmptySlots();
            InvalidateVisual();
            PreviewRendered?.Invoke(this, preview);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer render; the previous preview stays on screen.
        }
        catch (Exception ex)
        {
            RenderFailed?.Invoke(this, ex);
        }
        finally
        {
            _renderInFlight = false;
            if (ReferenceEquals(_renderCts, cts))
            {
                _renderCts = null;
            }

            cts.Dispose();

            if (_renderPending)
            {
                _renderPending = false;
                RequestRender();
            }
        }
    }

    private static void OnPageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (PageCanvas)d;
        canvas.HoveredSlotId = null;
        canvas.DropTargetSlotId = null;
        canvas.RebuildEmptySlots();
        canvas.RequestRender(immediate: true);
        canvas.InvalidateVisual();
    }

    private static void OnPreviewChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (PageCanvas)d;
        if (canvas._settingPreview)
        {
            return;
        }

        // Set from outside — adopt its pixels as they are.
        canvas._bitmap = e.NewValue is PagePreview preview ? PixelBridge.ToBitmap(preview.Image) : null;
        canvas.RebuildEmptySlots();
        canvas.InvalidateVisual();
    }

    private void RebuildEmptySlots()
    {
        _emptySlots.Clear();
        if (Preview is not { } preview)
        {
            return;
        }

        var page = Page;
        foreach (var slotId in preview.Result.SlotRects.Keys)
        {
            var empty = page is not null
                ? page.PlacementFor(slotId) is not { } placement || string.IsNullOrEmpty(placement.PhotoId)
                : !preview.Result.VisibleImageRects.ContainsKey(slotId);

            if (empty)
            {
                _emptySlots.Add(slotId);
            }
        }
    }

    /// <inheritdoc/>
    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        RequestRender();
    }

    // ==================================================================== drawing

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);
        base.OnRender(dc);

        var bounds = new Rect(RenderSize);
        dc.DrawRectangle(Background ?? PaletteBrush("PageTableBrush", "#FF33383E"), null, bounds);

        var bitmap = _bitmap;
        if (bitmap is null)
        {
            return;
        }

        var page = PageRect;
        if (page.IsEmpty || page.Width <= 0 || page.Height <= 0)
        {
            return;
        }

        var sheet = SheetRect;
        if (sheet.IsEmpty || sheet.Width <= 1 || sheet.Height <= 1)
        {
            sheet = page;
        }

        DrawSheetShadow(dc, sheet);

        dc.PushClip(new RectangleGeometry(sheet));
        dc.DrawImage(bitmap, page);
        DrawGutterShading(dc, sheet);

        if (ShowGuides)
        {
            DrawGuides(dc);
        }

        if (IsInteractive)
        {
            DrawInteractionLayer(dc);
        }

        dc.Pop();

        DrawSheetEdge(dc, sheet);

        if (ShowGuides)
        {
            DrawGuideLegend(dc, sheet);
        }
    }

    /// <summary>
    /// Hover, drop-target and selection chrome, all three spoken in the editor's one selection
    /// language (<see cref="SelectionChrome"/>): a crisp accent frame with a dark hairline either
    /// side of it, and — on the selection alone — small square corner grips. The grips mark the crop
    /// box the way every image editor marks one; the eight-grip form is reserved for layout override
    /// mode, where all eight actually resize something.
    /// </summary>
    private void DrawInteractionLayer(DrawingContext dc)
    {
        if (ShowEmptySlotAffordance)
        {
            foreach (var slotId in _emptySlots)
            {
                DrawEmptySlot(dc, slotId);
            }
        }

        var contrast = PaletteBrush("ScrimBrush", "#99000000");

        var hovered = HoveredSlotId;
        if (hovered is not null && hovered != SelectedSlotId && hovered != DropTargetSlotId && !_emptySlots.Contains(hovered))
        {
            SelectionChrome.DrawFrame(
                dc, SlotRectInControl(hovered), PaletteBrush("AccentWashStrongBrush", "#4A4C8DF5"), contrast, 1.5);
        }

        if (DropTargetSlotId is { } dropTarget)
        {
            SelectionChrome.DrawFrame(
                dc, SlotRectInControl(dropTarget), PaletteBrush("FocusRingBrush", "#FF7FB0FF"), contrast, 2);
        }

        if (SelectedSlotId is { } selected && selected != DropTargetSlotId)
        {
            var rect = SlotRectInControl(selected);
            SelectionChrome.DrawFrame(dc, rect, PaletteBrush("SelectionBorderBrush", "#CC4C8DF5"), contrast, 2);
            SelectionChrome.DrawGrips(
                dc,
                rect,
                PaletteBrush("FocusHandleBrush", "#FFECEFF3"),
                PaletteBrush("FocusHandleBorderBrush", "#CC000000"),
                cornersOnly: true);
        }
    }

    /// <summary>
    /// The soft shadow that seats the sheet on the table: concentric rounded rects whose alpha falls
    /// off quadratically, offset a little downwards so the light reads as coming from above. The
    /// spine edge of a facing pair gets none — the two sheets meet there.
    /// </summary>
    private void DrawSheetShadow(DrawingContext dc, Rect sheet)
    {
        const int Layers = 8;
        const double Spread = 2.6;

        var shadow = PaletteColor("PageShadowColor", "#FF04060A");
        var noLeft = SpreadSide == PageSheetSide.Right;
        var noRight = SpreadSide == PageSheetSide.Left;

        for (var i = Layers; i >= 1; i--)
        {
            var grow = i * Spread;
            var fade = 1.0 - ((i - 1) / (double)Layers);
            var alpha = (byte)Math.Clamp(Math.Round(96 * fade * fade), 1, 255);

            var rect = new Rect(
                sheet.X - (noLeft ? 0 : grow),
                sheet.Y - (grow * 0.35),
                sheet.Width + (noLeft ? 0 : grow) + (noRight ? 0 : grow),
                sheet.Height + (grow * 0.35) + (grow * 0.9));

            var brush = new SolidColorBrush(Color.FromArgb(alpha, shadow.R, shadow.G, shadow.B));
            brush.Freeze();
            dc.DrawRoundedRectangle(brush, null, rect, grow * 0.5, grow * 0.5);
        }
    }

    /// <summary>
    /// The cut line: a dark hairline just outside the sheet so it never disappears into a light
    /// table, and the crisp <c>PageEdgeBrush</c> line on the trim itself. With guides on the sheet
    /// edge is the <i>bleed</i> box, so the bright line is left to the trim guide and only the dark
    /// hairline seats the sheet — one white line on screen, always meaning "this is the cut".
    /// </summary>
    private void DrawSheetEdge(DrawingContext dc, Rect sheet)
    {
        var shade = new Pen(PaletteBrush("PageEdgeShadeBrush", "#59000000"), 1);
        shade.Freeze();
        dc.DrawRectangle(null, shade, Inset(sheet, -0.5));

        if (ShowGuides)
        {
            return;
        }

        var edge = new Pen(PaletteBrush("PageEdgeBrush", "#E8DCE3EA"), 1);
        edge.Freeze();
        dc.DrawRectangle(null, edge, Inset(sheet, 0.5));
    }

    /// <summary>
    /// A spread's gutter: the page darkens as it turns into the binding, which — with the two trim
    /// hairlines meeting at the seam — is what makes the centre line legible (doc 09 §3.1).
    /// </summary>
    private void DrawGutterShading(DrawingContext dc, Rect sheet)
    {
        if (SpreadSide == PageSheetSide.None)
        {
            return;
        }

        var width = Math.Min(sheet.Width * 0.06, Math.Max(6, sheet.Width * 0.045));
        if (width < 3)
        {
            return;
        }

        var left = SpreadSide == PageSheetSide.Left;
        var shadow = PaletteColor("PageShadowColor", "#FF04060A");
        var brush = new LinearGradientBrush
        {
            StartPoint = left ? new Point(1, 0) : new Point(0, 0),
            EndPoint = left ? new Point(0, 0) : new Point(1, 0),
            GradientStops =
            [
                new GradientStop(Color.FromArgb(0x8A, shadow.R, shadow.G, shadow.B), 0),
                new GradientStop(Color.FromArgb(0x38, shadow.R, shadow.G, shadow.B), 0.45),
                new GradientStop(Color.FromArgb(0x00, shadow.R, shadow.G, shadow.B), 1),
            ],
        };

        brush.Freeze();
        dc.DrawRectangle(
            brush,
            null,
            new Rect(left ? sheet.Right - width : sheet.X, sheet.Y, width, sheet.Height));
    }

    /// <summary>
    /// Bleed, trim and safe-margin guides (kernel §3), taken from the geometry the renderer actually
    /// drew with so they can never disagree with the pixels underneath.
    /// </summary>
    private void DrawGuides(DrawingContext dc)
    {
        if (Preview is not { } preview || PreviewScale <= 0)
        {
            return;
        }

        DrawGuideRect(dc, BitmapRectToControl(preview.Geometry.MediaRect), PaletteBrush("GuideBleedBrush", "#FFE0685E"), dashed: true);
        DrawGuideRect(dc, BitmapRectToControl(preview.Geometry.TrimRect), PaletteBrush("GuideTrimBrush", "#FFDCE3EA"), dashed: false);
        DrawGuideRect(dc, BitmapRectToControl(preview.Geometry.SafeRect), PaletteBrush("GuideSafeBrush", "#FF56C79F"), dashed: true);
    }

    private void DrawGuideRect(DrawingContext dc, Rect rect, Brush brush, bool dashed)
    {
        if (rect.IsEmpty || rect.Width <= 2 || rect.Height <= 2)
        {
            return;
        }

        // The dark backing line keeps a guide readable where it crosses a bright photograph.
        var backing = new Pen(PaletteBrush("ScrimBrush", "#99000000"), 2);
        backing.Freeze();
        dc.DrawRectangle(null, backing, Inset(rect, 0.5));

        var pen = new Pen(brush, 1);
        if (dashed)
        {
            pen.DashStyle = new DashStyle([4, 3], 0);
            pen.DashCap = PenLineCap.Flat;
        }

        pen.Freeze();
        dc.DrawRectangle(null, pen, Inset(rect, 0.5));
    }

    /// <summary>
    /// Names the three guides, on the table just under the sheet where it covers no photograph —
    /// falling back to inside the sheet only when the sheet all but fills the canvas.
    /// </summary>
    private void DrawGuideLegend(DrawingContext dc, Rect sheet)
    {
        if (sheet.Width < 260 || sheet.Height < 150)
        {
            return;
        }

        (string Label, Brush Brush)[] entries =
        [
            ("Bleed", PaletteBrush("GuideBleedBrush", "#FFE0685E")),
            ("Trim", PaletteBrush("GuideTrimBrush", "#FFDCE3EA")),
            ("Safe", PaletteBrush("GuideSafeBrush", "#FF56C79F")),
        ];

        const double Pad = 8;
        const double Swatch = 14;
        const double Gap = 6;

        var labels = entries.Select(e => FormatText(e.Label, PaletteBrush("TextPrimaryBrush", "#FFECEFF3"), 10.5, FontWeights.SemiBold)).ToList();
        var width = (Pad * 2) + labels.Sum(l => l.Width) + (entries.Length * (Swatch + Gap)) + ((entries.Length - 1) * 12);
        var height = labels.Max(l => l.Height) + 10;

        var below = sheet.Bottom + 10 + height <= ActualHeight;
        var pill = new Rect(
            sheet.X + (below ? 0 : 12),
            below ? sheet.Bottom + 10 : sheet.Bottom - height - 12,
            width,
            height);

        dc.DrawRoundedRectangle(
            PaletteBrush("ScrimHeavyBrush", "#CC07080A"), null, pill, height / 2, height / 2);

        var x = pill.X + Pad;
        for (var i = 0; i < entries.Length; i++)
        {
            var pen = new Pen(entries[i].Brush, 2);
            pen.Freeze();
            var y = pill.Y + (pill.Height / 2);
            dc.DrawLine(pen, new Point(x, y), new Point(x + Swatch, y));

            x += Swatch + Gap;
            dc.DrawText(labels[i], new Point(x, pill.Y + ((pill.Height - labels[i].Height) / 2)));
            x += labels[i].Width + 12;
        }
    }

    private void DrawEmptySlot(DrawingContext dc, string slotId)
    {
        var rect = SlotRectInControl(slotId);
        if (rect.IsEmpty || rect.Width <= 2 || rect.Height <= 2)
        {
            return;
        }

        var hovered = string.Equals(slotId, HoveredSlotId, StringComparison.Ordinal);
        var amber = PaletteBrush("AmberFlagBrush", "#FFFFB300");

        dc.DrawRectangle(
            hovered ? PaletteBrush("AmberFlagWashBrush", "#1FFFB300") : null,
            null,
            rect);

        var pen = new Pen(amber, 2)
        {
            DashStyle = new DashStyle(new double[] { 4, 3 }, 0),
            DashCap = PenLineCap.Flat,
        };
        dc.DrawRectangle(null, pen, Inset(rect, 1));

        // The photo-plus glyph is authored on a 24 × 24 grid in Themes/Icons.xaml.
        var glyphSize = Math.Min(30, Math.Min(rect.Width, rect.Height) * 0.34);
        var textHeight = hovered ? 24.0 : 0.0;
        if (glyphSize >= 12 && TryFindResource("Icon.PhotoPlus") is Geometry glyph)
        {
            var scale = glyphSize / 24.0;
            var origin = new Point(
                rect.X + ((rect.Width - glyphSize) / 2),
                rect.Y + ((rect.Height - glyphSize - textHeight) / 2));

            dc.PushTransform(new TranslateTransform(origin.X, origin.Y));
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawGeometry(null, new Pen(amber, 1.4 / scale) { LineJoin = PenLineJoin.Round }, glyph);
            dc.Pop();
            dc.Pop();
        }

        if (!hovered || rect.Width < 96 || rect.Height < 64)
        {
            return;
        }

        var label = FormatText("Click to fill", PaletteBrush("AmberFlagTextBrush", "#FF120C00"), 10.5, FontWeights.SemiBold);
        var padding = new Size(10, 4);
        var pill = new Rect(
            rect.X + ((rect.Width - label.Width - (padding.Width * 2)) / 2),
            rect.Y + ((rect.Height + glyphSize) / 2) - (textHeight / 2) + 2,
            label.Width + (padding.Width * 2),
            label.Height + (padding.Height * 2));

        dc.DrawRoundedRectangle(amber, null, pill, pill.Height / 2, pill.Height / 2);
        dc.DrawText(label, new Point(pill.X + padding.Width, pill.Y + padding.Height));
    }

    private FormattedText FormatText(string text, Brush brush, double size, FontWeight weight)
    {
        var family = TryFindResource("AppFontFamily") as FontFamily ?? new FontFamily("Segoe UI");
        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal),
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    /// <summary>
    /// Resolves a palette brush. The literal is a last-resort fallback for a canvas hosted outside the
    /// app's resource scope (the XAML designer); inside the app the dictionary always wins, so colour
    /// still lives in <c>Themes/Palette.xaml</c> alone.
    /// </summary>
    private Brush PaletteBrush(string key, string fallback)
    {
        if (TryFindResource(key) is Brush brush)
        {
            return brush;
        }

        var solid = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
        solid.Freeze();
        return solid;
    }

    /// <summary>
    /// Resolves a palette <see cref="Color"/> — for the shadow ramps, which need one hue at many
    /// alphas and so cannot use a single brush resource.
    /// </summary>
    private Color PaletteColor(string key, string fallback) => TryFindResource(key) switch
    {
        Color color => color,
        SolidColorBrush brush => brush.Color,
        _ => (Color)ColorConverter.ConvertFromString(fallback),
    };

    private static Rect Inset(Rect rect, double amount)
    {
        var width = rect.Width - (amount * 2);
        var height = rect.Height - (amount * 2);
        return width <= 0 || height <= 0
            ? rect
            : new Rect(rect.X + amount, rect.Y + amount, width, height);
    }

    // ================================================================= interaction

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);

        if (!IsInteractive)
        {
            return;
        }

        var point = e.GetPosition(this);
        UpdateHover(point);

        if (!_pressed)
        {
            return;
        }

        if (!_dragging)
        {
            var moved = point - _pressPoint;
            if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _dragging = true;
            var start = DragArgs(point, point - _pressPoint, point - _pressPoint);
            DragStarted?.Invoke(this, start);
            _lastPoint = point;

            if (start.CancelPointerDrag)
            {
                CancelPointerDrag(raiseCompleted: false);
            }

            return;
        }

        var delta = point - _lastPoint;
        _lastPoint = point;
        DragDelta?.Invoke(this, DragArgs(point, delta, point - _pressPoint));
    }

    /// <inheritdoc/>
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        HoveredSlotId = null;
        Cursor = null;
    }

    /// <inheritdoc/>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseLeftButtonDown(e);

        if (!IsInteractive)
        {
            return;
        }

        Focus();
        var point = e.GetPosition(this);
        var slotId = SlotAt(point);

        _pressPoint = point;
        _lastPoint = point;
        _pressSlotId = slotId;
        _pressed = true;
        _dragging = false;
        CaptureMouse();

        var args = ClickArgs(point, MouseButton.Left, e.ClickCount);

        if (e.ClickCount >= 2)
        {
            SlotActivated?.Invoke(this, args);
            e.Handled = true;
            return;
        }

        SlotClicked?.Invoke(this, args);
        if (!args.Handled)
        {
            SelectedSlotId = slotId;
        }

        if (slotId is not null && _emptySlots.Contains(slotId))
        {
            EmptySlotActivated?.Invoke(this, args);
        }

        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseLeftButtonUp(e);

        if (!_pressed)
        {
            return;
        }

        var point = e.GetPosition(this);
        var dragging = _dragging;
        var total = point - _pressPoint;

        ReleaseCapture();

        if (dragging)
        {
            DragCompleted?.Invoke(this, DragArgs(point, point - _lastPoint, total));
        }
    }

    /// <inheritdoc/>
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseRightButtonDown(e);

        if (!IsInteractive)
        {
            return;
        }

        Focus();
        var point = e.GetPosition(this);
        var args = ClickArgs(point, MouseButton.Right, e.ClickCount);
        SlotClicked?.Invoke(this, args);

        if (!args.Handled && args.SlotId is not null)
        {
            SelectedSlotId = args.SlotId;
        }
    }

    /// <inheritdoc/>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_pressed || _dragging)
        {
            CancelPointerDrag(raiseCompleted: true);
        }
    }

    /// <inheritdoc/>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseWheel(e);

        if (!IsInteractive || Wheel is null)
        {
            return;
        }

        var point = e.GetPosition(this);
        var slotId = SlotAt(point);
        var args = new PageCanvasWheelEventArgs
        {
            Page = Page,
            SlotId = slotId,
            IsSlotEmpty = slotId is not null && _emptySlots.Contains(slotId),
            ControlPoint = point,
            BitmapPoint = ControlToBitmap(point),
            NormalizedPoint = ControlToNormalized(point) ?? new Point(double.NaN, double.NaN),
            Modifiers = Keyboard.Modifiers,
            Delta = e.Delta,
            Steps = e.Delta / (double)Mouse.MouseWheelDeltaForOneLine,
        };

        Wheel.Invoke(this, args);
        e.Handled = args.Handled;
    }

    /// <summary>
    /// Hands the gesture to WPF drag-and-drop: releases the pointer capture the canvas took, stops
    /// tracking pan deltas and runs the drag loop. Call this from a <see cref="DragStarted"/> handler
    /// that is moving a photo rather than panning a crop.
    /// </summary>
    /// <param name="data">The payload — typically the photo id or a view model.</param>
    /// <param name="allowed">Effects the source permits.</param>
    /// <returns>The effect the drop actually had.</returns>
    public DragDropEffects BeginDragDrop(object data, DragDropEffects allowed = DragDropEffects.Move)
    {
        ArgumentNullException.ThrowIfNull(data);
        CancelPointerDrag(raiseCompleted: false);
        return DragDrop.DoDragDrop(this, data, allowed);
    }

    /// <inheritdoc/>
    protected override void OnDragOver(DragEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnDragOver(e);
        HandleDragOver(e);
    }

    /// <inheritdoc/>
    protected override void OnDragEnter(DragEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnDragEnter(e);
        HandleDragOver(e);
    }

    /// <inheritdoc/>
    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        DropTargetSlotId = null;
    }

    /// <inheritdoc/>
    protected override void OnDrop(DragEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnDrop(e);

        var args = DropArgs(e);
        SlotDrop?.Invoke(this, args);
        e.Effects = args.Effects;
        e.Handled = true;
        DropTargetSlotId = null;
    }

    private void HandleDragOver(DragEventArgs e)
    {
        var args = DropArgs(e);
        SlotDragOver?.Invoke(this, args);
        e.Effects = args.Effects;
        e.Handled = true;
        DropTargetSlotId = args.Effects == DragDropEffects.None ? null : args.SlotId;
    }

    private PageCanvasDropEventArgs DropArgs(DragEventArgs e)
    {
        var point = e.GetPosition(this);
        var slotId = SlotAt(point);
        return new PageCanvasDropEventArgs
        {
            Page = Page,
            SlotId = slotId,
            IsSlotEmpty = slotId is not null && _emptySlots.Contains(slotId),
            Data = e.Data,
            ControlPoint = point,
            NormalizedPoint = ControlToNormalized(point) ?? new Point(double.NaN, double.NaN),
            Modifiers = Keyboard.Modifiers,
            AllowedEffects = e.AllowedEffects,
            Effects = DragDropEffects.None,
        };
    }

    private void UpdateHover(Point point)
    {
        var slotId = SlotAt(point);
        if (!string.Equals(slotId, HoveredSlotId, StringComparison.Ordinal))
        {
            HoveredSlotId = slotId;
        }

        Cursor = slotId is not null && _emptySlots.Contains(slotId) ? Cursors.Hand : null;
    }

    private void CancelPointerDrag(bool raiseCompleted)
    {
        var wasDragging = _dragging;
        _pressed = false;
        _dragging = false;

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        if (raiseCompleted && wasDragging)
        {
            DragCompleted?.Invoke(this, DragArgs(_lastPoint, default, _lastPoint - _pressPoint, canceled: true));
        }
    }

    private void ReleaseCapture()
    {
        _pressed = false;
        _dragging = false;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
    }

    private PageCanvasClickEventArgs ClickArgs(Point point, MouseButton button, int clickCount)
    {
        var slotId = SlotAt(point);
        return new PageCanvasClickEventArgs
        {
            Page = Page,
            SlotId = slotId,
            IsSlotEmpty = slotId is not null && _emptySlots.Contains(slotId),
            ControlPoint = point,
            BitmapPoint = ControlToBitmap(point),
            NormalizedPoint = ControlToNormalized(point) ?? new Point(double.NaN, double.NaN),
            Modifiers = Keyboard.Modifiers,
            Button = button,
            ClickCount = clickCount,
        };
    }

    private PageCanvasDragEventArgs DragArgs(Point point, Vector delta, Vector total, bool canceled = false)
    {
        var slotId = SlotAt(point);
        return new PageCanvasDragEventArgs
        {
            Page = Page,
            SlotId = slotId,
            IsSlotEmpty = slotId is not null && _emptySlots.Contains(slotId),
            ControlPoint = point,
            BitmapPoint = ControlToBitmap(point),
            NormalizedPoint = ControlToNormalized(point) ?? new Point(double.NaN, double.NaN),
            Modifiers = Keyboard.Modifiers,
            OriginSlotId = _pressSlotId,
            NormalizedDelta = ControlDeltaToNormalized(delta),
            TotalNormalizedDelta = ControlDeltaToNormalized(total),
            SlotDelta = ControlDeltaToSlotUnits(_pressSlotId, delta),
            TotalSlotDelta = ControlDeltaToSlotUnits(_pressSlotId, total),
            Canceled = canceled,
        };
    }
}
