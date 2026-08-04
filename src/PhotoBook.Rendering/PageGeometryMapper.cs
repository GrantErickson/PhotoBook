using PhotoBook.Core.Model;
using SkiaSharp;
using CoreRect = PhotoBook.Core.Model.Rect;

namespace PhotoBook.Rendering;

/// <summary>
/// Maps the normalized <c>[0,1] × [0,1]</c> template coordinate space (kernel §3) onto physical page
/// geometry at any requested scale — screen pixels for the editor preview, PostScript points for the
/// PDF — for single pages and for 22 × 8.5 in spreads alike.
/// <para>
/// Nothing here is hard-coded to 11 × 8.5 in (R19). Every constant is derived from the
/// <see cref="PrintProfile"/> plus the <see cref="PageSizeSpec"/> the book names, exactly as doc 12
/// "Other page sizes" specifies:
/// </para>
/// <code>
/// trimW  = size.trimWidthIn  × scale
/// mediaW = (size.trimWidthIn + 2·bleedIn) × scale
/// xDev   = mediaLeft + bleedIn·scale + x·trimW
/// safe   = safeMarginIn·scale inset from trim
/// spread = 2·trimW wide, outer-edge bleed only
/// </code>
/// <para>
/// The default <c>11x8.5-landscape</c> profile at <c>scale = 72</c> reproduces the doc 12 table
/// exactly: media 810 × 630 pt, trim 792 × 612 pt at (9, 9), safe 738 × 558 pt at (36, 36).
/// </para>
/// </summary>
public sealed class PageGeometryMapper
{
    /// <summary>
    /// How close to a trim edge a slot edge must lie, in normalized units, to be snapped outward to
    /// the bleed edge (doc 12 "Bleed extension"): <c>x ≤ 0.005</c>, <c>x + w ≥ 0.995</c>.
    /// </summary>
    public const double BleedSnapTolerance = 0.005;

    /// <summary>The slug added on every side when <see cref="PrintProfile.IncludeTrimMarks"/> is set, in inches.</summary>
    public const double TrimMarkSlugIn = 0.25;

    private PageGeometryMapper(
        PrintProfile profile, PageSizeSpec pageSize, PageSurface surface, double scale, SKPoint origin)
    {
        Profile = profile;
        PageSize = pageSize;
        Surface = surface;
        Scale = scale;
        SlugIn = profile.IncludeTrimMarks ? TrimMarkSlugIn : 0.0;

        PageTrimWidthIn = pageSize.TrimWidthIn;
        TrimHeightIn = pageSize.TrimHeightIn;
        TrimWidthIn = surface == PageSurface.Spread ? pageSize.TrimWidthIn * 2 : pageSize.TrimWidthIn;
        BleedIn = Math.Max(0, profile.BleedIn);
        SafeMarginIn = Math.Max(0, profile.SafeMarginIn);
        GutterCautionIn = Math.Max(0, profile.GutterCautionIn);

        var slug = (float)(SlugIn * scale);
        var bleed = (float)(BleedIn * scale);
        var trimW = (float)(TrimWidthIn * scale);
        var trimH = (float)(TrimHeightIn * scale);

        PaperRect = SKRect.Create(origin.X, origin.Y, trimW + 2 * (bleed + slug), trimH + 2 * (bleed + slug));
        MediaRect = SKRect.Create(origin.X + slug, origin.Y + slug, trimW + 2 * bleed, trimH + 2 * bleed);
        TrimRect = SKRect.Create(MediaRect.Left + bleed, MediaRect.Top + bleed, trimW, trimH);

        var safe = (float)(SafeMarginIn * scale);
        SafeRect = new SKRect(TrimRect.Left + safe, TrimRect.Top + safe, TrimRect.Right - safe, TrimRect.Bottom - safe);

        CenterlineX = TrimRect.MidX;
        var gutter = (float)(GutterCautionIn * scale);
        GutterCautionRect = new SKRect(CenterlineX - gutter, TrimRect.Top, CenterlineX + gutter, TrimRect.Bottom);
    }

    /// <summary>The profile every constant is derived from.</summary>
    public PrintProfile Profile { get; }

