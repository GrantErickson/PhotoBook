using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CoreRect = PhotoBook.Core.Model.Rect;
using FocusKind = PhotoBook.Core.Model.FocusKind;

namespace PhotoBook.App.Controls;

/// <summary>What the surface needs of one Focus Region to draw, hit-test and reshape it.</summary>
public interface IFocusRegionShape : INotifyPropertyChanged
{
    /// <summary>The region in normalized image coordinates <c>[0,1] × [0,1]</c>, origin top-left.</summary>
    CoreRect Rect { get; }

    /// <summary>Which detector — or the user — proposed it; decides the colour and whether it is reshapeable.</summary>
    FocusKind Kind { get; }

    /// <summary>Importance in <c>[0,1]</c>; 0 means disabled but kept (doc 09 §2.2).</summary>
    double Weight { get; }

    /// <summary>True only for <see cref="FocusKind.User"/> regions — detected boxes are analysis truth.</summary>
    bool IsEditable { get; }

    /// <summary>The chip drawn on the region: "You", "Face", a person's name.</summary>
    string Label { get; }
}

/// <summary>Which of the eight handles, or the body, a gesture grabbed.</summary>
public enum FocusHandle
{
    /// <summary>No handle — the pointer is over empty photo.</summary>
    None,

    /// <summary>The region body: a move.</summary>
    Body,

    /// <summary>Top-left corner.</summary>
    NorthWest,

    /// <summary>Top edge.</summary>
    North,

    /// <summary>Top-right corner.</summary>
    NorthEast,

    /// <summary>Right edge.</summary>
    East,

    /// <summary>Bottom-right corner.</summary>
    SouthEast,

    /// <summary>Bottom edge.</summary>
    South,

    /// <summary>Bottom-left corner.</summary>
    SouthWest,

    /// <summary>Left edge.</summary>
    West,
}

/// <summary>A new region the user just drew.</summary>
/// <param name="rect">The region in normalized image coordinates.</param>
public sealed class FocusRegionDrawnEventArgs(CoreRect rect) : EventArgs
{
    /// <summary>The new region's rect.</summary>
    public CoreRect Rect { get; } = rect;
}

/// <summary>One frame of a move/resize gesture, or its end.</summary>
/// <param name="region">The shape being reshaped.</param>
/// <param name="rect">The rect as of this frame.</param>
/// <param name="startRect">The rect when the gesture began — what undo restores.</param>
/// <param name="handle">Which handle drives the gesture.</param>
/// <param name="canceled">True when the user pressed Esc; the caller should restore <paramref name="startRect"/>.</param>
public sealed class FocusRegionEditEventArgs(
    IFocusRegionShape region, CoreRect rect, CoreRect startRect, FocusHandle handle, bool canceled = false)
    : EventArgs
{
    /// <summary>The shape being reshaped.</summary>
    public IFocusRegionShape Region { get; } = region;

    /// <summary>The rect as of this frame.</summary>
    public CoreRect Rect { get; } = rect;

    /// <summary>The rect the gesture started from.</summary>
    public CoreRect StartRect { get; } = startRect;

    /// <summary>Which handle drives the gesture; <see cref="FocusHandle.Body"/> for a move.</summary>
    public FocusHandle Handle { get; } = handle;

    /// <summary>True when the gesture was abandoned with Esc.</summary>
    public bool Canceled { get; } = canceled;
}

/// <summary>
/// The Focus Region editing surface of doc 09 §2.2 (R25): a photo with its Focus Regions overlaid,
/// colour-coded by kind, where the user draws a new region by dragging on empty photo and moves or
/// resizes their own regions with eight handles.
///
/// <para>
/// Precision is the whole point — this is what makes smart-crop obey the user — so the geometry is
/// exact by construction. The photo is laid out letterboxed inside the control and every coordinate
/// the surface emits is normalized to that <b>image</b> rect, never to the control, so a region is
/// identical whatever the window size. Edges snap (6 device-independent pixels) to the image bounds,
/// the centre lines and the thirds, and every rect is clamped inside <c>[0,1]²</c> before it leaves
/// here, so a region can never describe pixels that do not exist.
/// </para>
///
/// <para>
/// Detected regions (person, face, saliency) are drawn but never reshaped: they are what analysis
/// measured (doc 09 §2.2). They can be selected — so they can be disabled or weighted — and that is
/// all. The surface itself owns no model state: it raises <see cref="RegionDrawn"/> and the
/// <see cref="RegionEditStarted"/>/<see cref="RegionEditDelta"/>/<see cref="RegionEditCompleted"/>
/// trio, and the view model turns those into undoable commands.
/// </para>
/// </summary>
public sealed class FocusRegionSurface : FrameworkElement
{
    private const double HandleSize = 9;
    private const double SnapPixels = 6;
    private const double MinimumSize = 0.02;
    private const double DrawThreshold = 0.012;

