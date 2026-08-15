using System.Security.Cryptography;
using System.Text;
using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;

namespace PhotoBook.Ingestion.Journal;

/// <summary>
/// Turns the optional Word journal into dated <see cref="JournalEntry"/> records (R2, doc 11,
/// ADR-0008 — OpenXML, never Word interop). The pipeline is: paragraph stream → tolerant
/// multi-matcher → segmentation → same-day merge → identity → re-import carry-forward → Import Report.
/// <para>The two contracts that shape every decision here:</para>
/// <list type="bullet">
/// <item><description><b>The document owns the text; the user owns the dates.</b> Paragraphs, heading
/// text and machine fields are refreshed from the document on every import; user dates, exclusions and
/// <see cref="JournalEntryStatus.UserAssigned"/> statuses are carried forward onto matching
/// entries, and anything that matches nothing lands in the report as an orphaned assignment.</description></item>
/// <item><description><b>A day's text is atomic</b> (kernel §9): entries resolving to the same date
/// merge into one, so the model holds exactly one entry per day and the layout engine can treat it as
/// one continuous flow.</description></item>
/// </list>
/// <para>Parsing is deterministic and reads no clock apart from
/// <see cref="JournalParseRequest.ImportedAtUtc"/>: importing the same document twice produces
/// identical ids and identical content, which is a golden test (doc 13).</para>
/// </summary>
public sealed class JournalParser : IJournalParser
{
    /// <summary>How much of the first paragraph feeds the content-derived entry key (doc 11).</summary>
    private const int SourceKeyPrefixLength = 80;

    /// <inheritdoc/>
    public async Task<JournalDocument> ParseAsync(
        Stream content, JournalParseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var bytes = await ReadAllAsync(content, ct).ConfigureAwait(false);
        var contentHash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        using var buffer = new MemoryStream(bytes, writable: false);
        var paragraphs = DocxParagraphReader.Read(buffer);
        ct.ThrowIfCancellationRequested();

        var drafts = Segment(paragraphs, request.BookYear);
        drafts = MergeSameDay(drafts);
        AssignIdentity(drafts);

        var entries = drafts.Select(d => d.ToEntry()).ToList();
        var orphans = CarryForwardUserWork(entries, request.Previous);

        var ordered = entries
            .OrderBy(e => e.EffectiveDate)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();

        return new JournalDocument
        {
            SchemaVersion = 1,
            Source = new JournalSource
            {
                FileName = request.FileName,
                ContentHash = contentHash,
                ImportedAtUtc = request.ImportedAtUtc ?? DateTime.UtcNow,
            },
            Entries = ordered,
            ImportReport = BuildReport(ordered, request.BookYear, orphans),
        };
    }

    // ---------------------------------------------------------------- segmentation

    /// <summary>
    /// The segmentation loop of doc 11. Text before the first date match becomes a single preamble
    /// bucket (usually a title page or dedication) rather than being attached to the first dated entry.
    /// </summary>
    private static List<EntryDraft> Segment(IReadOnlyList<ParagraphRecord> paragraphs, int bookYear)
    {
        var drafts = new List<EntryDraft>();
        var context = new JournalDateContext(bookYear, null);
        var current = EntryDraft.Preamble(0);

        foreach (var paragraph in paragraphs)
        {
            var match = JournalDateMatchers.TryMatch(paragraph, context);

            if (match is not null && match.Confidence >= JournalDateMatchers.AmbiguousThreshold)
            {
                Close(drafts, current);

                var remainder = TrimAfterToken(paragraph.Text[match.TokenLength..]);
                var headingText = remainder.Length == 0 && paragraph.IsHeadingCandidate ? paragraph.Text : null;

                current = EntryDraft.Dated(drafts.Count + 1, match, headingText);
                if (remainder.Length > 0) current.Paragraphs.Add(remainder);

                // The chronology cursor only follows confident matches, so one bad guess cannot drag
                // the rest of the journal after it.
                if (match.Confidence >= JournalDateMatchers.MatchedThreshold)
                    context = context with { Cursor = match.DateStart };

                continue;
            }

            if (paragraph.IsStrongHeading)
            {
                // A heading that yields no date at any confidence is never silently swallowed: it starts
                // an unmatched entry the user can date in the Import Report (doc 11).
                Close(drafts, current);
                current = EntryDraft.UnmatchedHeading(drafts.Count + 1, paragraph.Text);
                continue;
            }

            current.Paragraphs.Add(paragraph.Text);
        }

        Close(drafts, current);
        return drafts;
    }

