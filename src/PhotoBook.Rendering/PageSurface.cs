namespace PhotoBook.Rendering;

/// <summary>
/// What one rendered sheet contains: a single page, or the 22 × 8.5 in panorama of two facing pages
/// (kernel §3, doc 12 "Spreads and full-spread photos").
/// </summary>
public enum PageSurface
{
    /// <summary>One page — trim 11 × 8.5 in by default, bleed on all four edges.</summary>
    SinglePage,

    /// <summary>Two facing pages imposed as one sheet — trim 22 × 8.5 in, bleed on the outer edges.</summary>
    Spread,
}

/// <summary>
/// Which half of the rendered surface a page occupies. <see cref="Full"/> is the only legal value on
/// a <see cref="PageSurface.SinglePage"/> sheet; <see cref="Left"/> and <see cref="Right"/> place a
/// page into one half of a <see cref="PageSurface.Spread"/> sheet.
/// </summary>
public enum PageHalf
{
    /// <summary>The page owns the whole surface.</summary>
    Full,

    /// <summary>The verso: normalized <c>x ∈ [0,1]</c> maps to the left half of the spread trim.</summary>
    Left,

    /// <summary>The recto: normalized <c>x ∈ [0,1]</c> maps to the right half of the spread trim.</summary>
    Right,
}
