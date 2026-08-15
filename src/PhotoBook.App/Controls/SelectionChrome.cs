using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PhotoBook.App.Controls;

/// <summary>One of the eight grips on a selection frame, named for the compass point it sits on.</summary>
internal enum SelectionGrip
{
    /// <summary>Top-left corner.</summary>
    NorthWest,

    /// <summary>Top edge midpoint.</summary>
    North,

    /// <summary>Top-right corner.</summary>
    NorthEast,

    /// <summary>Left edge midpoint.</summary>
    West,

    /// <summary>Right edge midpoint.</summary>
    East,

    /// <summary>Bottom-left corner.</summary>
    SouthWest,

    /// <summary>Bottom edge midpoint.</summary>
    South,

    /// <summary>Bottom-right corner.</summary>
    SouthEast,
}

/// <summary>
/// The one selection language the whole editor speaks: a crisp accent frame carrying small
/// <b>square</b> grips at the corners and edge midpoints — the convention every image editor uses —
/// instead of the round knobs this app drew before.
///
/// <para><b>Why it is drawn in three strokes.</b> A selection lands on photographs, so it has to
/// read on a white sky and on a black shadow in the same frame. Each stroke is therefore laid down
/// as a dark contrast hairline just outside the accent line and another just inside it: on a light
/// photo the dark hairlines carry the shape, on a dark one the accent does. Grips get the same
/// treatment — a light body on a dark ring — which is why they never disappear into the picture.</para>
///
/// <para><b>Hit area is not the drawn size.</b> <see cref="GripSize"/> is what the user sees;
/// <see cref="GripHitPad"/> is what they can hit, inflating an 8 px square to a comfortable 20 px
/// target. Callers hit-test with <see cref="HitTest"/> so the two can never drift apart.</para>
///
/// <para>Colour is always passed in by the caller, resolved from <c>Themes/Palette.xaml</c> —
/// nothing here knows a hex value.</para>
/// </summary>
internal static class SelectionChrome
{
    /// <summary>The drawn edge length of a grip, in device-independent pixels.</summary>
    public const double GripSize = 8;

    /// <summary>How far past the drawn grip the pointer still counts as on it.</summary>
    public const double GripHitPad = 6;

    /// <summary>Below this, in either axis, the edge midpoints would collide with the corners.</summary>
    private const double MinimumSizeForEdgeGrips = 34;

    /// <summary>Below this a frame carries no grips at all — they would swamp what they mark.</summary>
    private const double MinimumSizeForGrips = 14;

    private static readonly SelectionGrip[] AllGrips =
    [
        SelectionGrip.NorthWest, SelectionGrip.North, SelectionGrip.NorthEast,
        SelectionGrip.West, SelectionGrip.East,
        SelectionGrip.SouthWest, SelectionGrip.South, SelectionGrip.SouthEast,
    ];

    private static readonly SelectionGrip[] CornerGrips =
    [
        SelectionGrip.NorthWest, SelectionGrip.NorthEast,
        SelectionGrip.SouthWest, SelectionGrip.SouthEast,
    ];

    /// <summary>The resize cursor that matches a grip's direction.</summary>
    /// <param name="grip">The grip under the pointer, or null for none.</param>
    public static Cursor CursorFor(SelectionGrip? grip) => grip switch
    {
        SelectionGrip.North or SelectionGrip.South => Cursors.SizeNS,
        SelectionGrip.West or SelectionGrip.East => Cursors.SizeWE,
        SelectionGrip.NorthWest or SelectionGrip.SouthEast => Cursors.SizeNWSE,
        SelectionGrip.NorthEast or SelectionGrip.SouthWest => Cursors.SizeNESW,
        _ => Cursors.Arrow,
    };

    /// <summary>
    /// The grips a frame of this size shows: all eight, the four corners, or none at all. A small
    /// container keeps its corners — losing them would make it unresizable — and only sheds the edge
    /// midpoints, which would otherwise sit on top of the corners.
    /// </summary>
    /// <param name="frame">The selection frame in control pixels.</param>
    /// <param name="cornersOnly">True to omit the edge midpoints whatever the size.</param>
    public static IReadOnlyList<SelectionGrip> GripsFor(Rect frame, bool cornersOnly)
    {
        if (frame.IsEmpty || frame.Width < MinimumSizeForGrips || frame.Height < MinimumSizeForGrips)
        {
            return [];
        }

        return cornersOnly || frame.Width < MinimumSizeForEdgeGrips || frame.Height < MinimumSizeForEdgeGrips
            ? CornerGrips
            : AllGrips;
    }

