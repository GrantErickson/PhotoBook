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
}
