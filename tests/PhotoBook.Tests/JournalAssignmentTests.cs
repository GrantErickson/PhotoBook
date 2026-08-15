using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;
using PhotoBook.Core.Templates;
using PhotoBook.Engine;
using PhotoBook.Imaging;
using PhotoBook.Ingestion.Journal;
using PhotoBook.Rendering;
using PhotoBook.Tests.Fixtures;
using SkiaSharp;

namespace PhotoBook.Tests;

/// <summary>
/// Manual date assignment (doc 11 §"Import Report UI" and §"Re-import semantics"): the promise that
/// journal text the matcher could not date is <b>surfaced rather than dropped</b>, that dating it by
/// hand puts it in the right month, and that the assignment is permanent work which survives the next
/// re-import of the same Word document.
/// <para>
/// The rules under test live in <see cref="JournalEdits"/> rather than in the WPF view model, so the
/// Import Report UI and these tests exercise one implementation of "the document owns the text; the
/// user owns the dates" instead of two.
/// </para>
/// </summary>
public sealed class JournalAssignmentTests
{
    private const int BookYear = 2024;

    private static readonly DateTime ImportedAt = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    // ---- an undated entry is surfaced, never silently dropped ---------------------------------------

    [Fact]
    public async Task AnEntryTheMatcherCannotDateIsKeptWholeAndReportedAsUnmatched()
    {
        using var workspace = new TempWorkspace("assign-unmatched");
        var journal = await ParseAsync(workspace.At("journal.docx"), MixedJournal());

        var undated = journal.Entries.Single(e => e.Status == JournalEntryStatus.Unmatched);

        // The text is all there — this is the whole point: nothing the user wrote is lost because a
        // date could not be read.
        Assert.Equal("Some Thoughts About This Winter", undated.HeadingText);
        Assert.Equal(2, undated.Paragraphs.Count);
        Assert.StartsWith("I keep meaning", undated.Paragraphs[0], StringComparison.Ordinal);
        Assert.True(undated.CharacterCount > 0);

        // …and it is reported rather than guessed at.
        Assert.Equal(1, journal.ImportReport.Unmatched);
        Assert.True(journal.ImportReport.HasFindings);
        Assert.Null(undated.MatchedBy);
        Assert.Null(undated.UserDate);

        // It is in the document but in no month of the book, which is exactly why the report has to
        // exist: without it, this text is invisible everywhere.
        Assert.Contains(undated, journal.Entries);
        for (var month = 1; month <= 12; month++)
        {
            Assert.DoesNotContain(undated, journal.EntriesIn(BookYear, month));
        }

        Assert.Equal(JournalReviewBucket.Undated, JournalEdits.Bucket(undated, BookYear));
    }

    // ---- assigning a date ---------------------------------------------------------------------------

    [Fact]
    public async Task AssigningADatePutsTheEntryInThatMonthAndNoOther()
    {
        using var workspace = new TempWorkspace("assign-month");
        var journal = await ParseAsync(workspace.At("journal.docx"), MixedJournal());
        var undated = journal.Entries.Single(e => e.Status == JournalEntryStatus.Unmatched);

        JournalEdits.AssignDate(journal, undated, new DateOnly(BookYear, 2, 14), BookYear);

        Assert.Equal(JournalEntryStatus.UserAssigned, undated.Status);
        Assert.Equal(new DateOnly(BookYear, 2, 14), undated.UserDate);
        Assert.Equal(new DateOnly(BookYear, 2, 14), undated.EffectiveDate);
        Assert.Equal(1.00, undated.Confidence);

        Assert.Contains(undated, journal.EntriesIn(BookYear, 2));
        Assert.DoesNotContain(undated, journal.EntriesIn(BookYear, 1));
        Assert.Contains(undated, journal.EntriesOn(new DateOnly(BookYear, 2, 14)));

        // The date the document gave is left alone: it is a machine field, refreshed on every import.
        Assert.Null(undated.MatchedBy);
        Assert.Equal(default, undated.DateStart);

        // The report now says what is actually true, rather than what the import said an hour ago.
        Assert.Equal(0, journal.ImportReport.Unmatched);
        Assert.Equal(6, journal.ImportReport.Matched);
    }

