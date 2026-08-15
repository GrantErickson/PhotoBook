using PhotoBook.Core.Model;

namespace PhotoBook.Core.Abstractions;

/// <summary>
/// Turns the optional Word journal into dated entries the layout engine interleaves with photos (R2,
/// doc 11, ADR-0008 — OpenXML, never Word interop).
/// <para>Contract rules:</para>
/// <list type="bullet">
/// <item><description><b>The document owns the text; the user owns the dates.</b> Paragraphs, heading
/// text and machine fields are always refreshed from the document; user dates, exclusions and
/// <see cref="JournalEntryStatus.UserAssigned"/> statuses are carried forward.</description></item>
/// <item><description>Identity is content-derived, not positional:
/// <c>id = "je-" + sha256(sourceKey + "#" + occurrence)[..12]</c>, so inserting a paragraph does not
/// re-key the rest of the journal (<see cref="Ids.JournalEntryId"/>).</description></item>
/// <item><description>Parsing is deterministic and free of wall-clock reads apart from
/// <see cref="JournalParseRequest.ImportedAtUtc"/>: importing the same document twice is a strict
/// no-op, which is a golden test.</description></item>
/// <item><description>A date the matchers cannot resolve is never guessed — the entry comes back
/// <see cref="JournalEntryStatus.Unmatched"/> or <see cref="JournalEntryStatus.Ambiguous"/> and appears
/// in the import report. Unresolved entries never block export; they simply do not appear in the book.</description></item>
/// <item><description>The parser never writes to disk; the caller persists the result through the
/// project store.</description></item>
/// <item><description>The layout engine never sees this interface — it receives the parsed
/// <see cref="JournalDocument"/> (kernel §12).</description></item>
/// </list>
/// </summary>
public interface IJournalParser
{
    /// <summary>Parses a Word journal into dated entries plus the import report.</summary>
    /// <param name="content">The <c>.docx</c> bytes, readable and seekable. The parser does not dispose it.</param>
    /// <param name="request">File name, book year, the previous journal and the import timestamp.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The journal document to store as <c>journal.json</c>.</returns>
    /// <exception cref="OperationCanceledException">The import was cancelled.</exception>
    Task<JournalDocument> ParseAsync(Stream content, JournalParseRequest request, CancellationToken ct = default);
}
