namespace PhotoBook.Core.Model;

/// <summary>
/// The border drawn around every placed photo in the level's scope (doc 10 §5, R23) — one switch,
/// every image. The stroke runs <em>inside</em> the visible image rect, so enabling borders never
/// changes layout geometry or crop math, and it wraps the visible image rather than the slot when a
/// photo is letterboxed.
/// </summary>
public sealed record ImageBorderStyle
{
    /// <summary>Whether borders are drawn at all.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Stroke width in points.</summary>
    public double? WidthPt { get; set; }

    /// <summary>Stroke color as <c>#RRGGBB</c> sRGB.</summary>
    public string? Color { get; set; }

    /// <summary>Corner radius in points; greater than 0 also clips the photo to the rounded rect.</summary>
    public double? CornerRadiusPt { get; set; }
}
