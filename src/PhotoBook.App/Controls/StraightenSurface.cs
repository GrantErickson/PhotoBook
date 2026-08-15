using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoBook.Core.Model;

// This control works in control-space geometry throughout, so Rect means WPF's. Core's normalized
// Rect is a different thing and is spelled out where it is genuinely meant.
using Rect = System.Windows.Rect;

namespace PhotoBook.App.Controls;

/// <summary>One frame of a straightening drag, or the end of one.</summary>
/// <param name="delta">Degrees to add to the photo's current straighten angle.</param>
/// <param name="reference">Which axis the line was measured against, after <c>Auto</c> resolved.</param>
/// <param name="isUsable">False while the drag is still too short to mean anything.</param>
public sealed class StraightenLineEventArgs(double delta, StraightenReference reference, bool isUsable) : EventArgs
{
    /// <summary>Degrees to add to the current straighten angle.</summary>
    public double Delta { get; } = delta;

    /// <summary>The axis the line was levelled against.</summary>
    public StraightenReference Reference { get; } = reference;

    /// <summary>False while the drag is shorter than <see cref="StraightenMath.MinimumLength"/>.</summary>
    public bool IsUsable { get; } = isUsable;
}

/// <summary>
/// The interactive half of the straightening tool (R6/R11, doc 05 geometry stage): a large preview the
/// user <b>drags a line along</b> — a horizon, a door frame, the edge of a table — from which the
/// rotation is derived. Levelling by nudging a number is guesswork; levelling by tracing the thing
/// that ought to be level is not.
///
/// <para>
/// <b>Angles, not pixels.</b> The photo is laid out letterboxed and aspect-preserving inside the
/// control, so control-space angles <em>are</em> image-space angles and the measurement needs no
/// coordinate conversion at all. <see cref="StraightenMath"/> in <c>PhotoBook.Core</c> owns the one
/// piece of arithmetic — including the sign convention that ties it to a clockwise-positive rotation —
/// so the tool's correctness is testable without a window.
/// </para>
///
/// <para>
/// <b>What is drawn.</b> While the pointer is down: the traced line with its endpoints, and a rotation
/// grid tilted to where "level" is about to be, clipped to the photo, so the user is aiming at
/// something rather than guessing. The grid stays up whenever <see cref="ShowGrid"/> is set — the
/// slider raises it too, so both routes to the same value look the same. The surface holds no model
/// state: it raises <see cref="LineChanged"/> per frame and <see cref="LineCommitted"/> at release,
/// and the view model turns those into one undoable edit.
/// </para>
/// </summary>
public sealed class StraightenSurface : FrameworkElement
{
    private const double Padding = 10;
    private const int GridDivisions = 8;

    private Point _start;
    private Point _current;
    private bool _dragging;

    /// <summary>Creates the surface.</summary>
    public StraightenSurface()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Cross;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    /// <summary>Raised on every pointer frame of a drag, so the readout can follow the line.</summary>
    public event EventHandler<StraightenLineEventArgs>? LineChanged;

    /// <summary>Raised once at pointer release: the value to commit as a single undo entry.</summary>
    public event EventHandler<StraightenLineEventArgs>? LineCommitted;

    /// <summary>Raised when <c>Esc</c> or a right-click abandons the drag.</summary>
    public event EventHandler? LineCanceled;

