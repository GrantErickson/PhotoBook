using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;
using PhotoBook.Ingestion.Journal;
using PhotoBook.Tests.Fixtures;

namespace PhotoBook.Tests;

/// <summary>
/// Journal ingestion end to end (R2, doc 11, ADR-0008) against a real synthesized <c>.docx</c>: every
/// date format the matcher list names resolves to the right day, and the heading with no parseable
/// date is surfaced in the Import Report rather than swallowed.
/// </summary>
public sealed class JournalImportTests
{
    private const int BookYear = 2024;

    private static readonly DateTime ImportedAt = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task EverySynthesizedDateFormatParsesToTheRightDate()
    {
        using var workspace = new TempWorkspace("journal");
        var document = await ParseAsync(workspace);

        foreach (var (matcher, start, end) in SyntheticJournal.ExpectedMixedFormats(BookYear))
        {
            var entry = document.Entries.SingleOrDefault(e => e.DateStart == start);
            Assert.True(entry is not null, $"no entry resolved to {start:yyyy-MM-dd} (matcher {matcher})");
            Assert.Equal(end, entry!.DateEnd);
            Assert.Equal(matcher, entry.MatchedBy);
            Assert.Equal(JournalEntryStatus.Matched, entry.Status);
            Assert.True(entry.Confidence >= JournalDateMatchers.MatchedThreshold,
                $"{matcher} came back at {entry.Confidence:F2}, below the matched threshold");
        }
    }

    [Fact]
    public async Task TheRangeEntrySpansItsWholeSpan()
    {
        using var workspace = new TempWorkspace("journal-range");
        var document = await ParseAsync(workspace);

        var range = document.Entries.Single(e => e.DateStart == new DateOnly(BookYear, 1, 25));

        Assert.True(range.IsRange);
        Assert.Equal(new DateOnly(BookYear, 1, 27), range.DateEnd);
    }

    [Fact]
    public async Task TheUndatedHeadingLandsInTheImportReportAsUnmatched()
    {
        using var workspace = new TempWorkspace("journal-unmatched");
        var document = await ParseAsync(workspace);

        var unmatched = document.Entries.Where(e => e.Status == JournalEntryStatus.Unmatched).ToList();

        Assert.Single(unmatched);
        Assert.Equal(SyntheticJournal.UndatedHeading, unmatched[0].HeadingText);
        Assert.Equal(1, document.ImportReport.Unmatched);
        Assert.True(document.ImportReport.HasFindings);

        // An undated entry never appears in the book until the user dates it.
        Assert.DoesNotContain(unmatched[0], document.EntriesIn(BookYear, 1));

        // …and its date is never guessed.
        Assert.Null(unmatched[0].UserDate);
        Assert.Null(unmatched[0].MatchedBy);
    }

    [Fact]
    public async Task TheReportCountsAddUpToTheEntriesParsed()
    {
        using var workspace = new TempWorkspace("journal-report");
        var document = await ParseAsync(workspace);

        var report = document.ImportReport;

        Assert.Equal(5, report.Matched);
        Assert.Equal(0, report.Ambiguous);
        Assert.Equal(1, report.Unmatched);
        Assert.Equal(0, report.OutOfYear);
        Assert.Equal(document.Entries.Count, report.Matched + report.Ambiguous + report.Unmatched);
        Assert.Empty(report.OrphanedUserAssignments);
    }

    [Fact]
    public async Task TheDocumentTextIsCarriedOntoTheEntriesItBelongsTo()
    {
        using var workspace = new TempWorkspace("journal-text");
        var document = await ParseAsync(workspace);

        var january5 = document.Entries.Single(e => e.DateStart == new DateOnly(BookYear, 1, 5));

        Assert.Equal(2, january5.Paragraphs.Count);
        Assert.StartsWith("We drove up to the cabin", january5.Paragraphs[0], StringComparison.Ordinal);
        Assert.Equal("The kids stayed up far too late.", january5.Paragraphs[1]);
        Assert.Equal($"Friday, January 5, {BookYear}", january5.HeadingText);
        Assert.True(january5.CharacterCount > 0);
    }

    [Fact]
    public async Task EntriesAreStoredInDateOrderAndOneEntryPerDay()
    {
        using var workspace = new TempWorkspace("journal-order");
        var document = await ParseAsync(workspace);

        var dated = document.Entries.Where(e => e.Status != JournalEntryStatus.Unmatched).ToList();

        Assert.Equal(dated.OrderBy(e => e.EffectiveDate).Select(e => e.Id), dated.Select(e => e.Id));
        Assert.Equal(dated.Count, dated.Select(e => e.EffectiveDate).Distinct().Count());
    }