    [Fact]
    public async Task AnAssignedEntryIsSortedIntoPlaceLikeAnyOther()
    {
        using var workspace = new TempWorkspace("assign-sort");
        var journal = await ParseAsync(workspace.At("journal.docx"), MixedJournal());
        var undated = journal.Entries.Single(e => e.Status == JournalEntryStatus.Unmatched);

        JournalEdits.AssignDate(journal, undated, new DateOnly(BookYear, 1, 15), BookYear);

        Assert.Equal(
            journal.Entries.OrderBy(e => e.EffectiveDate).Select(e => e.Id),
            journal.Entries.Select(e => e.Id));
    }

    [Fact]
    public async Task DiscardingAnEntryTakesItOutOfTheBookWithoutLosingItsText()
    {
        using var workspace = new TempWorkspace("assign-discard");
        var journal = await ParseAsync(workspace.At("journal.docx"), MixedJournal());
        var entry = journal.Entries.Single(e => e.DateStart == new DateOnly(BookYear, 1, 20));
        var text = entry.Paragraphs.ToList();

        JournalEdits.SetExcluded(journal, entry, excluded: true, BookYear);

        Assert.DoesNotContain(entry, journal.EntriesIn(BookYear, 1));
        Assert.Equal(text, entry.Paragraphs);
        Assert.Equal(JournalReviewBucket.Discarded, JournalEdits.Bucket(entry, BookYear));

        JournalEdits.SetExcluded(journal, entry, excluded: false, BookYear);
        Assert.Contains(entry, journal.EntriesIn(BookYear, 1));
    }

    [Fact]
    public async Task UndoRestoresTheExactStateAnAssignmentReplaced()
    {
        using var workspace = new TempWorkspace("assign-undo");
        var journal = await ParseAsync(workspace.At("journal.docx"), MixedJournal());
        var entry = journal.Entries.First(e => e.Status == JournalEntryStatus.Matched);
        var before = JournalEdits.Capture(entry);

        JournalEdits.AssignDate(journal, entry, new DateOnly(BookYear, 6, 1), BookYear);
        JournalEdits.Restore(journal, entry, before, BookYear);

        Assert.Equal(before, JournalEdits.Capture(entry));
        Assert.Equal(before.UserDate, entry.UserDate);
        Assert.Equal(before.Status, entry.Status);
        Assert.Equal(before.Confidence, entry.Confidence);
    }

    // ---- re-import ----------------------------------------------------------------------------------

    [Fact]
    public async Task AnAssignmentSurvivesAReImportOfTheSameDocument()
    {
        using var workspace = new TempWorkspace("assign-reimport");
        var path = workspace.At("journal.docx");

        var first = await ParseAsync(path, MixedJournal());
        var undated = first.Entries.Single(e => e.Status == JournalEntryStatus.Unmatched);
        JournalEdits.AssignDate(first, undated, new DateOnly(BookYear, 2, 14), BookYear);

        var second = await ReParseAsync(path, first, MixedJournal());

        var carried = second.Entries.Single(e => string.Equals(e.Id, undated.Id, StringComparison.Ordinal));
        Assert.Equal(new DateOnly(BookYear, 2, 14), carried.UserDate);
        Assert.Equal(JournalEntryStatus.UserAssigned, carried.Status);
        Assert.Equal(1.00, carried.Confidence);
        Assert.Contains(carried, second.EntriesIn(BookYear, 2));
        Assert.Empty(second.ImportReport.OrphanedUserAssignments);

        // The document still owns the text: the paragraphs came back from the .docx, not from the
        // stored copy.
        Assert.Equal(undated.Paragraphs, carried.Paragraphs);
    }

