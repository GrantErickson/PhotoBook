using System.Globalization;
using System.Text.Json.Serialization;

namespace PhotoBook.Core.Model;

/// <summary>
/// One page size a <see cref="PrintProfile"/> supports (doc 12). Templates are authored per size id
/// and never re-stretched across aspect families, which is how R19's multiple page sizes stay
/// consistent end to end.
/// </summary>
public sealed record PageSizeSpec
{
    /// <summary>The size id referenced by <see cref="Book.PageSize"/> and <see cref="Template.PageSize"/>.</summary>
    public string Id { get; set; } = PageGeometry.DefaultPageSizeId;

    /// <summary>Trim width in inches.</summary>
    public double TrimWidthIn { get; set; } = PageGeometry.TrimWidthIn;

    /// <summary>Trim height in inches.</summary>
    public double TrimHeightIn { get; set; } = PageGeometry.TrimHeightIn;

    /// <summary>
    /// Trim aspect, width ÷ height. This is the number that decides whether a template set can be
    /// shared: doc 12 refuses to re-flow a composition across aspect families, so two sizes with
    /// different aspects each need their own authored variants.
    /// </summary>
    [JsonIgnore]
    public double Aspect => TrimHeightIn <= 0 ? double.NaN : TrimWidthIn / TrimHeightIn;

    /// <summary>How the page reads — <c>landscape</c>, <c>portrait</c> or <c>square</c>.</summary>
    [JsonIgnore]
    public string Orientation =>
        Math.Abs(TrimWidthIn - TrimHeightIn) < 0.01 ? "square"
            : TrimWidthIn > TrimHeightIn ? "landscape"
            : "portrait";

    /// <summary>
    /// A human label for a picker — "11 × 8.5 in landscape". Built from the trim rather than the id
    /// so a size added to a profile needs no matching string table.
    /// </summary>
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            var w = TrimWidthIn.ToString("0.##", CultureInfo.CurrentCulture);
            var h = TrimHeightIn.ToString("0.##", CultureInfo.CurrentCulture);
            return Orientation == "square"
                ? $"{w} × {h} in square"
                : $"{w} × {h} in {Orientation}";
        }
    }
}
