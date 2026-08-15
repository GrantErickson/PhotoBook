namespace PhotoBook.Core.Model;

/// <summary>
/// A print service's specifics as data, not code (kernel §11, doc 12): page sizes, bleed and safe
/// geometry, page-count rules, color intent and image encoding. Profiles ship as JSON beside the app
/// and are referenced by id from <see cref="Book.PrintProfileRef"/>, so "same book, different
/// printer" is a re-export rather than a rebuild.
/// </summary>
public sealed record PrintProfile
{
    /// <summary>The id of the generic profile shipped with the app.</summary>
    public const string GenericId = "generic-11x8.5";

    /// <summary>Schema version of a profile document; currently 1.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Stable profile id, referenced by <see cref="Book.PrintProfileRef"/>.</summary>
    public string Id { get; set; } = GenericId;

    /// <summary>Human display name.</summary>
    public string Name { get; set; } = "Generic 11×8.5 landscape";

    /// <summary>The page sizes this service prints; the book's page size id must appear here (R19).</summary>
    public IList<PageSizeSpec> PageSizes { get; set; } = new List<PageSizeSpec> { new() };

    /// <summary>Bleed per outer edge, inches.</summary>
    public double BleedIn { get; set; } = PageGeometry.BleedIn;

    /// <summary>Safe margin inside trim, inches.</summary>
    public double SafeMarginIn { get; set; } = PageGeometry.SafeMarginIn;

    /// <summary>Gutter caution zone each side of the spread centerline, inches.</summary>
    public double GutterCautionIn { get; set; } = PageGeometry.GutterCautionIn;

    /// <summary>Minimum page count the service accepts; null for no minimum.</summary>
    public int? MinPages { get; set; } = 20;

    /// <summary>Maximum page count the service accepts; null for no maximum.</summary>
    public int? MaxPages { get; set; } = 200;

    /// <summary>Required page-count multiple, e.g. 2 or 4; null for none.</summary>
    public int? PageCountMultiple { get; set; } = 2;

    /// <summary>Color intent; v1 supports <c>sRGB</c> only.</summary>
    public string ColorIntent { get; set; } = "sRGB";

    /// <summary>Target image resolution in dots per inch.</summary>
    public int ImageDpi { get; set; } = PageGeometry.ExportDpi;

    /// <summary>JPEG quality for images embedded in the PDF.</summary>
    public int JpegQuality { get; set; } = PageGeometry.ExportJpegQuality;

    /// <summary>True to add a slug area and draw crop marks.</summary>
    public bool IncludeTrimMarks { get; set; }

    /// <summary>Whether the PDF contains single pages or imposed spreads.</summary>
    public PdfOutputMode Output { get; set; } = PdfOutputMode.SinglePages;

    /// <summary>Reserved: spine-width calculation arrives with covers in v2 (kernel §11).</summary>
    public string? Spine { get; set; }

    /// <summary>The named page size, or null when this profile does not support it.</summary>
    public PageSizeSpec? FindPageSize(string pageSizeId) =>
        PageSizes.FirstOrDefault(s => string.Equals(s.Id, pageSizeId, StringComparison.Ordinal));

    /// <summary>True when the page count satisfies the service's min, max and multiple rules (doc 12 preflight).</summary>
    public bool IsPageCountValid(int pageCount) =>
        (MinPages is null || pageCount >= MinPages) &&
        (MaxPages is null || pageCount <= MaxPages) &&
        (PageCountMultiple is null or <= 0 || pageCount % PageCountMultiple.Value == 0);
}
