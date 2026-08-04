namespace PhotoBook.Core.Model;

/// <summary>
/// A named person in a photo (doc 03 §3). A tag carrying a <see cref="RegionRect"/> is materialized
/// during analysis fusion as a <see cref="FocusRegion"/> of kind <see cref="FocusKind.Person"/>,
/// making named people top-priority crop anchors — below only explicit user regions. Tags without a
/// rect cannot be localized and contribute a small quality bonus only (doc 06).
/// </summary>
public sealed record PersonTag
{
    /// <summary>The person's name as supplied by the source.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Who supplied the tag.</summary>
    public PersonTagSource Source { get; set; } = PersonTagSource.OneDrive;

    /// <summary>The person's box in normalized image coordinates, when the source supplied one.</summary>
    public Rect? RegionRect { get; set; }
}