    /// <summary>The square a grip occupies, centred on its point of the frame.</summary>
    /// <param name="frame">The selection frame in control pixels.</param>
    /// <param name="grip">Which grip.</param>
    public static Rect GripRect(Rect frame, SelectionGrip grip)
    {
        const double Half = GripSize / 2;
        var (x, y) = grip switch
        {
            SelectionGrip.NorthWest => (frame.Left, frame.Top),
            SelectionGrip.North => (frame.Left + (frame.Width / 2), frame.Top),
            SelectionGrip.NorthEast => (frame.Right, frame.Top),
            SelectionGrip.West => (frame.Left, frame.Top + (frame.Height / 2)),
            SelectionGrip.East => (frame.Right, frame.Top + (frame.Height / 2)),
            SelectionGrip.SouthWest => (frame.Left, frame.Bottom),
            SelectionGrip.South => (frame.Left + (frame.Width / 2), frame.Bottom),
            _ => (frame.Right, frame.Bottom),
        };

        // Whole device-independent pixels, so an 8 px square stays an 8 px square rather than a
        // blurred 9 px one wherever the frame happens to land.
        return new Rect(Math.Round(x - Half), Math.Round(y - Half), GripSize, GripSize);
    }

    /// <summary>
    /// The grip under a point, or null. The hit area is <see cref="GripHitPad"/> larger than the
    /// drawn square on every side, so a grip is comfortable to grab at any zoom.
    /// </summary>
    /// <param name="frame">The selection frame in control pixels.</param>
    /// <param name="point">The pointer, in the same space.</param>
    /// <param name="cornersOnly">True when the frame only offers corner grips.</param>
    public static SelectionGrip? HitTest(Rect frame, Point point, bool cornersOnly = false)
    {
        foreach (var grip in GripsFor(frame, cornersOnly))
        {
            if (Inflate(GripRect(frame, grip), GripHitPad).Contains(point))
            {
                return grip;
            }
        }

        return null;
    }

    /// <summary>
    /// Draws the selection frame: the accent line with a dark contrast hairline either side of it.
    /// </summary>
    /// <param name="dc">The drawing context.</param>
    /// <param name="frame">The frame in control pixels.</param>
    /// <param name="accent">The accent stroke — <c>SelectionBorderBrush</c> for a selection.</param>
    /// <param name="contrast">The hairline brush, normally <c>ScrimBrush</c>; null to skip it.</param>
    /// <param name="thickness">The accent stroke's weight.</param>
    /// <param name="fill">An optional wash inside the frame.</param>
    public static void DrawFrame(
        DrawingContext dc, Rect frame, Brush accent, Brush? contrast, double thickness = 2, Brush? fill = null)
    {
        ArgumentNullException.ThrowIfNull(dc);

        if (frame.IsEmpty || frame.Width <= 1 || frame.Height <= 1)
        {
            return;
        }

        if (fill is not null)
        {
            dc.DrawRectangle(fill, null, frame);
        }

        if (contrast is not null)
        {
            var hairline = new Pen(contrast, 1);
            hairline.Freeze();
            dc.DrawRectangle(null, hairline, Inflate(frame, 0.5));
            dc.DrawRectangle(null, hairline, Inflate(frame, -(thickness + 0.5)));
        }

        var pen = new Pen(accent, thickness);
        pen.Freeze();
        dc.DrawRectangle(null, pen, Inflate(frame, -(thickness / 2)));
    }

    /// <summary>
    /// Draws the square grips. The hovered one is filled with <paramref name="hoverFill"/> and grown
    /// a pixel on each side, which is the only feedback that says "this grip, not the one next to it".
    /// </summary>
    /// <param name="dc">The drawing context.</param>
    /// <param name="frame">The frame in control pixels.</param>
    /// <param name="fill">The grip body — a light brush, so it reads on a dark photo.</param>
    /// <param name="stroke">The grip's ring — a dark brush, so it reads on a light photo.</param>
    /// <param name="hoverFill">The body of the hovered or active grip; null to skip the state.</param>
    /// <param name="active">The grip being hovered or dragged, or null.</param>
    /// <param name="cornersOnly">True to draw only the four corners.</param>
    public static void DrawGrips(
        DrawingContext dc,
        Rect frame,
        Brush fill,
        Brush stroke,
        Brush? hoverFill = null,
        SelectionGrip? active = null,
        bool cornersOnly = false)
    {
        ArgumentNullException.ThrowIfNull(dc);

        var grips = GripsFor(frame, cornersOnly);
        if (grips.Count == 0)
        {
            return;
        }

        var ring = new Pen(stroke, 1);
        ring.Freeze();

        foreach (var grip in grips)
        {
            var rect = GripRect(frame, grip);
            var hot = hoverFill is not null && active == grip;
            dc.DrawRectangle(hot ? hoverFill : fill, ring, hot ? Inflate(rect, 1) : rect);
        }
    }

    private static Rect Inflate(Rect rect, double by)
    {
        var width = rect.Width + (2 * by);
        var height = rect.Height + (2 * by);
        return width <= 0 || height <= 0
            ? rect
            : new Rect(rect.X - by, rect.Y - by, width, height);
    }
}