    private static void Close(List<EntryDraft> drafts, EntryDraft draft)
    {
        if (draft.IsEmpty) return;
        draft.Order = drafts.Count;
        drafts.Add(draft);
    }

    /// <summary>Strips the separator that follows an inline date token: "1/5 — We drove…".</summary>
    private static string TrimAfterToken(string text) =>
        text.TrimStart(' ', '\t', ' ', '—', '–', '-', ':', ';', ',', '.', ')', ']').Trim();

    /// <summary>
    /// Merges entries resolving to the same day. A day's journal text is atomic (R5, kernel §9) and the
    /// model stores exactly one entry per day, so two paragraphs dated "January 5" become one entry.
    /// </summary>
    private static List<EntryDraft> MergeSameDay(List<EntryDraft> drafts)
    {
        var merged = new List<EntryDraft>();
        var byDate = new Dictionary<DateOnly, EntryDraft>();

        foreach (var draft in drafts)
        {
            if (draft.Match is null)
            {
                merged.Add(draft);
                continue;
            }

            if (byDate.TryGetValue(draft.Match.DateStart, out var target))
            {
                target.Absorb(draft);
                continue;
            }

            byDate[draft.Match.DateStart] = draft;
            merged.Add(draft);
        }

        for (var i = 0; i < merged.Count; i++) merged[i].Order = i;
        return merged;
    }

    // ---------------------------------------------------------------- identity