    private static readonly double[] SnapTargets = [0, 1.0 / 3.0, 0.5, 2.0 / 3.0, 1];

    private Rect _imageRect;
    private IFocusRegionShape? _active;
    private CoreRect _activeStart;
    private CoreRect _activeRect;
    private FocusHandle _activeHandle = FocusHandle.None;
    private Point _grabNormalized;
    private bool _drawing;
    private Point _drawAnchor;
    private CoreRect _drawRect;
    private CoreRect _drawRaw;

    /// <summary>The photo to edit over, already adjusted and at preview resolution.</summary>
    public static readonly DependencyProperty ImageProperty = DependencyProperty.Register(
        nameof(Image), typeof(ImageSource), typeof(FocusRegionSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The regions to draw; an <see cref="INotifyCollectionChanged"/> collection stays live.</summary>
    public static readonly DependencyProperty RegionsProperty = DependencyProperty.Register(
        nameof(Regions), typeof(IEnumerable), typeof(FocusRegionSurface),
        new FrameworkPropertyMetadata(null, OnRegionsChanged));

    /// <summary>The selected region; two-way so the inspector and the surface agree.</summary>
    public static readonly DependencyProperty SelectedRegionProperty = DependencyProperty.Register(
        nameof(SelectedRegion), typeof(IFocusRegionShape), typeof(FocusRegionSurface),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>False makes the surface a read-only preview of the regions.</summary>
    public static readonly DependencyProperty IsInteractiveProperty = DependencyProperty.Register(
        nameof(IsInteractive), typeof(bool), typeof(FocusRegionSurface),
        new FrameworkPropertyMetadata(true));

    /// <summary>Creates the surface.</summary>
    public FocusRegionSurface()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
    }

    /// <summary>Raised when a drag on empty photo completed and is large enough to be a region.</summary>
    public event EventHandler<FocusRegionDrawnEventArgs>? RegionDrawn;

    /// <summary>Raised at pointer-down on a user region — open the undo gesture here.</summary>
    public event EventHandler<FocusRegionEditEventArgs>? RegionEditStarted;

    /// <summary>Raised on every pointer frame of a move or resize.</summary>
    public event EventHandler<FocusRegionEditEventArgs>? RegionEditDelta;

    /// <summary>Raised at pointer-up, or with <c>Canceled</c> when Esc abandoned the gesture.</summary>
    public event EventHandler<FocusRegionEditEventArgs>? RegionEditCompleted;

    /// <summary>Raised when Delete is pressed with a region selected.</summary>
    public event EventHandler<EventArgs>? DeleteRequested;

    /// <inheritdoc cref="ImageProperty"/>
    public ImageSource? Image
    {
        get => (ImageSource?)GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    /// <inheritdoc cref="RegionsProperty"/>
    public IEnumerable? Regions
    {
        get => (IEnumerable?)GetValue(RegionsProperty);
        set => SetValue(RegionsProperty, value);
    }

    /// <inheritdoc cref="SelectedRegionProperty"/>
    public IFocusRegionShape? SelectedRegion
    {
        get => (IFocusRegionShape?)GetValue(SelectedRegionProperty);
        set => SetValue(SelectedRegionProperty, value);
    }

    /// <inheritdoc cref="IsInteractiveProperty"/>
    public bool IsInteractive
    {
        get => (bool)GetValue(IsInteractiveProperty);
        set => SetValue(IsInteractiveProperty, value);
    }

    /// <summary>The photo's rect inside the control, letterboxed and aspect-preserving.</summary>
    public Rect ImageRect => _imageRect;

    // ================================================================= layout and paint

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 480 : availableSize.Height;
        return new Size(width, height);
    }

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        drawingContext.DrawRectangle(PaletteBrush("FocusSurfaceBrush", "#FF060708"), null, bounds);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        _imageRect = ComputeImageRect(bounds);
        if (Image is { } image && _imageRect.Width > 0)
        {
            drawingContext.DrawImage(image, _imageRect);
        }

        foreach (var shape in Shapes())
        {
            DrawRegion(drawingContext, shape);
        }

        if (_drawing && _drawRect.W > 0)
        {
            var pen = new Pen(PaletteBrush("FocusUserBrush", "#FF4C8DF5"), 1.5)
            {
                DashStyle = new DashStyle([4, 3], 0),
            };
            pen.Freeze();
            drawingContext.DrawRectangle(PaletteBrush("FocusUserWashBrush", "#294C8DF5"), pen, ToControl(_drawRect));
        }
    }

    private Rect ComputeImageRect(Rect bounds)
    {
        var aspect = Image is { Width: > 0, Height: > 0 } image ? image.Width / image.Height : 1.5;
        var width = bounds.Width;
        var height = width / aspect;
        if (height > bounds.Height)
        {
            height = bounds.Height;
            width = height * aspect;
        }

        return new Rect(
            bounds.X + ((bounds.Width - width) / 2),
            bounds.Y + ((bounds.Height - height) / 2),
            Math.Max(0, width),
            Math.Max(0, height));
    }

    private void DrawRegion(DrawingContext dc, IFocusRegionShape shape)
    {
        var isActive = ReferenceEquals(shape, _active);
        var rect = ToControl(isActive ? _activeRect : shape.Rect);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var disabled = shape.Weight <= 0;
        var selected = ReferenceEquals(shape, SelectedRegion);
        var stroke = disabled ? PaletteBrush("FocusDisabledBrush", "#FF565D66") : KindBrush(shape.Kind);
        var wash = disabled ? null : KindWashBrush(shape.Kind);

        var pen = new Pen(stroke, selected ? 2.5 : 1.5);
        if (disabled)
        {
            pen.DashStyle = new DashStyle([3, 3], 0);
        }

        pen.Freeze();

        // A thin dark halo outside the stroke keeps every kind readable on a bright photo.
        var halo = new Pen(PaletteBrush("ScrimBrush", "#99000000"), 1);
        halo.Freeze();
        dc.DrawRectangle(wash, null, rect);
        dc.DrawRectangle(null, halo, Inflate(rect, pen.Thickness / 2));
        dc.DrawRectangle(null, pen, rect);

        DrawLabel(dc, shape, rect, stroke, disabled);

        if (selected && shape.IsEditable && IsInteractive)
        {
            var fill = PaletteBrush("FocusHandleBrush", "#FFECEFF3");
            var border = new Pen(PaletteBrush("FocusHandleBorderBrush", "#CC000000"), 1);
            border.Freeze();
            foreach (var handle in Handles(rect))
            {
                dc.DrawRectangle(fill, border, handle.Value);
            }
        }
    }

    private void DrawLabel(DrawingContext dc, IFocusRegionShape shape, Rect rect, Brush stroke, bool disabled)
    {
        var text = disabled ? shape.Label + " · off" : shape.Label;
        if (string.IsNullOrEmpty(text) || rect.Width < 54)
        {
            return;
        }

        var label = FormatText(text, PaletteBrush("TextOnAccentBrush", "#FF0A0D12"), 10.5);
        var pill = new Rect(rect.X, Math.Max(0, rect.Y - label.Height - 6), label.Width + 12, label.Height + 4);
        dc.DrawRoundedRectangle(stroke, null, pill, pill.Height / 2, pill.Height / 2);
        dc.DrawText(label, new Point(pill.X + 6, pill.Y + 2));
    }

    private static IEnumerable<KeyValuePair<FocusHandle, Rect>> Handles(Rect rect)
    {
        var half = HandleSize / 2;
        Rect At(double x, double y) => new(x - half, y - half, HandleSize, HandleSize);

        yield return new(FocusHandle.NorthWest, At(rect.Left, rect.Top));
        yield return new(FocusHandle.North, At(rect.Left + (rect.Width / 2), rect.Top));
        yield return new(FocusHandle.NorthEast, At(rect.Right, rect.Top));
        yield return new(FocusHandle.East, At(rect.Right, rect.Top + (rect.Height / 2)));
        yield return new(FocusHandle.SouthEast, At(rect.Right, rect.Bottom));
        yield return new(FocusHandle.South, At(rect.Left + (rect.Width / 2), rect.Bottom));
        yield return new(FocusHandle.SouthWest, At(rect.Left, rect.Bottom));
        yield return new(FocusHandle.West, At(rect.Left, rect.Top + (rect.Height / 2)));
    }

    // ================================================================= interaction

    /// <inheritdoc/>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseLeftButtonDown(e);

        Focus();
        if (!IsInteractive || _imageRect.Width <= 0)
        {
            return;
        }

        var point = e.GetPosition(this);
        var normalized = ToNormalized(point);

        // A handle of the current selection wins over everything: handles sit on top by definition.
        if (SelectedRegion is { IsEditable: true } selected)
        {
            var handle = HandleAt(selected, point);
            if (handle != FocusHandle.None)
            {
                Begin(selected, handle, normalized);
                e.Handled = true;
                return;
            }
        }

        var hit = RegionAt(point);
        if (hit is not null)
        {
            SelectedRegion = hit;
            if (hit.IsEditable)
            {
                Begin(hit, FocusHandle.Body, normalized);
            }

            InvalidateVisual();
            e.Handled = true;
            return;
        }

        SelectedRegion = null;
        _drawing = true;
        _drawAnchor = normalized;
        _drawRect = new CoreRect(normalized.X, normalized.Y, 0, 0);
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);

