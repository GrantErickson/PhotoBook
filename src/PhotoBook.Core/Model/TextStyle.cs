namespace PhotoBook.Core.Model;

/// <summary>
/// Type appearance for one text role (doc 10 §1). Every field is nullable because chapter- and
/// page-level styles are <b>sparse</b>: only the leaves the user changed are stored, and the rest
/// fall through the cascade (doc 10 §2).
/// </summary>
public sealed record TextStyle
{
    /// <summary>Font family; one of the three bundled OFL families (doc 10 §3).</summary>
    public string? Family { get; set; }

    /// <summary>Size in points at print scale (1 pt = 1/72 in on the trim box).</summary>
    public double? SizePt { get; set; }

    /// <summary>Text color as <c>#RRGGBB</c> sRGB.</summary>
    public string? Color { get; set; }

    /// <summary>Weight name, e.g. <c>"regular"</c>.</summary>
    public string? Weight { get; set; }

    /// <summary>Line height as a multiple of the font size.</summary>
    public double? LineHeight { get; set; }
}
