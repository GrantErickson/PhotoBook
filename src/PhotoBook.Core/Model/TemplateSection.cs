namespace PhotoBook.Core.Model;

/// <summary>
/// One day's group of slots inside a <see cref="TemplateKind.MultiDay"/> template (doc 07, R28).
/// Sections never share photos or text across days — that is the R28 invariant, enforced structurally
/// rather than by scoring.
/// </summary>
public sealed record TemplateSection
{
    /// <summary>Unique within the template; conventionally <c>d1..dN</c> in day (reading) order.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The image slots belonging to this day; at least one.</summary>
    public IList<string> SlotIds { get; set; } = new List<string>();

    /// <summary>The journal text slot for this day; zero or one entry. Empty means a photos-only day.</summary>
    public IList<string> TextSlotIds { get; set; } = new List<string>();
}
