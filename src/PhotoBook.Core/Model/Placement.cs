namespace PhotoBook.Core.Model;

/// <summary>
/// The single stored link between the photo world and the page world (doc 03 §4).
/// One placement binds exactly one <see cref="Photo"/> to exactly one <see cref="ImageSlot"/>; a
/// slot holds at most one placement (slots without one are empty and flagged amber, R14), and a
/// photo appears in at most one placement in the entire book — drag-drop between filled slots swaps
/// placements (R9).
/// </summary>
public sealed record Placement
{
    /// <summary>The <see cref="ImageSlot.Id"/> in the page's effective template.</summary>
    public string SlotId { get; set; } = string.Empty;

    /// <summary>The <see cref="Photo.Id"/> placed in that slot.</summary>
    public string PhotoId { get; set; } = string.Empty;

    /// <summary>
    /// How the photo is framed in the slot (kernel §4). Auto-layout emits the same value the user
    /// hand-tweaks; the arithmetic lives in <see cref="CropMath"/>.
    /// </summary>
    public CropState Crop { get; set; } = CropState.Default;
}
