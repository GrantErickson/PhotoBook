namespace PhotoBook.Core.Model;

/// <summary>
/// The user-owned half of one <see cref="JournalEntry"/> — everything a manual date action writes and
/// everything an undo has to put back. Captured by value before an edit runs, exactly as doc 11's
/// re-import contract carries these four fields (and only these four) forward.
/// </summary>
/// <param name="UserDate">The manual date assignment, or null when the parser's date stands.</param>
/// <param name="Excluded">Whether the user removed the entry from the book.</param>
/// <param name="Status">The entry's confidence bucket.</param>
/// <param name="Confidence">The match confidence; 1.00 once the user has spoken.</param>
public readonly record struct JournalEntryUserState(
    DateOnly? UserDate, bool Excluded, JournalEntryStatus Status, double Confidence);

/// <summary>
/// The manual half of journal ingestion (doc 11 §"Import Report UI"): assigning a date the matcher
/// could not read, discarding an entry, and re-attaching a user assignment that a re-import orphaned.
///
/// <para>
/// This is deliberately domain code rather than view-model code. Doc 11's contract — <b>the document
/// owns the text; the user owns the dates</b> — means a manual action may write only
/// <see cref="JournalEntry.UserDate"/>, <see cref="JournalEntry.Excluded"/>,
/// <see cref="JournalEntry.Status"/> and <see cref="JournalEntry.Confidence"/>. Machine fields
/// (<see cref="JournalEntry.DateStart"/>, <see cref="JournalEntry.MatchedBy"/>,
/// <see cref="JournalEntry.Paragraphs"/>) are never touched here, because they are regenerated from
/// the document on the next import and any local change would simply be overwritten.
/// </para>
///
/// <para>
/// Every mutation leaves the document in the same shape the parser produces: entries sorted by
/// effective date then id, and <see cref="JournalDocument.ImportReport"/> recounted — so the report
/// stays truthful after an assignment instead of freezing at whatever the import said. Both are pure
/// functions of the entries, which is what makes undo a matter of restoring the four captured fields
/// and running them again.
/// </para>
/// </summary>
public static class JournalEdits
{
    /// <summary>The confidence a manual date action records — exactly 1.00 per doc 11.</summary>
    public const double UserAssignedConfidence = 1.00;

    /// <summary>Reads the four user-owned fields of an entry, for an undo command's before-state.</summary>
    /// <param name="entry">The entry to snapshot.</param>
    public static JournalEntryUserState Capture(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new JournalEntryUserState(entry.UserDate, entry.Excluded, entry.Status, entry.Confidence);
    }

    /// <summary>
    /// Writes a captured state back onto an entry and re-normalizes the document. This is the undo
    /// path for every edit below, so it must restore the exact prior state and nothing else.
    /// </summary>
    /// <param name="journal">The document the entry belongs to.</param>
    /// <param name="entry">The entry being restored.</param>
    /// <param name="state">The state captured before the edit.</param>
    /// <param name="bookYear">The book's year, for the out-of-year count (R3).</param>
    public static void Restore(
        JournalDocument journal, JournalEntry entry, JournalEntryUserState state, int bookYear)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(entry);

        entry.UserDate = state.UserDate;
        entry.Excluded = state.Excluded;
        entry.Status = state.Status;
        entry.Confidence = state.Confidence;