    [Fact]
    public async Task AnAssignmentSurvivesEvenWhenTheEntryHasNoHeadingToMatchOn()
    {
        // The preamble bucket — text before the first date match — has no heading at all, so the
        // re-import fallback of doc 11 step 2 ("then by unique headingText") cannot help it. It
        // survives on the content-derived id alone, which is the case worth pinning down.
        using var workspace = new TempWorkspace("assign-preamble");
        var path = workspace.At("journal.docx");

        var first = await ParseAsync(path, PreambleJournal());
        var preamble = first.Entries.Single(e => e.HeadingText is null && e.Status == JournalEntryStatus.Unmatched);
        Assert.StartsWith("For the boys, one day", preamble.Paragraphs[0], StringComparison.Ordinal);

        JournalEdits.AssignDate(first, preamble, new DateOnly(BookYear, 3, 2), BookYear);

        var second = await ReParseAsync(path, first, PreambleJournal());

        var carried = second.Entries.Single(e => string.Equals(e.Id, preamble.Id, StringComparison.Ordinal));
        Assert.Equal(new DateOnly(BookYear, 3, 2), carried.UserDate);
        Assert.Equal(JournalEntryStatus.UserAssigned, carried.Status);
        Assert.Contains(carried, second.EntriesIn(BookYear, 3));
    }

    [Fact]
    public async Task EditingTheDocumentAroundAnAssignedEntryKeepsTheAssignment()
    {
        // The realistic re-import: the user adds a day in Word and re-imports. Identity is content
        // derived, so an insertion elsewhere must not disturb the entry that carries user work.
        using var workspace = new TempWorkspace("assign-edited");
        var path = workspace.At("journal.docx");

        var first = await ParseAsync(path, MixedJournal());
        var undated = first.Entries.Single(e => e.Status == JournalEntryStatus.Unmatched);
        JournalEdits.AssignDate(first, undated, new DateOnly(BookYear, 2, 14), BookYear);

        var edited = MixedJournal().ToList();
        edited.Insert(0, SyntheticJournal.Para.Heading($"Tuesday, January 2, {BookYear}"));
        edited.Insert(1, new SyntheticJournal.Para("Back to school, and nobody wanted to go."));

        var second = await ReParseAsync(path, first, edited);

        var carried = second.Entries.Single(e => string.Equals(e.Id, undated.Id, StringComparison.Ordinal));
        Assert.Equal(new DateOnly(BookYear, 2, 14), carried.UserDate);
        Assert.Equal(JournalEntryStatus.UserAssigned, carried.Status);
        Assert.Empty(second.ImportReport.OrphanedUserAssignments);
        Assert.Contains(second.Entries, e => e.DateStart == new DateOnly(BookYear, 1, 2));
    }

    [Fact]
    public async Task AnAssignmentWhoseEntryLeftTheDocumentBecomesAnOrphanRatherThanDisappearing()
    {
        using var workspace = new TempWorkspace("assign-orphan");
        var path = workspace.At("journal.docx");

        var first = await ParseAsync(path, MixedJournal());
        var undated = first.Entries.Single(e => e.Status == JournalEntryStatus.Unmatched);
        JournalEdits.AssignDate(first, undated, new DateOnly(BookYear, 2, 14), BookYear);

        // The user deletes that passage from the Word document and re-imports.
        var trimmed = MixedJournal()
            .Where(p => !p.Text.StartsWith("Some Thoughts", StringComparison.Ordinal) &&
                        !p.Text.StartsWith("I keep meaning", StringComparison.Ordinal) &&
                        !p.Text.StartsWith("Most of them are gone", StringComparison.Ordinal))
            .ToList();

        var second = await ReParseAsync(path, first, trimmed);

        var orphan = Assert.Single(second.ImportReport.OrphanedUserAssignments);
        Assert.Equal(undated.Id, orphan.EntryId);
        Assert.Equal(new DateOnly(BookYear, 2, 14), orphan.UserDate);
        Assert.Contains("Some Thoughts", orphan.TextPreview, StringComparison.Ordinal);

        // …and it can be moved onto an entry of the new document, or let go.
        var target = second.Entries.First(e => e.Status != JournalEntryStatus.Unmatched);
        Assert.True(JournalEdits.ReattachOrphan(second, orphan, target, BookYear));
        Assert.Empty(second.ImportReport.OrphanedUserAssignments);
        Assert.Equal(new DateOnly(BookYear, 2, 14), target.UserDate);
        Assert.Equal(JournalEntryStatus.UserAssigned, target.Status);
    }

