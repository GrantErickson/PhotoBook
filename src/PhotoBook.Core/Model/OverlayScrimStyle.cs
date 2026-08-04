namespace PhotoBook.Core.Model;

/// <summary>
/// The gradient behind overlay captions and overlapping month titles (doc 10 §4): black, from 0% at
/// the scrim's top edge to <see cref="MaxOpacity"/> at the photo's bottom edge, so white text stays
/// legible on any image.
/// </summary>
public sealed record OverlayScrimStyle
{
    /// <summary>Whether the gradient is drawn; <c>false</c> leaves raw overlay text.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Opacity at the photo's bottom edge, <c>0..1</c>.</summary>
    public double? MaxOpacity { get; set; }

    /// <summary>Inset of the text from the photo's left and bottom edges, in points; also sets the scrim height.</summary>
    public double? PaddingPt { get; set; }
}
