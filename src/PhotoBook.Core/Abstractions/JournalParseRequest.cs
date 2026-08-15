using PhotoBook.Core.Model;

namespace PhotoBook.Core.Abstractions;

/// <summary>
/// One journal import (doc 11). Parsing is a pure transformation from document bytes to a
/// <see cref="JournalDocument"/>; the only mutable input is the user's own date work, carried in
/// <see cref="Previous"/>.
/// </summary>
/// <param name="FileName">The <c>.docx</c> file name, recorded in <see cref="JournalSource"/>.</param>
/// <param name="BookYear">
/// The book's year (R3). Dates written without a year are resolved into it; a date that lands outside
/// it is flagged <c>outOfYear</c> in the import report.
/// </param>
/// <param name="Previous">
/// The journal currently stored in the project, or null on a first import. Re-import carries
/// <see cref="JournalEntry.UserDate"/>, <see cref="JournalEntry.Excluded"/> and the
/// <see cref="JournalEntryStatus.UserAssigned"/> status forward onto matching new entries, and reports
/// anything that matched nothing as an orphaned assignment.
/// </param>
/// <param name="ImportedAtUtc">
/// The timestamp stamped into <see cref="JournalSource.ImportedAtUtc"/>; pass an explicit value to keep
/// golden tests byte-stable.
/// </param>
public sealed record JournalParseRequest(
    string FileName,
    int BookYear,
    JournalDocument? Previous = null,
    DateTime? ImportedAtUtc = null);
