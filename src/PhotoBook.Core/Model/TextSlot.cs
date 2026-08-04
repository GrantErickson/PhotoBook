namespace PhotoBook.Core.Model;

/// <summary>
/// A place for text on a page (kernel §6, doc 07). A text slot carries no typography of its own —
/// family, size and color come from the <see cref="Style"/> cascade (R23) — and no declared
/// capacity: text fit is measured by the engine and is a hard filter (doc 11).
/// </summary>
public sealed record TextSlot
{
    /// <summary>Unique within the template; conventionally <c>t1..tN</c>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The text box in page space, normalized over the trim box; clears the safe area and the gutter caution zone.</summary>
    public Rect Rect { get; set; }

    /// <summary>What this slot holds.</summary>
    public TextRole Role { get; set; } = TextRole.Journal;

    /// <summary>Alignment; null means the role default (left for journal and caption, center for month titles).</summary>
    public TextAlign? Align { get; set; }

    /// <summary>Caption role only: the id of the <see cref="ImageSlot"/> this caption belongs to.</summary>
    public string? AttachedTo { get; set; }
}
