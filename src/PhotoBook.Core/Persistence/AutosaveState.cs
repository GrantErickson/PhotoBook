namespace PhotoBook.Core.Persistence;

/// <summary>
/// What the autosave loop is doing right now (doc 04 §6). The shell shows this verbatim, so every
/// value has to be honest: there is no "Saved" while a write is still in flight and no "Saved"
/// after a write that threw.
/// </summary>
public enum AutosaveState
{
    /// <summary>No project is open; there is nothing to save and nothing to show.</summary>
    Closed,

    /// <summary>Everything in memory is on disk. The shell says "Saved".</summary>
    Saved,

    /// <summary>Edits are waiting for the next tick or flush. The shell says "Unsaved changes".</summary>
    Dirty,

    /// <summary>A write is in flight. The shell says "Saving…".</summary>
    Saving,

    /// <summary>
    /// The last write failed and the edits are still only in memory. The shell must say so — the
    /// next tick retries, but silence here is the one lie that loses a book.
    /// </summary>
    Failed,
}