    /// <summary>The page size entry the book names (R19).</summary>
    public PageSizeSpec PageSize { get; }

    /// <summary>Whether this sheet is one page or an imposed spread.</summary>
    public PageSurface Surface { get; }

    /// <summary>Device units per inch: <c>72</c> for PDF points, the preview DPI for screen pixels.</summary>
    public double Scale { get; }

    /// <summary>Device units per PostScript point — the factor style sizes in points are multiplied by.</summary>
    public double PointScale => Scale / PageGeometry.PointsPerInch;

    /// <summary>Trim width of one page, inches; half of <see cref="TrimWidthIn"/> on a spread.</summary>
    public double PageTrimWidthIn { get; }

    /// <summary>Trim width of the whole sheet, inches.</summary>
    public double TrimWidthIn { get; }

    /// <summary>Trim height, inches.</summary>
    public double TrimHeightIn { get; }

    /// <summary>Bleed per outer edge, inches.</summary>
    public double BleedIn { get; }

    /// <summary>Safe margin inside trim, inches.</summary>
    public double SafeMarginIn { get; }

    /// <summary>Gutter caution zone either side of the spread centerline, inches.</summary>
    public double GutterCautionIn { get; }

    /// <summary>Slug added outside the bleed box for crop marks, inches; <c>0</c> unless the profile asks for trim marks.</summary>
    public double SlugIn { get; }

    /// <summary>
    /// The PDF page rect — the bleed box, plus the slug when the profile draws trim marks. This is
    /// what <c>SKDocument.BeginPage</c> is sized to.
    /// </summary>
    public SKRect PaperRect { get; }

    /// <summary>The bleed box: the page background and full-bleed images paint to here (doc 12).</summary>
    public SKRect MediaRect { get; }

    /// <summary>The trim box — the final cut size, and the box normalized template coordinates span.</summary>
    public SKRect TrimRect { get; }

    /// <summary>The safe area: all text and detected faces stay inside it (kernel §3).</summary>
    public SKRect SafeRect { get; }

    /// <summary>The gutter caution zone around the spread centerline — no faces, no text.</summary>
    public SKRect GutterCautionRect { get; }

    /// <summary>The spread centerline; on a single page this is simply the page's vertical center.</summary>
    public float CenterlineX { get; }

    /// <summary>Trim width of one page in device units.</summary>
    public float PageTrimWidth => (float)(PageTrimWidthIn * Scale);

    /// <summary>
    /// A mapper for a sheet drawn at <paramref name="scale"/> device units per inch with its paper
    /// rect's top-left at <paramref name="origin"/>.
    /// </summary>
    /// <param name="profile">The print profile supplying bleed, safe margin, gutter and page sizes.</param>
    /// <param name="pageSizeId">The book's page size id; must appear in the profile (doc 12 validation).</param>
    /// <param name="scale">Device units per inch — <c>72</c> for PDF points.</param>
    /// <param name="surface">Single page or imposed spread.</param>
    /// <param name="origin">Where the paper rect's top-left lands in canvas coordinates.</param>
    public static PageGeometryMapper Create(
        PrintProfile profile,
        string pageSizeId,
        double scale,
        PageSurface surface = PageSurface.SinglePage,
        SKPoint origin = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(pageSizeId);
        if (!(scale > 0) || !double.IsFinite(scale))
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Scale must be a finite positive number.");

        var size = profile.FindPageSize(pageSizeId)
            ?? throw new ArgumentException(
                $"Print profile '{profile.Id}' does not support page size '{pageSizeId}'.", nameof(pageSizeId));
        return new PageGeometryMapper(profile, size, surface, scale, origin);
    }

    /// <summary>
    /// A mapper scaled and centered so the whole paper rect fits inside <paramref name="target"/> —
    /// the screen-preview constructor. Aspect ratio is preserved; the sheet is centered in the target.
    /// </summary>
    public static PageGeometryMapper Fit(
        PrintProfile profile,
        string pageSizeId,
        SKRect target,
        PageSurface surface = PageSurface.SinglePage)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var probe = Create(profile, pageSizeId, 1.0, surface);
        double scale = Math.Min(target.Width / probe.PaperRect.Width, target.Height / probe.PaperRect.Height);
        if (!(scale > 0) || !double.IsFinite(scale)) scale = 1.0;