    /// <summary>
    /// Content-derived identity (doc 11 Decision): <c>sourceKey = normalize(heading) + "|" +
    /// normalize(first 80 chars of the first paragraph)</c>, and <c>id = "je-" +
    /// sha256(sourceKey + "#" + occurrence)[..12]</c>. Positional ids would break the moment a
    /// paragraph is inserted; content keys survive reordering and unrelated edits.
    /// </summary>
    private static void AssignIdentity(List<EntryDraft> drafts)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var draft in drafts)
        {
            var firstParagraph = draft.Paragraphs.Count > 0 ? draft.Paragraphs[0] : string.Empty;
            var prefix = firstParagraph.Length <= SourceKeyPrefixLength
                ? firstParagraph
                : firstParagraph[..SourceKeyPrefixLength];

            var sourceKey = $"{Normalize(draft.HeadingText)}|{Normalize(prefix)}";
            var occurrence = occurrences.TryGetValue(sourceKey, out var seen) ? seen : 0;
            occurrences[sourceKey] = occurrence + 1;

            draft.SourceKey = sourceKey;
            draft.Occurrence = occurrence;
            draft.Id = Ids.JournalEntryId(sourceKey, occurrence);
        }
    }

    /// <summary>Lowercases, strips punctuation and collapses whitespace — the doc 11 <c>normalize</c>.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
                lastWasSpace = false;
            }
            else if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c))
            {
                if (!lastWasSpace && builder.Length > 0) builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    // ---------------------------------------------------------------- re-import

    /// <summary>
    /// Re-import carry-forward (doc 11): match old entries to new ones by exact id, then by unique
    /// heading text; carry the user's date, exclusion and <see cref="JournalEntryStatus.UserAssigned"/>
    /// status; report any user work that matched nothing so the UI can offer re-attach or discard.
    /// </summary>
    private static List<OrphanedUserAssignment> CarryForwardUserWork(List<JournalEntry> entries, JournalDocument? previous)
    {
        var orphans = new List<OrphanedUserAssignment>();
        if (previous is null || previous.Entries.Count == 0) return orphans;

        var byId = new Dictionary<string, JournalEntry>(StringComparer.Ordinal);
        foreach (var entry in entries) byId.TryAdd(entry.Id, entry);

        var byHeading = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.HeadingText))
            .GroupBy(e => Normalize(e.HeadingText), StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);

        var claimed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var old in previous.Entries)
        {
            var hasUserWork = old.UserDate is not null || old.Excluded || old.Status == JournalEntryStatus.UserAssigned;

            JournalEntry? target = null;
            if (byId.TryGetValue(old.Id, out var byIdMatch) && claimed.Add(byIdMatch.Id))
            {
                target = byIdMatch;
            }
            else if (!string.IsNullOrWhiteSpace(old.HeadingText) &&
                     byHeading.TryGetValue(Normalize(old.HeadingText), out var byHeadingMatch) &&
                     claimed.Add(byHeadingMatch.Id))
            {
                target = byHeadingMatch;
            }

            if (target is null)
            {
                if (hasUserWork)
                {
                    orphans.Add(new OrphanedUserAssignment
                    {
                        EntryId = old.Id,
                        TextPreview = Preview(old),
                        UserDate = old.UserDate,
                        Excluded = old.Excluded,
                    });
                }

                continue;
            }

            if (!hasUserWork) continue;

            target.UserDate = old.UserDate;
            target.Excluded = old.Excluded;
            if (old.Status == JournalEntryStatus.UserAssigned)
            {
                target.Status = JournalEntryStatus.UserAssigned;
                target.Confidence = 1.00;
            }
        }

        return orphans;
    }

    private static string Preview(JournalEntry entry)
    {
        var text = entry.HeadingText ?? entry.Paragraphs.FirstOrDefault() ?? string.Empty;
        return text.Length <= 120 ? text : text[..120] + "…";
    }

    private static JournalImportReport BuildReport(
        IReadOnlyList<JournalEntry> entries, int bookYear, List<OrphanedUserAssignment> orphans)
    {
        var report = new JournalImportReport
        {
            Matched = entries.Count(e => e.Status is JournalEntryStatus.Matched or JournalEntryStatus.UserAssigned),
            Ambiguous = entries.Count(e => e.Status == JournalEntryStatus.Ambiguous),
            Unmatched = entries.Count(e => e.Status == JournalEntryStatus.Unmatched),
            OutOfYear = entries.Count(e =>
                e.Status != JournalEntryStatus.Unmatched && e.EffectiveDate.Year != bookYear),
        };

        foreach (var orphan in orphans) report.OrphanedUserAssignments.Add(orphan);
        return report;
    }

    private static async Task<byte[]> ReadAllAsync(Stream content, CancellationToken ct)
    {
        if (content is MemoryStream ready && ready.TryGetBuffer(out _)) return ready.ToArray();

        if (content.CanSeek) content.Position = 0;
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct).ConfigureAwait(false);
        if (content.CanSeek) content.Position = 0;
        return buffer.ToArray();
    }

    /// <summary>One entry under construction while the paragraph stream is being segmented.</summary>
    private sealed class EntryDraft
    {
        private EntryDraft(int order, JournalDateMatch? match, string? headingText)
        {
            Order = order;
            Match = match;
            HeadingText = headingText;
        }

        public int Order { get; set; }

        public JournalDateMatch? Match { get; private set; }

        public string? HeadingText { get; }

        public List<string> Paragraphs { get; } = [];

        public string SourceKey { get; set; } = string.Empty;

        public int Occurrence { get; set; }

        public string Id { get; set; } = string.Empty;

        public bool IsEmpty => Match is null && HeadingText is null && Paragraphs.Count == 0;

        public static EntryDraft Preamble(int order) => new(order, null, null);

        public static EntryDraft UnmatchedHeading(int order, string headingText) => new(order, null, headingText);

        public static EntryDraft Dated(int order, JournalDateMatch match, string? headingText) =>
            new(order, match, headingText);

        /// <summary>Folds another entry for the same day into this one, keeping document order.</summary>
        public void Absorb(EntryDraft other)
        {
            if (other.HeadingText is { Length: > 0 } heading && Paragraphs.Count > 0)
            {
                // The absorbed entry's own heading survives as text so nothing the user wrote is lost.
                Paragraphs.Add(heading);
            }

            Paragraphs.AddRange(other.Paragraphs);

            if (other.Match is null || Match is null) return;

            var alternates = Match.Alternates.Concat(other.Match.Alternates).Distinct().ToList();
            Match = Match with
            {
                DateEnd = other.Match.DateEnd > Match.DateEnd ? other.Match.DateEnd : Match.DateEnd,
                Confidence = Math.Max(Match.Confidence, other.Match.Confidence),
                Alternates = alternates,
                OutOfYear = Match.OutOfYear || other.Match.OutOfYear,
            };
        }

        public JournalEntry ToEntry()
        {
            var entry = new JournalEntry
            {
                Id = Id,
                SourceKey = SourceKey,
                Occurrence = Occurrence,
                HeadingText = HeadingText,
                Status = JournalEntryStatus.Unmatched,
            };

            foreach (var paragraph in Paragraphs) entry.Paragraphs.Add(paragraph);

            if (Match is null) return entry;

            entry.DateStart = Match.DateStart;
            entry.DateEnd = Match.DateEnd;
            entry.Status = JournalDateMatchers.StatusFor(Match.Confidence);
            entry.MatchedBy = Match.MatcherName;
            entry.Confidence = Math.Round(Match.Confidence, 4);
            foreach (var alternate in Match.Alternates) entry.Alternates.Add(alternate);

            return entry;
        }
    }
}
