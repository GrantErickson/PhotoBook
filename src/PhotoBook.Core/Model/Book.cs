using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoBook.Core.Model;

/// <summary>
/// One book = one calendar year (R3), serialized as <c>book.json</c>. The book owns the global style
/// level, the page size, the print-profile reference and the engine seed that makes layout
/// reproducible.
/// </summary>
public sealed record Book
{
    /// <summary>Schema version of <c>book.json</c>; currently 1 (doc 04 §5).</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Opaque stable book id, e.g. <c>bk-…</c>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The book's display title, e.g. "Our 2024".</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The calendar year this book covers (R3).</summary>
    public int Year { get; set; }

    /// <summary>The page-size id; <c>11x8.5-landscape</c> by default. Templates apply only to matching sizes (R19).</summary>
    public string PageSize { get; set; } = PageGeometry.DefaultPageSizeId;

    /// <summary>The global level of the style cascade (R23).</summary>
    public Style Style { get; set; } = BuiltInStyles.Default;

    /// <summary>The <see cref="PrintProfile.Id"/> used for export.</summary>
    public string PrintProfileRef { get; set; } = PrintProfile.GenericId;

    /// <summary>
    /// The engine seed, generated once at book creation and never changed: same inputs plus same seed
    /// produce the same book (kernel §7).
    /// </summary>
    public ulong Seed { get; set; }

    /// <summary>The PDF metadata timestamp, written once at book creation so exports stay byte-stable (kernel §11).</summary>
    public DateTime PdfTimestampUtc { get; set; }

    /// <summary>Where the book's photos come from.</summary>
    public BookSource Source { get; set; } = new();

    /// <summary>Per-book analysis selection.</summary>
    public AnalysisSettings Analysis { get; set; } = new();

    /// <summary>
    /// Members written by a newer minor revision of the app, preserved verbatim on round-trip so an
    /// older build never silently deletes them (doc 04 §4 rule 6).
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalData { get; set; }
}
