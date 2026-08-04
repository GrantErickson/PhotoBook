namespace PhotoBook.Core.Model;

/// <summary>
/// An axis-aligned rectangle, origin top-left (doc 03 §1). A <see cref="Rect"/> is normally
/// normalized <c>[0,1] × [0,1]</c> in one of two coordinate spaces — <em>page space</em> (over the
/// single-page trim box: templates, slots) or <em>image space</em> (over the decoded,
/// orientation-corrected photo: focus regions, person-tag regions). A rect never mixes spaces; the
/// owning field documents which space it lives in.
/// </summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="W">Width; must be &gt; 0 for a well-formed rect.</param>
/// <param name="H">Height; must be &gt; 0 for a well-formed rect.</param>
public readonly record struct Rect(double X, double Y, double W, double H)
{
    /// <summary>The unit rect <c>(0,0,1,1)</c> — the whole page or the whole image.</summary>
    public static Rect Unit => new(0, 0, 1, 1);

    /// <summary>Right edge (<c>X + W</c>).</summary>
    public double Right => X + W;

    /// <summary>Bottom edge (<c>Y + H</c>).</summary>
    public double Bottom => Y + H;

    /// <summary>Horizontal center.</summary>
    public double CenterX => X + W / 2.0;

    /// <summary>Vertical center.</summary>
    public double CenterY => Y + H / 2.0;

    /// <summary>Area (<c>W × H</c>).</summary>
    public double Area => W * H;

    /// <summary>Width divided by height, in the rect's own coordinate space.</summary>
    public double Aspect => H == 0 ? double.NaN : W / H;

    /// <summary>True when every component is finite and both extents are strictly positive.</summary>
    public bool IsWellFormed =>
        double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(W) && double.IsFinite(H) &&
        W > 0 && H > 0;

    /// <summary>True when this rect lies entirely within the unit square.</summary>
    public bool IsInsideUnitSquare => X >= 0 && Y >= 0 && Right <= 1 && Bottom <= 1;

    /// <summary>True when the point lies inside (edges inclusive).</summary>
    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;

    /// <summary>True when <paramref name="other"/> lies entirely inside this rect (edges inclusive).</summary>
    public bool Contains(Rect other) =>
        other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;

    /// <summary>Area shared with <paramref name="other"/>; <c>0</c> when they do not overlap.</summary>
    public double IntersectionArea(Rect other)
    {
        var w = Math.Min(Right, other.Right) - Math.Max(X, other.X);
        var h = Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);
        return w <= 0 || h <= 0 ? 0 : w * h;
    }

    /// <summary>True when the rects share any positive area.</summary>
    public bool Intersects(Rect other) => IntersectionArea(other) > 0;

    /// <summary>
    /// Intersection over union — the overlap metric used by focus-region fusion (doc 06).
    /// Returns <c>0</c> when the rects are disjoint or degenerate.
    /// </summary>
    public double IoU(Rect other)
    {
        var inter = IntersectionArea(other);
        if (inter <= 0) return 0;
        var union = Area + other.Area - inter;
        return union <= 0 ? 0 : inter / union;
    }

    /// <summary>The smallest rect containing both this rect and <paramref name="other"/>.</summary>
    public Rect Union(Rect other)
    {
        var x = Math.Min(X, other.X);
        var y = Math.Min(Y, other.Y);
        return new Rect(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
    }

    /// <summary>
    /// Horizontal mirror about the page centerline: <c>x → 1 − x − w</c> (doc 07 "Mirroring").
    /// Templates are authored as right pages and mirrored for left pages; the transform is applied
    /// at load time and never persisted.
    /// </summary>
    public Rect Mirrored() => new(1 - X - W, Y, W, H);

    /// <summary>
    /// Maps this rect from normalized coordinates onto a concrete box (page points, pixels, …).
    /// </summary>
    public Rect ScaleTo(double originX, double originY, double width, double height) =>
        new(originX + X * width, originY + Y * height, W * width, H * height);

    /// <summary>A rect from explicit edges rather than an origin plus extents.</summary>
    public static Rect FromEdges(double left, double top, double right, double bottom) =>
        new(left, top, right - left, bottom - top);

    /// <summary>A rect of the given size centered on <paramref name="centerX"/>/<paramref name="centerY"/>.</summary>
    public static Rect FromCenter(double centerX, double centerY, double width, double height) =>
        new(centerX - width / 2.0, centerY - height / 2.0, width, height);

    /// <inheritdoc/>
    public override string ToString() =>
        $"({X:0.####}, {Y:0.####}, {W:0.####} × {H:0.####})";
}