    // ---- out of year --------------------------------------------------------------------------------

    [Fact]
    public async Task AnEntryDatedOutsideTheBookYearIsReportedAndKept()
    {
        using var workspace = new TempWorkspace("assign-outofyear");
        var journal = await ParseAsync(workspace.At("journal.docx"),
        [
            SyntheticJournal.Para.Heading("Sunday, December 31, 2023"),
            new SyntheticJournal.Para("New Year's Eve at home, which was exactly right."),
            SyntheticJournal.Para.Heading($"Monday, January 1, {BookYear}"),
            new SyntheticJournal.Para("A slow start to the year."),
        ]);

        var stray = journal.Entries.Single(e => e.DateStart.Year == 2023);

        Assert.Equal(1, journal.ImportReport.OutOfYear);
        Assert.Equal(JournalReviewBucket.OutOfYear, JournalEdits.Bucket(stray, BookYear));

        // Kept, with its text — it is simply in no chapter of this book (R3).
        Assert.Equal("New Year's Eve at home, which was exactly right.", stray.Paragraphs[0]);
        for (var month = 1; month <= 12; month++)
        {
            Assert.DoesNotContain(stray, journal.EntriesIn(BookYear, month));
        }

        // Re-dating it into the book year is the fix, and the report follows.
        JournalEdits.AssignDate(journal, stray, new DateOnly(BookYear, 1, 1), BookYear);
        Assert.Equal(0, journal.ImportReport.OutOfYear);
        Assert.Contains(stray, journal.EntriesIn(BookYear, 1));
    }

    [Fact]
    public async Task TheRecountedReportMatchesWhatAFreshImportWouldSay()
    {
        using var workspace = new TempWorkspace("assign-recount");
        var path = workspace.At("journal.docx");

        var first = await ParseAsync(path, MixedJournal());
        var undated = first.Entries.Single(e => e.Status == JournalEntryStatus.Unmatched);
        JournalEdits.AssignDate(first, undated, new DateOnly(BookYear, 2, 14), BookYear);

        var second = await ReParseAsync(path, first, MixedJournal());

        Assert.Equal(first.ImportReport.Matched, second.ImportReport.Matched);
        Assert.Equal(first.ImportReport.Ambiguous, second.ImportReport.Ambiguous);
        Assert.Equal(first.ImportReport.Unmatched, second.ImportReport.Unmatched);
        Assert.Equal(first.ImportReport.OutOfYear, second.ImportReport.OutOfYear);
    }

    // ---- fixtures -----------------------------------------------------------------------------------

    /// <summary>The canonical mixed-format journal, plus a passage with no date anywhere in it.</summary>
    private static List<SyntheticJournal.Para> MixedJournal() =>
    [
        SyntheticJournal.Para.Heading($"Friday, January 5, {BookYear}"),
        new SyntheticJournal.Para("We drove up to the cabin after work and got the fire going before dark."),
        new SyntheticJournal.Para("January 12 — Sledding on the hill behind the school until our gloves froze."),
        new SyntheticJournal.Para("1/20 — Snow day. Nobody left the house and nobody complained."),
        new SyntheticJournal.Para("Monday the 22nd was the first day the sun came back out."),
        new SyntheticJournal.Para("January 25–27 we had my parents staying with us for the long weekend."),

        // No date anywhere: this is the entry the user has to date by hand.
        SyntheticJournal.Para.Heading("Some Thoughts About This Winter"),
        new SyntheticJournal.Para("I keep meaning to write these down closer to when they happen."),
        new SyntheticJournal.Para("Most of them are gone by the time I sit down."),
    ];