        var sized = Create(profile, pageSizeId, scale, surface);
        var origin = new SKPoint(
            target.Left + (target.Width - sized.PaperRect.Width) / 2f,
            target.Top + (target.Height - sized.PaperRect.Height) / 2f);
        return Create(profile, pageSizeId, scale, surface, origin);
    }

    /// <summary>This mapper re-anchored so its paper rect's top-left sits at <paramref name="origin"/>.</summary>
    public PageGeometryMapper WithOrigin(SKPoint origin) =>
        new(Profile, PageSize, Surface, Scale, origin);

    /// <summary>This mapper at a different scale, keeping the same paper origin.</summary>
    public PageGeometryMapper WithScale(double scale) =>
        Create(Profile, PageSize.Id, scale, Surface, new SKPoint(PaperRect.Left, PaperRect.Top));

    /// <summary>Converts a length in points (the unit every <see cref="Style"/> size uses) to device units.</summary>
    public float Points(double points) => (float)(points * PointScale);

    /// <summary>Converts a length in inches to device units.</summary>
    public float Inches(double inches) => (float)(inches * Scale);

    /// <summary>Converts a device length back to inches.</summary>
    public double ToInches(double deviceLength) => Scale <= 0 ? 0 : deviceLength / Scale;

    /// <summary>
    /// The device rect a normalized page rect maps to. On a spread sheet <paramref name="half"/>
    /// selects which page the normalized coordinates belong to.
    /// </summary>
    public SKRect MapPageRect(CoreRect rect, PageHalf half = PageHalf.Full)
    {
        var (left, width) = HalfBounds(half);
        var x = left + (float)(rect.X * width);
        var y = TrimRect.Top + (float)(rect.Y * TrimRect.Height);
        return SKRect.Create(x, y, (float)(rect.W * width), (float)(rect.H * TrimRect.Height));
    }

    /// <summary>
    /// The device rect a rect normalized over the <b>whole sheet</b> maps to — used for spread-wide
    /// geometry such as a gutter-spanning photo's virtual slot.
    /// </summary>
    public SKRect MapSheetRect(CoreRect rect) =>
        SKRect.Create(
            TrimRect.Left + (float)(rect.X * TrimRect.Width),
            TrimRect.Top + (float)(rect.Y * TrimRect.Height),
            (float)(rect.W * TrimRect.Width),
            (float)(rect.H * TrimRect.Height));

    /// <summary>
    /// The device rect covering both halves of a gutter-spanning photo (R18, doc 07 <c>spanId</c>),
    /// expressed in <b>this sheet's</b> coordinates even when the facing page is not on this sheet.
    /// The shared <see cref="CropState"/> is computed over this rect, and each page then draws only
    /// the part that falls inside its own slot.
    /// </summary>
    /// <param name="leftSlot">The left page's slot rect, normalized over that page's trim box.</param>
    /// <param name="rightSlot">The right page's slot rect, normalized over that page's trim box.</param>
    /// <param name="thisSide">Which of the two pages this sheet (or half) is drawing.</param>
    public SKRect MapSpanRect(CoreRect leftSlot, CoreRect rightSlot, PairSide thisSide)
    {
        if (Surface == PageSurface.Spread)
        {
            var l = MapPageRect(leftSlot, PageHalf.Left);
            var r = MapPageRect(rightSlot, PageHalf.Right);
            return SKRect.Union(l, r);
        }

        // Single-page sheet: the facing page is imaginary, sitting one trim width to the left or right.
        var pageW = TrimRect.Width;
        var leftPageOriginX = thisSide == PairSide.Left ? TrimRect.Left : TrimRect.Left - pageW;
        var left = leftPageOriginX + (float)(leftSlot.X * pageW);
        var right = leftPageOriginX + pageW + (float)(rightSlot.Right * pageW);
        var top = TrimRect.Top + (float)(Math.Min(leftSlot.Y, rightSlot.Y) * TrimRect.Height);
        var bottom = TrimRect.Top + (float)(Math.Max(leftSlot.Bottom, rightSlot.Bottom) * TrimRect.Height);
        return new SKRect(left, top, right, bottom);
    }

    /// <summary>
    /// Applies doc 12's bleed extension to a mapped slot: any edge lying within
    /// <see cref="BleedSnapTolerance"/> of a trim edge is snapped outward to the bleed edge, so
    /// full-bleed templates survive the trimmer's ±0.0625 in tolerance. Edges that face the spine of
    /// a spread are never extended — at the spine the two halves butt-join with no inner bleed
    /// (doc 12 "Spreads").
    /// </summary>
    /// <param name="slot">The slot rect in normalized page coordinates.</param>
    /// <param name="mapped">The same slot already mapped to device units.</param>
    /// <param name="half">Which half of the sheet the page occupies.</param>
    /// <param name="spineEdge">
    /// Which edge of this page faces the spine, for a gutter-spanning slot that must butt-join rather
    /// than bleed; <c>null</c> for an ordinary slot.
    /// </param>
    public SKRect ApplyBleedExtension(CoreRect slot, SKRect mapped, PageHalf half = PageHalf.Full, PairSide? spineEdge = null)
    {
        var outerLeft = half != PageHalf.Right && spineEdge != PairSide.Left;
        var outerRight = half != PageHalf.Left && spineEdge != PairSide.Right;

        var left = outerLeft && slot.X <= BleedSnapTolerance ? MediaRect.Left : mapped.Left;
        var right = outerRight && slot.Right >= 1 - BleedSnapTolerance ? MediaRect.Right : mapped.Right;
        var top = slot.Y <= BleedSnapTolerance ? MediaRect.Top : mapped.Top;
        var bottom = slot.Bottom >= 1 - BleedSnapTolerance ? MediaRect.Bottom : mapped.Bottom;

        // A rect authored past the trim edge (bleed: true) is clamped to the bleed box, never beyond.
        return new SKRect(
            Math.Max(left, MediaRect.Left),
            Math.Max(top, MediaRect.Top),
            Math.Min(right, MediaRect.Right),
            Math.Min(bottom, MediaRect.Bottom));
    }

    /// <summary>
    /// The region one page owns on this sheet, out to the bleed box on its outer edges — what the
    /// page's background paints and what its drawing is clipped to.
    /// </summary>
    public SKRect PageMediaRect(PageHalf half) => half switch
    {
        PageHalf.Left => new SKRect(MediaRect.Left, MediaRect.Top, CenterlineX, MediaRect.Bottom),
        PageHalf.Right => new SKRect(CenterlineX, MediaRect.Top, MediaRect.Right, MediaRect.Bottom),
        _ => MediaRect,
    };

    /// <summary>The physical size of a normalized page rect, in inches — the input to effective-DPI math.</summary>
    public (double WidthIn, double HeightIn) PhysicalSizeIn(CoreRect rect, PageHalf half = PageHalf.Full)
    {
        var pageWidthIn = half == PageHalf.Full && Surface == PageSurface.SinglePage
            ? TrimWidthIn
            : PageTrimWidthIn;
        return (rect.W * pageWidthIn, rect.H * TrimHeightIn);
    }

    /// <summary>The physical size of an already-mapped device rect, in inches.</summary>
    public (double WidthIn, double HeightIn) PhysicalSizeIn(SKRect deviceRect) =>
        (ToInches(deviceRect.Width), ToInches(deviceRect.Height));

    private (float Left, float Width) HalfBounds(PageHalf half)
    {
        if (Surface == PageSurface.SinglePage || half == PageHalf.Full)
            return (TrimRect.Left, TrimRect.Width);
        var halfWidth = TrimRect.Width / 2f;
        return half == PageHalf.Left ? (TrimRect.Left, halfWidth) : (TrimRect.Left + halfWidth, halfWidth);
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"{PageSize.Id} {Surface} @ {Scale:0.###}/in — media {MediaRect.Width:0.##}×{MediaRect.Height:0.##}, trim {TrimRect.Width:0.##}×{TrimRect.Height:0.##}";
}
