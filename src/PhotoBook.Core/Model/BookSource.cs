namespace PhotoBook.Core.Model;

/// <summary>
/// The photo source a book is bound to. It exists only to sync <em>more</em> photos: once bytes are
/// copied into <c>originals/</c> nothing downstream reads the source again, so re-linking is a
/// non-problem (doc 04 §7).
/// </summary>
public sealed record BookSource
{
    /// <summary>The kind of source.</summary>
    public BookSourceKind Kind { get; set; } = BookSourceKind.LocalFolder;

    /// <summary>Source-system id: the Graph album or folder item id; null for a local folder.</summary>
    public string? Id { get; set; }

    /// <summary>Human-readable location, e.g. <c>Book 2024</c> or a local path.</summary>
    public string? Path { get; set; }

    /// <summary>Graph delta token from the last folder sync, when the source supports delta (doc 05).</summary>
    public string? DeltaLink { get; set; }
}