    /// <summary>A journal that opens with undated prose — doc 11's preamble bucket, which has no heading.</summary>
    private static List<SyntheticJournal.Para> PreambleJournal() =>
    [
        new SyntheticJournal.Para("For the boys, one day, so they know what this year was actually like."),
        SyntheticJournal.Para.Heading($"Friday, January 5, {BookYear}"),
        new SyntheticJournal.Para("We drove up to the cabin after work and got the fire going before dark."),
    ];

    private static async Task<JournalDocument> ParseAsync(string path, IEnumerable<SyntheticJournal.Para> paragraphs)
    {
        SyntheticJournal.Write(path, paragraphs);
        await using var stream = File.OpenRead(path);
        return await new JournalParser().ParseAsync(
            stream, new JournalParseRequest(Path.GetFileName(path), BookYear, null, ImportedAt));
    }

    /// <summary>Re-imports over an existing journal, optionally after the document itself was edited.</summary>
    private static async Task<JournalDocument> ReParseAsync(
        string path, JournalDocument previous, IEnumerable<SyntheticJournal.Para> paragraphs)
    {
        SyntheticJournal.Write(path, paragraphs);
        await using var stream = File.OpenRead(path);
        return await new JournalParser().ParseAsync(
            stream, new JournalParseRequest(Path.GetFileName(path), BookYear, previous, ImportedAt));
    }
}

/// <summary>
/// The end of the manual-assignment path: an entry the matcher could not date, dated by hand, has to
/// reach a real page and put real ink on it. The engine takes journal entries by effective date, so an
/// assignment that stopped at the model would be indistinguishable from one that worked until the
/// book was printed.
/// </summary>
[Collection(SyntheticMonthCollection.Name)]
public sealed class JournalAssignmentLayoutTests
{
    private readonly SyntheticMonth _month;

    /// <summary>Receives the shared month.</summary>
    public JournalAssignmentLayoutTests(SyntheticMonth month) => _month = month;

    [Fact]
    public async Task AManuallyDatedEntryReachesAPageAndDrawsOnIt()
    {
        // A journal for the fixture month with one passage the matcher cannot date at all.
        var journal = await BuildJournalAsync();
        var undated = journal.Entries.Single(e => e.Status == JournalEntryStatus.Unmatched);
        Assert.DoesNotContain(undated, journal.EntriesIn(SyntheticMonth.Year, SyntheticMonth.Month));

        // The user dates it to a day of the month that has photographs.
        var day = SyntheticMonth.SparseDays[0];
        JournalEdits.AssignDate(journal, undated, day, SyntheticMonth.Year);

        // 1. It now reaches the engine, which binds it to a journal slot on a real page.
        var book = LayoutEngine.LayoutChapter(new LayoutRequest
        {
            Chapter = new ChapterInput
            {
                Year = SyntheticMonth.Year,
                Month = SyntheticMonth.Month,
                Photos = [.. _month.Catalog.Photos],
                JournalEntries = [.. journal.EntriesIn(SyntheticMonth.Year, SyntheticMonth.Month)],
            },
            Style = StyleResolver.Resolve(_month.Book, null, null),
            Seed = SyntheticMonth.Seed,
        });

        var carrier = book.Pages.FirstOrDefault(p => p.JournalAssignments
            .Any(a => a.EntryIds.Contains(undated.Id, StringComparer.Ordinal)));
        Assert.True(carrier is not null, "the manually dated entry reached no page of the laid-out chapter");

        var slotId = carrier!.JournalAssignments
            .First(a => a.EntryIds.Contains(undated.Id, StringComparer.Ordinal))
            .TextSlotId;

        // 2. …and the renderer actually puts the words on the page. The same page is rendered with the
        //    entry excluded and the two buffers compared inside the journal slot: pixels that change
        //    are pixels this entry is responsible for. Counting "light" pixels would not do — a
        //    journal slot may sit over a photograph, where drawing text *darkens* the pixels under a
        //    scrim.
        using var images = new ThumbnailRenderImageSource(_month.Paths, _month.Catalog, _month.Thumbnails);

        var withText = PagePreviewRenderer.Render(
            Request(journal, carrier, images), 1100, 850, SKColors.Black);
        var slot = withText.Result.TextSlotRects[slotId];

        undated.Excluded = true;
        var withoutText = PagePreviewRenderer.Render(
            Request(journal, carrier, images), 1100, 850, SKColors.Black);
        undated.Excluded = false;

        var changed = DifferingPixels(withText, withoutText, slot);
        Assert.True(changed > 200,
            $"only {changed} pixels of the journal slot changed when the entry was added — the text did not draw");
    }