        if (!IsInteractive || _imageRect.Width <= 0)
        {
            return;
        }

        var point = e.GetPosition(this);

        if (_drawing)
        {
            var current = ToNormalized(point);
            _drawRaw = CoreRect.FromEdges(
                Math.Min(_drawAnchor.X, current.X),
                Math.Min(_drawAnchor.Y, current.Y),
                Math.Max(_drawAnchor.X, current.X),
                Math.Max(_drawAnchor.Y, current.Y));

            // Below the threshold the band stays raw: snapping (which enforces a minimum size) would
            // otherwise turn a plain deselecting click into a region.
            _drawRect = _drawRaw.W >= DrawThreshold && _drawRaw.H >= DrawThreshold
                ? SnapRect(_drawRaw, FocusHandle.SouthEast)
                : _drawRaw;

            InvalidateVisual();
            return;
        }

        if (_active is not null)
        {
            var normalized = ToNormalized(point);
            var next = _activeHandle == FocusHandle.Body
                ? Move(_activeStart, normalized - _grabNormalized)
                : Resize(_activeStart, _activeHandle, normalized);

            next = SnapRect(next, _activeHandle);
            if (next != _activeRect)
            {
                _activeRect = next;
                RegionEditDelta?.Invoke(this, new FocusRegionEditEventArgs(_active, next, _activeStart, _activeHandle));
                InvalidateVisual();
            }

            return;
        }