        Normalize(journal, bookYear);
    }

    /// <summary>
    /// Assigns a date by hand: sets <see cref="JournalEntry.UserDate"/>, the
    /// <see cref="JournalEntryStatus.UserAssigned"/> status and confidence 1.00 (doc 11). The date the
    /// matcher read is left untouched so a re-import can still refresh it, and so the report can go on
    /// showing what the document actually said.
    /// </summary>
    /// <param name="journal">The document the entry belongs to.</param>
    /// <param name="entry">The entry being dated.</param>
    /// <param name="date">The date the user chose.</param>
    /// <param name="bookYear">The book's year, for the out-of-year count (R3).</param>
    public static void AssignDate(JournalDocument journal, JournalEntry entry, DateOnly date, int bookYear)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(entry);

        entry.UserDate = date;
        entry.Status = JournalEntryStatus.UserAssigned;
        entry.Confidence = UserAssignedConfidence;

        // A dated entry is one the user wants in the book; leaving it excluded would date text that
        // still renders nowhere, which reads as the assignment having done nothing.
        entry.Excluded = false;

        Normalize(journal, bookYear);
    }

    /// <summary>
    /// Discards an entry, or puts it back. An excluded entry is treated exactly like a day with no
    /// entry (doc 11 §"No-photo days"): it keeps its text and its identity, so a later re-import still
    /// recognizes it and the user can change their mind.
    /// </summary>
    /// <param name="journal">The document the entry belongs to.</param>
    /// <param name="entry">The entry being discarded or restored.</param>
    /// <param name="excluded">True to remove it from the book.</param>
    /// <param name="bookYear">The book's year, for the out-of-year count (R3).</param>
    public static void SetExcluded(JournalDocument journal, JournalEntry entry, bool excluded, int bookYear)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(entry);

        entry.Excluded = excluded;
        Normalize(journal, bookYear);
    }

    /// <summary>
    /// Moves an orphaned user assignment onto an entry of the freshly imported document (doc 11
    /// §"Re-import semantics" step 4) and drops it from the report.
    /// </summary>
    /// <param name="journal">The document holding both the orphan and the target.</param>
    /// <param name="orphan">The assignment a re-import could not place.</param>
    /// <param name="target">The entry that should receive it.</param>
    /// <param name="bookYear">The book's year, for the out-of-year count (R3).</param>
    /// <returns>False when the orphan is not in this document's report.</returns>
    public static bool ReattachOrphan(
        JournalDocument journal, OrphanedUserAssignment orphan, JournalEntry target, int bookYear)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(orphan);
        ArgumentNullException.ThrowIfNull(target);

        if (!journal.ImportReport.OrphanedUserAssignments.Remove(orphan))
        {
            return false;
        }

        if (orphan.UserDate is { } date)
        {
            target.UserDate = date;
            target.Status = JournalEntryStatus.UserAssigned;
            target.Confidence = UserAssignedConfidence;
        }

        target.Excluded = orphan.Excluded;
        Normalize(journal, bookYear);
        return true;
    }

    /// <summary>Drops an orphaned assignment without applying it — the user let it go.</summary>
    /// <param name="journal">The document holding the report.</param>
    /// <param name="orphan">The assignment to forget.</param>
    /// <returns>False when the orphan is not in this document's report.</returns>
    public static bool DiscardOrphan(JournalDocument journal, OrphanedUserAssignment orphan)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(orphan);
        return journal.ImportReport.OrphanedUserAssignments.Remove(orphan);
    }

    /// <summary>Puts an orphan back at a known position — the undo of a re-attach or a discard.</summary>
    /// <param name="journal">The document holding the report.</param>
    /// <param name="orphan">The assignment to restore.</param>
    /// <param name="index">Where it sat in the report's list.</param>
    public static void RestoreOrphan(JournalDocument journal, OrphanedUserAssignment orphan, int index)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(orphan);

        var orphans = journal.ImportReport.OrphanedUserAssignments;
        orphans.Insert(Math.Clamp(index, 0, orphans.Count), orphan);
    }

    /// <summary>
    /// Restores the invariants the parser leaves behind: entries sorted by effective date then id, and
    /// an import report that matches the entries as they now stand.
    /// </summary>
    /// <param name="journal">The document to normalize.</param>
    /// <param name="bookYear">The book's year, for the out-of-year count (R3).</param>
    public static void Normalize(JournalDocument journal, int bookYear)
    {
        ArgumentNullException.ThrowIfNull(journal);
        Sort(journal);
        Recount(journal, bookYear);
    }

    /// <summary>Sorts the entries the way the parser stores them: effective date, then id.</summary>
    /// <param name="journal">The document to sort in place.</param>
    public static void Sort(JournalDocument journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        journal.Entries = [.. journal.Entries
            .OrderBy(e => e.EffectiveDate)
            .ThenBy(e => e.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Recomputes the report's four counts from the entries, using the parser's own rules so a report
    /// refreshed after a manual assignment is indistinguishable from one a re-import would produce.
    /// Orphaned assignments are left alone — they are import history, not a count.
    /// </summary>
    /// <param name="journal">The document whose report is rewritten.</param>
    /// <param name="bookYear">The book's year (R3).</param>
    public static void Recount(JournalDocument journal, int bookYear)
    {
        ArgumentNullException.ThrowIfNull(journal);

        var report = journal.ImportReport;
        report.Matched = journal.Entries.Count(e =>
            e.Status is JournalEntryStatus.Matched or JournalEntryStatus.UserAssigned);
        report.Ambiguous = journal.Entries.Count(e => e.Status == JournalEntryStatus.Ambiguous);
        report.Unmatched = journal.Entries.Count(e => e.Status == JournalEntryStatus.Unmatched);
        report.OutOfYear = journal.Entries.Count(e =>
            e.Status != JournalEntryStatus.Unmatched && e.EffectiveDate.Year != bookYear);
    }

    /// <summary>
    /// The entries that still need the user: no date at all, a date the matcher was unsure of, or a
    /// date outside the book's year. In report order — worst first — so the list reads as a worklist.
    /// </summary>
    /// <param name="journal">The document to scan.</param>
    /// <param name="bookYear">The book's year (R3).</param>
    public static IEnumerable<JournalEntry> NeedingAttention(JournalDocument journal, int bookYear)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return journal.Entries
            .Where(e => Bucket(e, bookYear) is
                JournalReviewBucket.Undated or JournalReviewBucket.Ambiguous or JournalReviewBucket.OutOfYear)
            .OrderBy(e => (int)Bucket(e, bookYear))
            .ThenBy(e => e.EffectiveDate)
            .ThenBy(e => e.Id, StringComparer.Ordinal);
    }

    /// <summary>Which review group an entry belongs to.</summary>
    /// <param name="entry">The entry to classify.</param>
    /// <param name="bookYear">The book's year (R3).</param>
    public static JournalReviewBucket Bucket(JournalEntry entry, int bookYear)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Excluded) return JournalReviewBucket.Discarded;
        if (entry.Status == JournalEntryStatus.Unmatched) return JournalReviewBucket.Undated;
        if (entry.Status == JournalEntryStatus.Ambiguous) return JournalReviewBucket.Ambiguous;
        return entry.EffectiveDate.Year == bookYear
            ? JournalReviewBucket.Dated
            : JournalReviewBucket.OutOfYear;
    }
}

/// <summary>
/// The groups the Import Report lists entries under (doc 11 §"Import Report UI"), ordered by how much
/// they need the user: an entry with no date at all is silently absent from the book, so it comes first.
/// </summary>
public enum JournalReviewBucket
{
    /// <summary>No date could be read — the entry appears nowhere until the user dates it.</summary>
    Undated,

    /// <summary>Provisionally dated at 0.40–0.69 confidence; the matcher's alternates are offered.</summary>
    Ambiguous,

    /// <summary>Dated outside the book's year (R3), so it falls in no chapter of this book.</summary>
    OutOfYear,

    /// <summary>The user removed it from the book; it keeps its text and can be brought back.</summary>
    Discarded,

    /// <summary>Dated confidently, or dated by the user — nothing to do.</summary>
    Dated,
}
