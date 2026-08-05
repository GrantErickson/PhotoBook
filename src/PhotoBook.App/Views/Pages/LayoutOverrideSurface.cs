using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PhotoBook.App.Controls;
using PhotoBook.App.ViewModels.Pages;
using CoreRect = PhotoBook.Core.Model.Rect;
using PageGeometry = PhotoBook.Core.Model.PageGeometry;

namespace PhotoBook.App.Views.Pages;

/// <summary>Which grip of a container is being dragged.</summary>
internal enum OverrideHandle
{
    /// <summary>The body: a move.</summary>
    Body,

    /// <summary>Top edge.</summary>
    N,

    /// <summary>Bottom edge.</summary>
    S,

    /// <summary>Left edge.</summary>
    W,

    /// <summary>Right edge.</summary>
    E,

    /// <summary>Top-left corner.</summary>
    NW,

    /// <summary>Top-right corner.</summary>
    NE,

    /// <summary>Bottom-left corner.</summary>
    SW,

    /// <summary>Bottom-right corner.</summary>
    SE,
}

/// <summary>
/// The interactive layer of layout override mode (doc 09 §3.7, R15). Laid over a
/// <see cref="PageCanvas"/> in the same grid cell, it dims the page, draws every image and text
/// container with move/resize handles, and snaps geometry to the trim edges, the 0.375 in safe
/// margin, the page halves and thirds, and the other containers' edges — 6 screen pixels of
/// tolerance, exactly as the spec says.
/// <para>
/// It owns no model state. Coordinates come from the canvas's own trim rect, so the handles can
/// never disagree with the pixels underneath, and every gesture is bracketed by
/// <see cref="PageOverrideViewModel.BeginGeometryEdit"/> /
/// <see cref="PageOverrideViewModel.EndGeometryEdit"/>, which is what makes the whole drag one undo
/// entry — with the page's Detach folded into it.
/// </para>
/// </summary>
public sealed class LayoutOverrideSurface : FrameworkElement
{
    /// <summary>Snap tolerance in screen pixels (doc 09 §3.7).</summary>
    public const double SnapTolerancePx = 6;

    private const double HandleSize = 9;
    private const double HandleHitPad = 5;

    private OverrideHandle _handle = OverrideHandle.Body;
    private string? _dragId;
    private Point _dragStart;
    private CoreRect _dragRect;
    private double? _snapX;
    private double? _snapY;

    /// <summary>The canvas this layer sits on; its trim rect is the coordinate system.</summary>
    public static readonly DependencyProperty CanvasProperty = DependencyProperty.Register(
        nameof(Canvas), typeof(PageCanvas), typeof(LayoutOverrideSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The override-mode model.</summary>
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(PageOverrideViewModel), typeof(LayoutOverrideSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnModelChanged));

    /// <summary>Creates the layer. It is only hit-testable while override mode is on.</summary>
    public LayoutOverrideSurface()
    {
        Focusable = true;
        FocusVisualStyle = null;
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;
    }

    /// <inheritdoc cref="CanvasProperty"/>
    public PageCanvas? Canvas
    {
        get => (PageCanvas?)GetValue(CanvasProperty);
        set => SetValue(CanvasProperty, value);
    }

    /// <inheritdoc cref="ModelProperty"/>
    public PageOverrideViewModel? Model
    {
        get => (PageOverrideViewModel?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    private static void OnModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (LayoutOverrideSurface)d;

        if (e.OldValue is PageOverrideViewModel previous)
        {
            previous.Invalidated -= surface.OnModelInvalidated;
            previous.PropertyChanged -= surface.OnModelPropertyChanged;
        }

        if (e.NewValue is PageOverrideViewModel next)
        {
            next.Invalidated += surface.OnModelInvalidated;
            next.PropertyChanged += surface.OnModelPropertyChanged;
        }

        surface.SyncActive();
    }

    private void OnModelInvalidated() => InvalidateVisual();

    private void OnModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PageOverrideViewModel.IsActive))
        {
            SyncActive();
        }

