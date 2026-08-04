namespace PhotoBook.Core.Model;

/// <summary>
/// The geometry and export constants of kernel §3, in one place so no doc, template or renderer has
/// to restate them. Physical values are inches; normalized values are fractions of the trim box for
/// the default <c>11x8.5-landscape</c> page.
/// </summary>
public static class PageGeometry
{
    /// <summary>The v1 page-size id (kernel §2).</summary>
    public const string DefaultPageSizeId = "11x8.5-landscape";

    /// <summary>Single-page trim width, inches.</summary>
    public const double TrimWidthIn = 11.0;

    /// <summary>Single-page trim height, inches.</summary>
    public const double TrimHeightIn = 8.5;

    /// <summary>Spread trim width, inches — two facing pages as one panorama.</summary>
    public const double SpreadWidthIn = 22.0;

    /// <summary>Bleed per outer edge, inches; the single-page bleed box is 11.25 × 8.75 in.</summary>
    public const double BleedIn = 0.125;

    /// <summary>Safe margin inside trim, inches — text and faces stay inside.</summary>
    public const double SafeMarginIn = 0.375;

    /// <summary>Gutter caution zone, inches either side of the spread centerline — no faces or text.</summary>
    public const double GutterCautionIn = 0.5;

    /// <summary>Export resolution in dots per inch.</summary>
    public const int ExportDpi = 300;

    /// <summary>JPEG quality of images embedded in the PDF.</summary>
    public const int ExportJpegQuality = 90;

    /// <summary>Effective DPI below which preflight warns (doc 12).</summary>
    public const int MinimumEffectiveDpi = 200;

    /// <summary>PostScript points per inch.</summary>
    public const double PointsPerInch = 72.0;

    /// <summary>Bleed as a fraction of trim width (0.0114).</summary>
    public static double BleedNormalizedX => BleedIn / TrimWidthIn;

    /// <summary>Bleed as a fraction of trim height (0.0147).</summary>
    public static double BleedNormalizedY => BleedIn / TrimHeightIn;

    /// <summary>Safe margin as a fraction of trim width (0.0341).</summary>
    public static double SafeMarginNormalizedX => SafeMarginIn / TrimWidthIn;

    /// <summary>Safe margin as a fraction of trim height (0.0441).</summary>
    public static double SafeMarginNormalizedY => SafeMarginIn / TrimHeightIn;

    /// <summary>Gutter caution as a fraction of trim width (0.0455).</summary>
    public static double GutterCautionNormalizedX => GutterCautionIn / TrimWidthIn;

    /// <summary>The trim box in normalized page space — the unit square.</summary>
    public static Rect TrimBoxNormalized => Rect.Unit;

    /// <summary>The bleed box in normalized page space; it extends outside the unit square.</summary>
    public static Rect BleedBoxNormalized => new(
        -BleedNormalizedX, -BleedNormalizedY,
        1 + 2 * BleedNormalizedX, 1 + 2 * BleedNormalizedY);

    /// <summary>The safe area in normalized page space — text and faces must stay inside it.</summary>
    public static Rect SafeAreaNormalized => new(
        SafeMarginNormalizedX, SafeMarginNormalizedY,
        1 - 2 * SafeMarginNormalizedX, 1 - 2 * SafeMarginNormalizedY);

    /// <summary>The default page's aspect ratio (width / height).</summary>
    public static double DefaultPageAspect => TrimWidthIn / TrimHeightIn;

    /// <summary>
    /// The physical aspect of a normalized rect on a page of the given trim size — the value an
    /// <see cref="ImageSlot.Aspect"/> must agree with (doc 07 linter rule L5).
    /// </summary>
    public static double PhysicalAspect(Rect normalizedRect, double trimWidthIn = TrimWidthIn, double trimHeightIn = TrimHeightIn) =>
        normalizedRect.H <= 0 ? double.NaN
            : (normalizedRect.W * trimWidthIn) / (normalizedRect.H * trimHeightIn);

    /// <summary>The physical size, in inches, of a normalized rect on a page of the given trim size.</summary>
    public static (double WidthIn, double HeightIn) PhysicalSize(Rect normalizedRect, double trimWidthIn = TrimWidthIn, double trimHeightIn = TrimHeightIn) =>
        (normalizedRect.W * trimWidthIn, normalizedRect.H * trimHeightIn);
}