        Cursor = CursorFor(point);
    }

    /// <inheritdoc/>
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseLeftButtonUp(e);

        if (_drawing)
        {
            _drawing = false;
            ReleaseMouseCapture();
            var rect = _drawRect;
            var raw = _drawRaw;
            _drawRect = default;
            _drawRaw = default;
            InvalidateVisual();

            if (raw.W >= DrawThreshold && raw.H >= DrawThreshold)
            {
                RegionDrawn?.Invoke(this, new FocusRegionDrawnEventArgs(Clamp(rect)));
            }

            e.Handled = true;
            return;
        }

        if (_active is not null)
        {
            Complete(canceled: false);
            e.Handled = true;
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
        var hit = RegionAt(e.GetPosition(this));
        if (hit is not null)
        {
            SelectedRegion = hit;
            InvalidateVisual();
        }
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);

        if (!IsInteractive)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape when _active is not null:
                Complete(canceled: true);
                e.Handled = true;
                break;

            case Key.Escape when _drawing:
                _drawing = false;
                _drawRect = default;
                ReleaseMouseCapture();
                InvalidateVisual();
                e.Handled = true;
                break;

            case Key.Escape:
                SelectedRegion = null;
                InvalidateVisual();
                e.Handled = true;
                break;

            case Key.Delete or Key.Back when SelectedRegion is not null:
                DeleteRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;

            case Key.Left or Key.Right or Key.Up or Key.Down when SelectedRegion is { IsEditable: true } nudged:
                Nudge(nudged, e.Key, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                e.Handled = true;
                break;

            case Key.Tab:
                SelectNext((Keyboard.Modifiers & ModifierKeys.Shift) == 0);
                e.Handled = true;
                break;

            default:
                break;
        }
    }

    private void Nudge(IFocusRegionShape shape, Key key, bool coarse)
    {
        var step = coarse ? 0.05 : 0.005;
        var delta = key switch
        {
            Key.Left => new Vector(-step, 0),
            Key.Right => new Vector(step, 0),
            Key.Up => new Vector(0, -step),
            _ => new Vector(0, step),
        };

        var start = shape.Rect;
        var next = Move(start, delta);
        RegionEditStarted?.Invoke(this, new FocusRegionEditEventArgs(shape, start, start, FocusHandle.Body));
        RegionEditDelta?.Invoke(this, new FocusRegionEditEventArgs(shape, next, start, FocusHandle.Body));
        RegionEditCompleted?.Invoke(this, new FocusRegionEditEventArgs(shape, next, start, FocusHandle.Body));
        InvalidateVisual();
    }

    private void SelectNext(bool forward)
    {
        var shapes = Shapes().ToList();
        if (shapes.Count == 0)
        {
            return;
        }

        var index = SelectedRegion is null ? -1 : shapes.FindIndex(s => ReferenceEquals(s, SelectedRegion));
        var next = index < 0
            ? (forward ? 0 : shapes.Count - 1)
            : ((index + (forward ? 1 : -1)) % shapes.Count + shapes.Count) % shapes.Count;

        SelectedRegion = shapes[next];
        InvalidateVisual();
    }

    private void Begin(IFocusRegionShape shape, FocusHandle handle, Point normalized)
    {
        _active = shape;
        _activeHandle = handle;
        _activeStart = shape.Rect;
        _activeRect = shape.Rect;
        _grabNormalized = normalized;
        CaptureMouse();
        RegionEditStarted?.Invoke(this, new FocusRegionEditEventArgs(shape, _activeRect, _activeStart, handle));
    }

    private void Complete(bool canceled)
    {
        var shape = _active;
        if (shape is null)
        {
            return;
        }

        var rect = canceled ? _activeStart : _activeRect;
        var handle = _activeHandle;
        _active = null;
        _activeHandle = FocusHandle.None;
        ReleaseMouseCapture();
        InvalidateVisual();

        RegionEditCompleted?.Invoke(
            this, new FocusRegionEditEventArgs(shape, rect, _activeStart, handle, canceled));
    }

    private Cursor CursorFor(Point point)
    {
        if (SelectedRegion is { IsEditable: true } selected)
        {
            var handle = HandleAt(selected, point);
            if (handle != FocusHandle.None && handle != FocusHandle.Body)
            {
                return handle switch
                {
                    FocusHandle.NorthWest or FocusHandle.SouthEast => Cursors.SizeNWSE,
                    FocusHandle.NorthEast or FocusHandle.SouthWest => Cursors.SizeNESW,
                    FocusHandle.North or FocusHandle.South => Cursors.SizeNS,
                    _ => Cursors.SizeWE,
                };
            }
        }

        var hit = RegionAt(point);
        return hit is null ? Cursors.Cross : hit.IsEditable ? Cursors.SizeAll : Cursors.Arrow;
    }

    private FocusHandle HandleAt(IFocusRegionShape shape, Point point)
    {
        var rect = ToControl(ReferenceEquals(shape, _active) ? _activeRect : shape.Rect);
        foreach (var handle in Handles(rect))
        {
            if (Inflate(handle.Value, 2).Contains(point))
            {
                return handle.Key;
            }
        }

        return FocusHandle.None;
    }

    /// <summary>The smallest region under the point, so a box inside a box is still reachable.</summary>
    private IFocusRegionShape? RegionAt(Point point) =>
        Shapes()
            .Where(s => ToControl(s.Rect).Contains(point))
            .OrderBy(s => s.Rect.Area)
            .FirstOrDefault();

    // ================================================================= geometry

    /// <summary>Control point to normalized image coordinates, clamped to the photo.</summary>
    public Point ToNormalized(Point point) => _imageRect.Width <= 0 || _imageRect.Height <= 0
        ? new Point(0, 0)
        : new Point(
            Math.Clamp((point.X - _imageRect.X) / _imageRect.Width, 0, 1),
            Math.Clamp((point.Y - _imageRect.Y) / _imageRect.Height, 0, 1));

    /// <summary>Normalized image rect to a rect in control coordinates.</summary>
    public Rect ToControl(CoreRect rect) => new(
        _imageRect.X + (rect.X * _imageRect.Width),
        _imageRect.Y + (rect.Y * _imageRect.Height),
        Math.Max(0, rect.W * _imageRect.Width),
        Math.Max(0, rect.H * _imageRect.Height));

    private static CoreRect Move(CoreRect start, Vector delta) => new(
        Math.Clamp(start.X + delta.X, 0, 1 - start.W),
        Math.Clamp(start.Y + delta.Y, 0, 1 - start.H),
        start.W,
        start.H);

    private static CoreRect Resize(CoreRect start, FocusHandle handle, Point point)
    {
        var left = start.X;
        var top = start.Y;
        var right = start.Right;
        var bottom = start.Bottom;

        if (handle is FocusHandle.NorthWest or FocusHandle.West or FocusHandle.SouthWest)
        {
            left = Math.Min(point.X, right - MinimumSize);
        }

        if (handle is FocusHandle.NorthEast or FocusHandle.East or FocusHandle.SouthEast)
        {
            right = Math.Max(point.X, left + MinimumSize);
        }

        if (handle is FocusHandle.NorthWest or FocusHandle.North or FocusHandle.NorthEast)
        {
            top = Math.Min(point.Y, bottom - MinimumSize);
        }

        if (handle is FocusHandle.SouthWest or FocusHandle.South or FocusHandle.SouthEast)
        {
            bottom = Math.Max(point.Y, top + MinimumSize);
        }

        return Clamp(CoreRect.FromEdges(left, top, right, bottom));
    }

    /// <summary>
    /// Snaps the edges a gesture is actually moving to the image bounds, the centre lines and the
    /// thirds, within a tolerance measured in screen pixels so it feels the same at any zoom.
    /// </summary>
    private CoreRect SnapRect(CoreRect rect, FocusHandle handle)
    {
        if (_imageRect.Width <= 0 || _imageRect.Height <= 0)
        {
            return Clamp(rect);
        }

        var toleranceX = SnapPixels / _imageRect.Width;
        var toleranceY = SnapPixels / _imageRect.Height;

        double left = rect.X, top = rect.Y, right = rect.Right, bottom = rect.Bottom;

        if (handle == FocusHandle.Body)
        {
            // Move: whichever of the two edges (or the centre) is closest to a guide wins, and the
            // whole rect shifts by that one correction — the box never distorts while being moved.
            var dx = BestCorrection(toleranceX, left, right, rect.CenterX);
            var dy = BestCorrection(toleranceY, top, bottom, rect.CenterY);
            return Clamp(new CoreRect(rect.X + dx, rect.Y + dy, rect.W, rect.H));
        }

        if (handle is FocusHandle.NorthWest or FocusHandle.West or FocusHandle.SouthWest)
        {
            left = Snap(left, toleranceX);
        }

        if (handle is FocusHandle.NorthEast or FocusHandle.East or FocusHandle.SouthEast)
        {
            right = Snap(right, toleranceX);
        }

        if (handle is FocusHandle.NorthWest or FocusHandle.North or FocusHandle.NorthEast)
        {
            top = Snap(top, toleranceY);
        }

        if (handle is FocusHandle.SouthWest or FocusHandle.South or FocusHandle.SouthEast)
        {
            bottom = Snap(bottom, toleranceY);
        }

        if (right - left < MinimumSize)
        {
            right = left + MinimumSize;
        }

        if (bottom - top < MinimumSize)
        {
            bottom = top + MinimumSize;
        }

        return Clamp(CoreRect.FromEdges(left, top, right, bottom));
    }

    private static double BestCorrection(double tolerance, params double[] edges)
    {
        var best = 0.0;
        var bestDistance = tolerance;

        foreach (var edge in edges)
        {
            foreach (var target in SnapTargets)
            {
                var distance = Math.Abs(target - edge);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = target - edge;
                }
            }
        }

        return best;
    }

    private static double Snap(double value, double tolerance)
    {
        foreach (var target in SnapTargets)
        {
            if (Math.Abs(target - value) < tolerance)
            {
                return target;
            }
        }

        return value;
    }

    private static CoreRect Clamp(CoreRect rect)
    {
        var w = Math.Clamp(rect.W, MinimumSize, 1);
        var h = Math.Clamp(rect.H, MinimumSize, 1);
        return new CoreRect(Math.Clamp(rect.X, 0, 1 - w), Math.Clamp(rect.Y, 0, 1 - h), w, h);
    }

    private static Rect Inflate(Rect rect, double amount)
    {
        var result = rect;
        result.Inflate(amount, amount);
        return result;
    }

    // ================================================================= plumbing

    private IEnumerable<IFocusRegionShape> Shapes() =>
        Regions?.OfType<IFocusRegionShape>() ?? [];

    private static void OnRegionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (FocusRegionSurface)d;
        surface.Detach(e.OldValue as IEnumerable);
        surface.Attach(e.NewValue as IEnumerable);
        surface.InvalidateVisual();
    }

    private void Attach(IEnumerable? regions)
    {
        if (regions is INotifyCollectionChanged collection)
        {
            collection.CollectionChanged += OnCollectionChanged;
        }

        foreach (var shape in regions?.OfType<IFocusRegionShape>() ?? [])
        {
            shape.PropertyChanged += OnShapeChanged;
        }
    }

    private void Detach(IEnumerable? regions)
    {
        if (regions is INotifyCollectionChanged collection)
        {
            collection.CollectionChanged -= OnCollectionChanged;
        }

        foreach (var shape in regions?.OfType<IFocusRegionShape>() ?? [])
        {
            shape.PropertyChanged -= OnShapeChanged;
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var shape in e.OldItems?.OfType<IFocusRegionShape>() ?? [])
        {
            shape.PropertyChanged -= OnShapeChanged;
        }

        foreach (var shape in e.NewItems?.OfType<IFocusRegionShape>() ?? [])
        {
            shape.PropertyChanged += OnShapeChanged;
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            Attach(Regions);
        }

        InvalidateVisual();
    }

    private void OnShapeChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    private FormattedText FormatText(string text, Brush brush, double size)
    {
        var family = TryFindResource("AppFontFamily") as FontFamily ?? new FontFamily("Segoe UI");
        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    private Brush KindBrush(FocusKind kind) => kind switch
    {
        FocusKind.User => PaletteBrush("FocusUserBrush", "#FF4C8DF5"),
        FocusKind.Person => PaletteBrush("FocusPersonBrush", "#FF3FB765"),
        FocusKind.Face => PaletteBrush("FocusFaceBrush", "#FF35B9B4"),
        _ => PaletteBrush("FocusSaliencyBrush", "#FF767F8A"),
    };

    private Brush KindWashBrush(FocusKind kind) => kind switch
    {
        FocusKind.User => PaletteBrush("FocusUserWashBrush", "#294C8DF5"),
        FocusKind.Person => PaletteBrush("FocusPersonWashBrush", "#1F3FB765"),
        FocusKind.Face => PaletteBrush("FocusFaceWashBrush", "#1F35B9B4"),
        _ => PaletteBrush("FocusSaliencyWashBrush", "#14767F8A"),
    };

    /// <summary>
    /// Resolves a themed brush. The literal is a last-resort fallback for a surface hosted outside the
    /// app's resource scope (the XAML designer); inside the app the dictionary always wins, so colour
    /// still lives in <c>Themes/</c> alone.
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
}