        InvalidateVisual();
    }

    private void SyncActive()
    {
        var active = Model?.IsActive == true;
        IsHitTestVisible = active;
        Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (active)
        {
            Focus();
        }

        InvalidateVisual();
    }

    // =================================================================== geometry

    private Rect Trim => Canvas?.TrimRect ?? Rect.Empty;

    private Rect ToControl(CoreRect rect)
    {
        var trim = Trim;
        return new Rect(
            trim.X + (rect.X * trim.Width),
            trim.Y + (rect.Y * trim.Height),
            Math.Max(0, rect.W * trim.Width),
            Math.Max(0, rect.H * trim.Height));
    }

    private Point ToNormalized(Point point)
    {
        var trim = Trim;
        return trim.Width <= 0 || trim.Height <= 0
            ? new Point(double.NaN, double.NaN)
            : new Point((point.X - trim.X) / trim.Width, (point.Y - trim.Y) / trim.Height);
    }

    private double ToleranceX => Trim.Width > 0 ? SnapTolerancePx / Trim.Width : 0;

    private double ToleranceY => Trim.Height > 0 ? SnapTolerancePx / Trim.Height : 0;

    // =================================================================== pointer

    /// <inheritdoc/>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Model is not { IsActive: true } model || Trim.Width <= 0)
        {
            return;
        }

        Focus();
        var point = e.GetPosition(this);
        var hit = HitTest(point);

        if (hit is null)
        {
            model.SelectedId = null;
            InvalidateVisual();
            return;
        }

        var (item, handle) = hit.Value;
        model.SelectedId = item.Id;

        if (!model.BeginGeometryEdit(item.Id))
        {
            InvalidateVisual();
            return;
        }

        _dragId = item.Id;
        _handle = handle;
        _dragStart = ToNormalized(point);

        // The detach may have rebuilt the item list, so re-read the rect from the live model.
        _dragRect = model.Items.FirstOrDefault(i => string.Equals(i.Id, item.Id, StringComparison.Ordinal))?.Rect
                    ?? item.Rect;

        CaptureMouse();
        e.Handled = true;
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Model is not { IsActive: true } model || Trim.Width <= 0)
        {
            return;
        }

        var point = e.GetPosition(this);
        var normalized = ToNormalized(point);
        if (!double.IsNaN(normalized.X))
        {
            model.CursorNormalized = (normalized.X, normalized.Y);
        }

        if (_dragId is null)
        {
            Cursor = CursorFor(HitTest(point)?.Handle);
            return;
        }

        var delta = new Vector(normalized.X - _dragStart.X, normalized.Y - _dragStart.Y);
        var rect = _handle == OverrideHandle.Body ? Moved(_dragRect, delta) : Resized(_dragRect, _handle, delta);
        rect = Snap(rect, _handle, model);

        model.UpdateGeometry(_dragId, rect);
        e.Handled = true;
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragId is null)
        {
            return;
        }

        _dragId = null;
        _snapX = null;
        _snapY = null;
        ReleaseMouseCapture();
        Model?.EndGeometryEdit();
        e.Handled = true;
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragId is null)
        {
            return;
        }

        _dragId = null;
        _snapX = null;
        _snapY = null;
        Model?.EndGeometryEdit();
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Model is not { IsActive: true } model)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape when _dragId is not null:
                _dragId = null;
                ReleaseMouseCapture();
                model.CancelGeometryEdit();
                e.Handled = true;
                break;

            case Key.Escape:
                model.Exit();
                e.Handled = true;
                break;

            case Key.Delete when model.DeleteSelectedCommand.CanExecute(null):
                model.DeleteSelectedCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Tab:
                StepSelection(model, forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
                e.Handled = true;
                break;
        }

        InvalidateVisual();
    }

    private static void StepSelection(PageOverrideViewModel model, bool forward)
    {
        if (model.Items.Count == 0)
        {
            return;
        }

        var index = model.Items.ToList().FindIndex(i => string.Equals(i.Id, model.SelectedId, StringComparison.Ordinal));
        var next = index < 0
            ? (forward ? 0 : model.Items.Count - 1)
            : (((index + (forward ? 1 : -1)) % model.Items.Count) + model.Items.Count) % model.Items.Count;

        model.SelectedId = model.Items[next].Id;
    }

    private static Cursor CursorFor(OverrideHandle? handle) => handle switch
    {
        OverrideHandle.N or OverrideHandle.S => Cursors.SizeNS,
        OverrideHandle.W or OverrideHandle.E => Cursors.SizeWE,
        OverrideHandle.NW or OverrideHandle.SE => Cursors.SizeNWSE,
        OverrideHandle.NE or OverrideHandle.SW => Cursors.SizeNESW,
        OverrideHandle.Body => Cursors.SizeAll,
        _ => Cursors.Arrow,
    };

    private (OverrideItemViewModel Item, OverrideHandle Handle)? HitTest(Point point)
    {
        if (Model is not { } model)
        {
            return null;
        }

        // The selected container's handles win, so a grip on top of another container still resizes
        // the thing that is actually selected.
        if (model.Selected is { } selected)
        {
            var rect = ToControl(selected.Rect);
            foreach (var handle in Handles)
            {
                if (HandleRect(rect, handle) is { } grip &&
                    Inflate(grip, HandleHitPad).Contains(point))
                {
                    return (selected, handle);
                }
            }
        }

        // Otherwise the topmost container under the pointer, which is the last one drawn.
        for (var i = model.Items.Count - 1; i >= 0; i--)
        {
            var item = model.Items[i];
            if (ToControl(item.Rect).Contains(point))
            {
                return (item, OverrideHandle.Body);
            }
        }

        return null;
    }

    private static readonly OverrideHandle[] Handles =
    [
        OverrideHandle.NW, OverrideHandle.N, OverrideHandle.NE,
        OverrideHandle.W, OverrideHandle.E,
        OverrideHandle.SW, OverrideHandle.S, OverrideHandle.SE,
    ];

    private static Rect Inflate(Rect rect, double by) =>
        new(rect.X - by, rect.Y - by, rect.Width + (2 * by), rect.Height + (2 * by));

    private static Rect? HandleRect(Rect rect, OverrideHandle handle)
    {
        var half = HandleSize / 2;
        var (x, y) = handle switch
        {
            OverrideHandle.NW => (rect.Left, rect.Top),
            OverrideHandle.N => (rect.Left + (rect.Width / 2), rect.Top),
            OverrideHandle.NE => (rect.Right, rect.Top),
            OverrideHandle.W => (rect.Left, rect.Top + (rect.Height / 2)),
            OverrideHandle.E => (rect.Right, rect.Top + (rect.Height / 2)),
            OverrideHandle.SW => (rect.Left, rect.Bottom),
            OverrideHandle.S => (rect.Left + (rect.Width / 2), rect.Bottom),
            OverrideHandle.SE => (rect.Right, rect.Bottom),
            _ => (double.NaN, double.NaN),
        };

        return double.IsNaN(x) ? null : new Rect(x - half, y - half, HandleSize, HandleSize);
    }

    private static CoreRect Moved(CoreRect rect, Vector delta) =>
        new(rect.X + delta.X, rect.Y + delta.Y, rect.W, rect.H);

    private static CoreRect Resized(CoreRect rect, OverrideHandle handle, Vector delta)
    {
        var left = rect.X;
        var top = rect.Y;
        var right = rect.Right;
        var bottom = rect.Bottom;

        if (handle is OverrideHandle.W or OverrideHandle.NW or OverrideHandle.SW)
        {
            left = Math.Min(rect.X + delta.X, right - Services.PageLayoutOverride.MinimumSize);
        }

        if (handle is OverrideHandle.E or OverrideHandle.NE or OverrideHandle.SE)
        {
            right = Math.Max(rect.Right + delta.X, left + Services.PageLayoutOverride.MinimumSize);
        }

        if (handle is OverrideHandle.N or OverrideHandle.NW or OverrideHandle.NE)
        {
            top = Math.Min(rect.Y + delta.Y, bottom - Services.PageLayoutOverride.MinimumSize);
        }

        if (handle is OverrideHandle.S or OverrideHandle.SW or OverrideHandle.SE)
        {
            bottom = Math.Max(rect.Bottom + delta.Y, top + Services.PageLayoutOverride.MinimumSize);
        }

        return CoreRect.FromEdges(left, top, right, bottom);
    }

    // ================================================================== snapping

    /// <summary>
    /// The snap targets of doc 09 §3.7: the trim edges, the safe margin, the page halves and thirds,
    /// and every other container's edges and centre.
    /// </summary>
    private (List<double> X, List<double> Y) SnapTargets(PageOverrideViewModel model)
    {
        var xs = new List<double>
        {
            0, 1, 0.5, 1.0 / 3, 2.0 / 3,
            PageGeometry.SafeMarginNormalizedX, 1 - PageGeometry.SafeMarginNormalizedX,
        };

        var ys = new List<double>
        {
            0, 1, 0.5, 1.0 / 3, 2.0 / 3,
            PageGeometry.SafeMarginNormalizedY, 1 - PageGeometry.SafeMarginNormalizedY,
        };

        foreach (var other in model.Items.Where(i => !string.Equals(i.Id, _dragId, StringComparison.Ordinal)))
        {
            xs.Add(other.Rect.X);
            xs.Add(other.Rect.Right);
            xs.Add(other.Rect.CenterX);
            ys.Add(other.Rect.Y);
            ys.Add(other.Rect.Bottom);
            ys.Add(other.Rect.CenterY);
        }

        return (xs, ys);
    }

    private CoreRect Snap(CoreRect rect, OverrideHandle handle, PageOverrideViewModel model)
    {
        var (xs, ys) = SnapTargets(model);
        _snapX = null;
        _snapY = null;

        if (handle == OverrideHandle.Body)
        {
            if (Nearest(xs, [rect.X, rect.CenterX, rect.Right], ToleranceX) is { } dx)
            {
                rect = rect with { X = rect.X + dx.Delta };
                _snapX = dx.Target;
            }

            if (Nearest(ys, [rect.Y, rect.CenterY, rect.Bottom], ToleranceY) is { } dy)
            {
                rect = rect with { Y = rect.Y + dy.Delta };
                _snapY = dy.Target;
            }

            return rect;
        }

        var left = rect.X;
        var top = rect.Y;
        var right = rect.Right;
        var bottom = rect.Bottom;

        if ((handle is OverrideHandle.W or OverrideHandle.NW or OverrideHandle.SW) &&
            Nearest(xs, [left], ToleranceX) is { } snapLeft)
        {
            left += snapLeft.Delta;
            _snapX = snapLeft.Target;
        }
        else if ((handle is OverrideHandle.E or OverrideHandle.NE or OverrideHandle.SE) &&
                 Nearest(xs, [right], ToleranceX) is { } snapRight)
        {
            right += snapRight.Delta;
            _snapX = snapRight.Target;
        }

        if ((handle is OverrideHandle.N or OverrideHandle.NW or OverrideHandle.NE) &&
            Nearest(ys, [top], ToleranceY) is { } snapTop)
        {
            top += snapTop.Delta;
            _snapY = snapTop.Target;
        }
        else if ((handle is OverrideHandle.S or OverrideHandle.SW or OverrideHandle.SE) &&
                 Nearest(ys, [bottom], ToleranceY) is { } snapBottom)
        {
            bottom += snapBottom.Delta;
            _snapY = snapBottom.Target;
        }

        return CoreRect.FromEdges(
            Math.Min(left, right - Services.PageLayoutOverride.MinimumSize),
            Math.Min(top, bottom - Services.PageLayoutOverride.MinimumSize),
            right,
            bottom);
    }

    private static (double Delta, double Target)? Nearest(
        IReadOnlyList<double> targets, IReadOnlyList<double> edges, double tolerance)
    {
        if (tolerance <= 0)
        {
            return null;
        }

        (double Delta, double Target)? best = null;
        var bestDistance = tolerance;

        foreach (var edge in edges)
        {
            foreach (var target in targets)
            {
                var distance = Math.Abs(target - edge);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = (target - edge, target);
                }
            }
        }

        return best;
    }

    // =================================================================== drawing

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (Model is not { IsActive: true } model || Trim.Width <= 0)
        {
            return;
        }

        var trim = Trim;

        // Photo content dims so the containers read as the subject (doc 09 §3.7).
        drawingContext.DrawRectangle(Palette("ScrimBrush"), null, trim);

        var safe = ToControl(PageGeometry.SafeAreaNormalized);
        drawingContext.DrawRectangle(null, DashedPen(Palette("BorderSubtleBrush"), 1), safe);

        foreach (var item in model.Items)
        {
            DrawContainer(drawingContext, item, isSelected: ReferenceEquals(item, model.Selected));
        }

        DrawSnapGuides(drawingContext, trim);
    }

    private void DrawContainer(DrawingContext dc, OverrideItemViewModel item, bool isSelected)
    {
        var rect = ToControl(item.Rect);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var stroke =
            isSelected ? Palette("SelectionBorderBrush") :
            item.IsText ? Palette("AccentDimBrush") :
            item.IsEmpty ? Palette("AmberFlagBrush") :
            Palette("BorderStrongBrush");

        var fill =
            isSelected ? Palette("AccentWashBrush") :
            item.IsText ? Palette("SurfaceHighestBrush") :
            item.IsEmpty ? Palette("AmberFlagWashBrush") :
            Palette("TransparentBrush");

        var pen = item.IsText || item.IsEmpty
            ? DashedPen(stroke, isSelected ? 2 : 1.5)
            : new Pen(stroke, isSelected ? 2 : 1.5);

        dc.DrawRectangle(fill, pen, rect);
        DrawLabel(dc, item, rect, isSelected);

        if (!isSelected)
        {
            return;
        }

        var gripFill = Palette("AccentBrush");
        var gripStroke = new Pen(Palette("TextOnAccentBrush"), 1);
        foreach (var handle in Handles)
        {
            if (HandleRect(rect, handle) is { } grip)
            {
                dc.DrawRectangle(gripFill, gripStroke, grip);
            }
        }
    }

    private void DrawLabel(DrawingContext dc, OverrideItemViewModel item, Rect rect, bool isSelected)
    {
        if (rect.Width < 46 || rect.Height < 22)
        {
            return;
        }

        var text = new FormattedText(
            item.Label,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            10.5,
            isSelected ? Palette("TextOnAccentBrush") : Palette("TextSecondaryBrush"),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var pad = 4.0;
        var chip = new Rect(rect.X + 5, rect.Y + 5, text.Width + (2 * pad), text.Height + 2);
        dc.DrawRoundedRectangle(
            isSelected ? Palette("AccentBrush") : Palette("SurfaceOverlayBrush"), null, chip, 3, 3);
        dc.DrawText(text, new Point(chip.X + pad, chip.Y + 1));
    }

    private void DrawSnapGuides(DrawingContext dc, Rect trim)
    {
        if (_dragId is null)
        {
            return;
        }

        var pen = DashedPen(Palette("AccentHoverBrush"), 1);

        if (_snapX is { } x)
        {
            var px = trim.X + (x * trim.Width);
            dc.DrawLine(pen, new Point(px, trim.Y), new Point(px, trim.Bottom));
        }

        if (_snapY is { } y)
        {
            var py = trim.Y + (y * trim.Height);
            dc.DrawLine(pen, new Point(trim.X, py), new Point(trim.Right, py));
        }
    }

    private static readonly Typeface LabelTypeface = new(
        new FontFamily("Segoe UI Variable Text, Segoe UI, Tahoma"),
        FontStyles.Normal,
        FontWeights.SemiBold,
        FontStretches.Normal);

    private static Pen DashedPen(Brush brush, double thickness) => new(brush, thickness)
    {
        DashStyle = new DashStyle([4, 3], 0),
    };

    /// <summary>
    /// Every colour comes from <c>Themes/Palette.xaml</c>; the transparent fallback only fires
    /// outside the app's resource scope, which is the XAML designer.
    /// </summary>
    private Brush Palette(string key) =>
        TryFindResource(key) as Brush
        ?? Application.Current?.TryFindResource(key) as Brush
        ?? Brushes.Transparent;
}