    private async Task<JournalDocument> BuildJournalAsync()
    {
        var path = _month.Workspace.At("assign", "journal.docx");
        SyntheticJournal.Write(path,
        [
            SyntheticJournal.Para.Heading(
                new DateOnly(SyntheticMonth.Year, SyntheticMonth.Month, 21)
                    .ToDateTime(TimeOnly.MinValue)
                    .ToString("dddd, MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture)),
            new SyntheticJournal.Para(SyntheticMonth.Filler(200)),

            // The passage with no date in it anywhere.
            SyntheticJournal.Para.Heading("A Few Things Worth Remembering"),
            new SyntheticJournal.Para(SyntheticMonth.Filler(240)),
        ]);

        await using var stream = File.OpenRead(path);
        return await new JournalParser().ParseAsync(
            stream,
            new JournalParseRequest(
                "journal.docx",
                SyntheticMonth.Year,
                null,
                new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc)));
    }

    private PageRenderRequest Request(JournalDocument journal, Page page, IRenderImageSource images) => new()
    {
        Book = _month.Book,
        Chapter = new Chapter { Year = SyntheticMonth.Year, Month = SyntheticMonth.Month },
        Page = page,
        Template = page.ResolveTemplate(id => TemplateLibrary.Default.Find(id))
                   ?? throw new InvalidOperationException($"unresolved template '{page.TemplateRef}'"),
        Geometry = PageGeometryMapper.Create(
            BuiltInPrintProfiles.Generic, _month.Book.PageSize, PageGeometry.PointsPerInch),
        Images = images,
        Photos = _month.Catalog,
        Journal = journal,
        Target = RenderTarget.Screen,
        ShowEmptySlotFlags = false,
    };

    /// <summary>Pixels inside a device rect that differ materially between two renders of one page.</summary>
    private static int DifferingPixels(PagePreview a, PagePreview b, SKRect rect)
    {
        var left = a.Image;
        var right = b.Image;
        var one = left.AsSpan();
        var two = right.AsSpan();
        var changed = 0;

        for (var y = (int)rect.Top; y < (int)rect.Bottom; y++)
        {
            if (y < 0 || y >= left.Height || y >= right.Height) continue;
            for (var x = (int)rect.Left; x < (int)rect.Right; x++)
            {
                if (x < 0 || x >= left.Width || x >= right.Width) continue;

                var i = ((y * left.Width) + x) * DecodedImage.BytesPerPixel;
                var delta = Math.Abs(one[i] - two[i]) +
                            Math.Abs(one[i + 1] - two[i + 1]) +
                            Math.Abs(one[i + 2] - two[i + 2]);
                if (delta > 24) changed++;
            }
        }

        return changed;
    }
}
