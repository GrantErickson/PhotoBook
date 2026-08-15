namespace PhotoBook.Core.Model;

/// <summary>
/// An interesting area of a photo that smart-crop must keep visible (R25, kernel §4). Stored on the
/// <see cref="Photo"/> in <c>photos.json</c>: fused regions are user-editable state, while raw
/// detector output lives in <c>cache/</c> only (doc 04 §8).
/// </summary>
public sealed record FocusRegion
{
    /// <summary>The region in normalized image coordinates <c>[0,1] × [0,1]</c>, origin top-left.</summary>
    public Rect Rect { get; set; }

    /// <summary>Importance in <c>[0,1]</c>; the highest-weight region of the highest priority kind is primary.</summary>
    public double Weight { get; set; }

    /// <summary>Which detector — or the user — proposed this region.</summary>
    public FocusKind Kind { get; set; } = FocusKind.Saliency;

    /// <summary>The person's name; set only when <see cref="Kind"/> is <see cref="FocusKind.Person"/>.</summary>
    public string? PersonName { get; set; }

    /// <summary>Fusion sort rank: 0 = user … 3 = saliency (kernel §4).</summary>
    public int KindPriority => Kind switch
    {
        FocusKind.User => 0,
        FocusKind.Person => 1,
        FocusKind.Face => 2,
        _ => 3,
    };
}
