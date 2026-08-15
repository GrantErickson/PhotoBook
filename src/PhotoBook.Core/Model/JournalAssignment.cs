namespace PhotoBook.Core.Model;

/// <summary>
/// Binds a day's journal entries to a journal text slot on the page. Only journal bindings need
/// storage: month titles resolve from <see cref="Chapter.Title"/> and captions from the placed
/// photo (doc 03 §4).
/// </summary>
public sealed record JournalAssignment
{
    /// <summary>The <see cref="TextSlot.Id"/> in the page's effective template.</summary>
    public string TextSlotId { get; set; } = string.Empty;

    /// <summary>The <see cref="JournalEntry.Id"/> values rendered there, in order.</summary>
    public IList<string> EntryIds { get; set; } = new List<string>();
}