    [Fact]
    public async Task ImportingTheSameDocumentTwiceIsAStrictNoOp()
    {
        using var workspace = new TempWorkspace("journal-golden");
        var path = SyntheticJournal.WriteMixedFormats(workspace.At("journal.docx"), BookYear);

        var first = await ParseFileAsync(path);
        var second = await ParseFileAsync(path, previous: first);

        Assert.Equal(
            first.Entries.Select(e => (e.Id, e.DateStart, e.DateEnd, e.Status, e.MatchedBy, e.Confidence)),
            second.Entries.Select(e => (e.Id, e.DateStart, e.DateEnd, e.Status, e.MatchedBy, e.Confidence)));
        Assert.Equal(first.Source!.ContentHash, second.Source!.ContentHash);
        Assert.Equal(first.Source.ImportedAtUtc, second.Source.ImportedAtUtc);
    }

    [Fact]
    public async Task TheUserOwnsTheDatesAndTheDocumentOwnsTheText()
    {
        using var workspace = new TempWorkspace("journal-carry");
        var path = SyntheticJournal.WriteMixedFormats(workspace.At("journal.docx"), BookYear);

        var first = await ParseFileAsync(path);

        // The user dates the entry the parser could not, and excludes another.
        var undated = first.Entries.Single(e => e.Status == JournalEntryStatus.Unmatched);
        undated.UserDate = new DateOnly(BookYear, 2, 14);
        undated.Status = JournalEntryStatus.UserAssigned;
        var excludedId = first.Entries.Single(e => e.DateStart == new DateOnly(BookYear, 1, 20)).Id;
        first.Entries.Single(e => e.Id == excludedId).Excluded = true;

        var second = await ParseFileAsync(path, previous: first);

        var carried = second.Entries.Single(e => e.Id == undated.Id);
        Assert.Equal(new DateOnly(BookYear, 2, 14), carried.UserDate);
        Assert.Equal(new DateOnly(BookYear, 2, 14), carried.EffectiveDate);
        Assert.Equal(JournalEntryStatus.UserAssigned, carried.Status);
        Assert.True(second.Entries.Single(e => e.Id == excludedId).Excluded);
        Assert.Empty(second.ImportReport.OrphanedUserAssignments);
    }

    [Fact]
    public async Task ADateOutsideTheBookYearIsFlaggedRatherThanDropped()
    {
        using var workspace = new TempWorkspace("journal-outofyear");
        var path = SyntheticJournal.Write(workspace.At("out-of-year.docx"),
        [
            SyntheticJournal.Para.Heading("Sunday, December 31, 2023"),
            new SyntheticJournal.Para("New Year's Eve at home, which was exactly right."),
            SyntheticJournal.Para.Heading($"Monday, January 1, {BookYear}"),
            new SyntheticJournal.Para("A slow start to the year."),
        ]);

        var document = await ParseFileAsync(path);

        Assert.Equal(1, document.ImportReport.OutOfYear);
        Assert.Contains(document.Entries, e => e.DateStart == new DateOnly(2023, 12, 31));
    }

    [Fact]
    public async Task ANonWordFileIsRejectedWithAnActionableMessage()
    {
        using var workspace = new TempWorkspace("journal-bad");
        var path = workspace.At("not-a-journal.docx");
        await File.WriteAllTextAsync(path, "This is plain text pretending to be a Word document.");

        await using var stream = File.OpenRead(path);
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => new JournalParser().ParseAsync(stream, new JournalParseRequest("not-a-journal.docx", BookYear)));

        Assert.Contains(".docx", error.Message, StringComparison.Ordinal);
    }

    private static async Task<JournalDocument> ParseAsync(TempWorkspace workspace) =>
        await ParseFileAsync(SyntheticJournal.WriteMixedFormats(workspace.At("journal.docx"), BookYear));

    private static async Task<JournalDocument> ParseFileAsync(string path, JournalDocument? previous = null)
    {
        await using var stream = File.OpenRead(path);
        var request = new JournalParseRequest(Path.GetFileName(path), BookYear, previous, ImportedAt);
        return await new JournalParser().ParseAsync(stream, request);
    }
}
