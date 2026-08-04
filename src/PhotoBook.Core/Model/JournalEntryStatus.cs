namespace PhotoBook.Core.Model;

/// <summary>How confidently a journal entry's date was established (doc 11).</summary>
public enum JournalEntryStatus
{
    /// <summary>Final confidence 0.70 or better — used as-is.</summary>
    Matched,

    /// <summary>Confidence 0.40–0.69 — provisionally assigned but flagged in the Import Report.</summary>
    Ambiguous,

    /// <summary>No date could be found; the entry does not appear in the book until the user dates it.</summary>
    Unmatched,

    /// <summary>The user assigned the date by hand; confidence is 1.00 and re-import preserves it.</summary>
    UserAssigned,
}
