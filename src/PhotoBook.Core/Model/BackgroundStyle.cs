namespace PhotoBook.Core.Model;

/// <summary>
/// The page background (doc 10 §6, R21) — the renderer's first pass on every page, with everything
/// else composited above it.
/// </summary>
public sealed record BackgroundStyle
{
    /// <summary>Background kind; v1 renders <see cref="BackgroundKind.Solid"/> only.</summary>
    public BackgroundKind? Kind { get; set; }

    /// <summary>Fill color as <c>#RRGGBB</c> sRGB; <c>#000000</c> in v1.</summary>
    public string? Color { get; set; }
}