    /// <summary>The photo to trace on — the preview render, already carrying the current edits.</summary>
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(BitmapSource), typeof(StraightenSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Shows the rotation grid even when no drag is in flight (the slider raises it).</summary>
    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
        nameof(ShowGrid), typeof(bool), typeof(StraightenSurface),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The angle the grid is tilted to when no drag is running — normally the live slider value.</summary>
    public static readonly DependencyProperty GridAngleProperty = DependencyProperty.Register(
        nameof(GridAngle), typeof(double), typeof(StraightenSurface),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Level, plumb, or let each line decide.</summary>
    public static readonly DependencyProperty ReferenceProperty = DependencyProperty.Register(
        nameof(Reference), typeof(StraightenReference), typeof(StraightenSurface),
        new FrameworkPropertyMetadata(StraightenReference.Auto, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Colour of the traced line and its endpoints.</summary>
    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(StraightenSurface),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Colour of the rotation grid.</summary>
    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(StraightenSurface),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <inheritdoc cref="SourceProperty"/>
    public BitmapSource? Source
    {
        get => (BitmapSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <inheritdoc cref="ShowGridProperty"/>
    public bool ShowGrid
    {
        get => (bool)GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    /// <inheritdoc cref="GridAngleProperty"/>
    public double GridAngle
    {
        get => (double)GetValue(GridAngleProperty);
        set => SetValue(GridAngleProperty, value);
    }

    /// <inheritdoc cref="ReferenceProperty"/>
    public StraightenReference Reference
    {
        get => (StraightenReference)GetValue(ReferenceProperty);
        set => SetValue(ReferenceProperty, value);
    }

    /// <inheritdoc cref="LineBrushProperty"/>
    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    /// <inheritdoc cref="GridBrushProperty"/>
    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    /// <summary>Where the photo is drawn inside the control: aspect-preserved, letterboxed, inset.</summary>
    public Rect ImageRect
    {
        get
        {
            var available = new Size(
                Math.Max(0, RenderSize.Width - (2 * Padding)),
                Math.Max(0, RenderSize.Height - (2 * Padding)));

            if (Source is not { PixelWidth: > 0, PixelHeight: > 0 } source ||
                available.Width <= 0 || available.Height <= 0)
            {
                return new Rect(Padding, Padding, available.Width, available.Height);
            }

            var scale = Math.Min(available.Width / source.PixelWidth, available.Height / source.PixelHeight);
            var width = source.PixelWidth * scale;
            var height = source.PixelHeight * scale;
            return new Rect(
                Padding + ((available.Width - width) / 2),
                Padding + ((available.Height - height) / 2),
                width,
                height);
        }
    }

    /// <inheritdoc/>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseLeftButtonDown(e);

        var point = e.GetPosition(this);
        if (!ImageRect.Contains(point)) return;

        _start = point;
        _current = point;
        _dragging = true;
        Focus();
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);
        if (!_dragging) return;

        _current = e.GetPosition(this);
        InvalidateVisual();
        LineChanged?.Invoke(this, Measure());
    }

    /// <inheritdoc/>
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;

        _current = e.GetPosition(this);
        var measurement = Measure();
        EndDrag();

        if (measurement.IsUsable)
        {
            LineCommitted?.Invoke(this, measurement);
        }
        else
        {
            LineCanceled?.Invoke(this, EventArgs.Empty);
        }

        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseRightButtonDown(e);
        Cancel();
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _dragging)
        {
            Cancel();
            e.Handled = true;
        }
    }

    /// <summary>Abandons a drag in flight without committing anything.</summary>
    public void Cancel()
    {
        if (!_dragging) return;
        EndDrag();
        LineCanceled?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        base.OnRender(drawingContext);

        var image = ImageRect;
        if (image.Width <= 0 || image.Height <= 0) return;

        // A transparent hit-test backdrop: without it the control only receives input where it drew.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        if (Source is { } source)
        {
            drawingContext.DrawImage(source, image);
        }

        var measurement = _dragging ? Measure() : null;
        var gridAngle = measurement is { IsUsable: true } ? measurement.Delta : GridAngle;

        if (_dragging || ShowGrid)
        {
            DrawGrid(drawingContext, image, gridAngle);
        }

        if (_dragging)
        {
            DrawLine(drawingContext, measurement!);
        }
    }

    /// <summary>
    /// The rotation grid: axis lines tilted <em>against</em> the pending correction, so a grid line
    /// lying along the traced feature is the promise that the feature ends up level.
    /// </summary>
    private void DrawGrid(DrawingContext context, Rect image, double degrees)
    {
        var pen = new Pen(GridBrush, 1) { DashStyle = new DashStyle([4, 4], 0) };
        pen.Brush = Fade(GridBrush, 0.38);
        pen.Freeze();

        var strong = new Pen(Fade(GridBrush, 0.72), 1.4);
        strong.Freeze();

        var centre = new Point(image.X + (image.Width / 2), image.Y + (image.Height / 2));

        context.PushClip(new RectangleGeometry(image));
        context.PushTransform(new RotateTransform(-degrees, centre.X, centre.Y));

        // The grid is drawn over a square large enough that it still covers the photo once rotated.
        var reach = Math.Sqrt((image.Width * image.Width) + (image.Height * image.Height)) / 2;
        var step = Math.Max(image.Width, image.Height) / GridDivisions;

        for (var offset = -Math.Ceiling(reach / step) * step; offset <= reach; offset += step)
        {
            var isAxis = Math.Abs(offset) < 1e-6;
            context.DrawLine(
                isAxis ? strong : pen,
                new Point(centre.X - reach, centre.Y + offset),
                new Point(centre.X + reach, centre.Y + offset));
            context.DrawLine(
                isAxis ? strong : pen,
                new Point(centre.X + offset, centre.Y - reach),
                new Point(centre.X + offset, centre.Y + reach));
        }

        context.Pop();
        context.Pop();
    }

    private void DrawLine(DrawingContext context, StraightenLineEventArgs measurement)
    {
        var brush = measurement.IsUsable ? LineBrush : Fade(LineBrush, 0.4);
        var pen = new Pen(brush, 2);
        pen.Freeze();

        context.DrawLine(pen, _start, _current);
        context.DrawEllipse(brush, null, _start, 4, 4);
        context.DrawEllipse(brush, null, _current, 4, 4);

        if (!measurement.IsUsable) return;

        var text = new FormattedText(
            measurement.Delta.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture) + "°",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            13,
            LineBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var anchor = new Point(_current.X + 12, _current.Y - (text.Height / 2));
        var chip = new Rect(anchor.X - 6, anchor.Y - 3, text.Width + 12, text.Height + 6);
        context.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)), null, chip, 4, 4);
        context.DrawText(text, anchor);
    }

    private StraightenLineEventArgs Measure()
    {
        var usable = StraightenMath.IsUsableLine(_start.X, _start.Y, _current.X, _current.Y);
        var resolved = Reference == StraightenReference.Auto
            ? StraightenMath.Resolve(_start.X, _start.Y, _current.X, _current.Y)
            : Reference;
        var delta = StraightenMath.AngleFromLine(_start.X, _start.Y, _current.X, _current.Y, Reference);
        return new StraightenLineEventArgs(delta, resolved, usable);
    }

    private void EndDrag()
    {
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
    }

    private static Brush Fade(Brush brush, double opacity)
    {
        var faded = brush.Clone();
        faded.Opacity = opacity;
        faded.Freeze();
        return faded;
    }
}
