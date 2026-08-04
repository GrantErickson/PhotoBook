namespace PhotoBook.Core.Model;

/// <summary>
/// An entry that carried user data — a manual date or an exclusion — but matched nothing on
/// re-import; the Import Report offers re-attach or discard (doc 11).
/// </summary>
public sealed record OrphanedUserAssignment
{
    /// <summary>The old <see cref="JournalEntry.Id"/>.</summary>
    public string EntryId { get; set; } = string.Empty;

    /// <summary>A one-line preview of the old entry text, so the user can recognize it.</summary>
    public string TextPreview { get; set; } = string.Empty;

    /// <summary>The user date that would otherwise be lost.</summary>
    public DateOnly? UserDate { get; set; }

    /// <summary>Whether the old entry was excluded.</summary>
    public bool Excluded { get; set; }
}
