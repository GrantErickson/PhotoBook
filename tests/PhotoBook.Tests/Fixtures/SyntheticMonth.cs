using System.Globalization;
using PhotoBook.Analysis.Caching;
using PhotoBook.Analysis.Classical;
using PhotoBook.Analysis.Running;
using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;
using PhotoBook.Core.Persistence;
using PhotoBook.Imaging;
using PhotoBook.Ingestion;
using PhotoBook.Ingestion.Journal;

namespace PhotoBook.Tests.Fixtures;

/// <summary>
/// One realistic month of a real family book, built out of the Phase 2 fixtures and walked through
/// the real Phase 2 chain — synthesized image files, <see cref="PhotoImporter"/>, the thumbnail
/// cache, <see cref="ClassicalAnalyzer"/> and <see cref="TierAssigner"/>, plus a real <c>.docx</c>
/// journal parsed by <see cref="JournalParser"/>. Nothing about the photos is hand-asserted state:
/// tiers and Focus Regions are whatever the shipped analyzer actually measured.
///
/// <para>The shape of the month is deliberate, because it is what doc 08 has to survive:</para>
/// <list type="bullet">
/// <item><description>three consecutive <b>sparse days</b> (3, 2 and 2 photos) with short entries —
/// R28's "these can be combined onto a single page";</description></item>
/// <item><description>one <b>heavy day</b> of 32 photos with a 1900-character entry — doc 08 §4's
/// birthday, which must split across several pages;</description></item>
/// <item><description>one <b>journal-only day</b> with no photos at all;</description></item>
/// <item><description>ordinary days of 4–6 photos, two <b>excluded</b> photos (R17) and one
/// panorama, so aspect handling is exercised;</description></item>
/// <item><description>a photo whose high-contrast subject is planted far off center, for the R25
/// smart-crop assertion.</description></item>
/// </list>
///
/// <para>
/// Building this costs real decode and analysis work, so it is a shared fixture: the whole Phase 3
/// suite pays for it once.
/// </para>
/// </summary>
public sealed class SyntheticMonth : IAsyncLifetime, IDisposable
{
    /// <summary>The book's year.</summary>
    public const int Year = 2024;

    /// <summary>The month under test.</summary>
    public const int Month = 4;

    /// <summary>The book seed — fixed, so every layout in the suite is reproducible.</summary>
    public const ulong Seed = 0x9E3779B97F4A7C15;

    /// <summary>The three consecutive sparse days that R28 wants merged.</summary>
    public static readonly DateOnly[] SparseDays =
    [
        new(Year, Month, 3), new(Year, Month, 4), new(Year, Month, 5),
    ];

    /// <summary>The 32-photo birthday.</summary>
    public static readonly DateOnly HeavyDay = new(Year, Month, 14);

    /// <summary>The day that has journal text and no photographs at all.</summary>
    public static readonly DateOnly JournalOnlyDay = new(Year, Month, 18);

    /// <summary>The planted subject of <see cref="SubjectPhotoFileName"/>, in normalized image coordinates.</summary>
    public static readonly Rect PlantedSubject = new(0.62, 0.60, 0.26, 0.30);

    /// <summary>The file whose subject sits far off center — the R25 fixture.</summary>
    public const string SubjectPhotoFileName = "IMG_9000.jpg";

    private readonly TempWorkspace _workspace = new("phase3-month");
    private ThumbnailCache? _thumbnails;

    /// <summary>The project folder layout.</summary>
    public ProjectPaths Paths { get; private set; } = null!;

    /// <summary>The imported, analyzed catalog.</summary>
    public PhotoCatalog Catalog { get; private set; } = null!;

    /// <summary>The parsed journal.</summary>
    public JournalDocument Journal { get; private set; } = null!;

    /// <summary>The book document these pages belong to.</summary>
    public Book Book { get; private set; } = null!;

    /// <summary>The thumbnail cache, shared so the render tests do not rebuild derived pixels.</summary>
    public ThumbnailCache Thumbnails => _thumbnails!;

    /// <summary>Scratch space outside the project folder, for exported PDFs.</summary>
    public TempWorkspace Workspace => _workspace;

    /// <summary>The photo whose subject is planted at <see cref="PlantedSubject"/>.</summary>
    public Photo SubjectPhoto =>
        Catalog.Photos.Single(p => string.Equals(p.OriginalFileName, SubjectPhotoFileName, StringComparison.Ordinal));

    /// <summary>The photos the user excluded from the book (R17).</summary>
    public IReadOnlyList<Photo> ExcludedPhotos =>
        [.. Catalog.Photos.Where(p => p.Excluded).OrderBy(p => p.Id, StringComparer.Ordinal)];

    /// <summary>Every non-excluded photo of the month, chronologically.</summary>
    public IReadOnlyList<Photo> MonthPhotos => [.. Catalog.InChapter(Year, Month)];

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        Paths = new ProjectPaths(_workspace.Folder("project"));
        Paths.EnsureFolders();
        var source = _workspace.Folder("source");

        WriteMonth(source);

        Catalog = new PhotoCatalog();
        var report = await new PhotoImporter(Paths).ImportAsync(
            new FolderPhotoSource(source),
            Catalog,
            new PhotoImportOptions
            {
                MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2),
                ImportedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });

        if (report.AddedCount != Catalog.Photos.Count)
            throw new InvalidOperationException($"import added {report.AddedCount} of {Catalog.Photos.Count} photos.");

        _thumbnails = new ThumbnailCache(Paths, ThumbnailSources.FromCatalog(Paths, () => Catalog));

        var runner = new AnalysisRunner(
            new ClassicalAnalyzer(),
            new ThumbnailCacheAnalysisCopyLocator(_thumbnails),
            new FileAnalysisCache(Paths));

        var run = await runner.RunAsync(
            Catalog.Photos,
            new AnalysisRunOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) });

        if (run.Failed != 0) throw new InvalidOperationException($"{run.Failed} photo(s) failed analysis.");

        // The user drops two near-duplicates from the book (R17). Exclusion happens after analysis,
        // exactly as it does in the app: the tombstone is a catalog fact, not an import filter.
        foreach (var photo in Catalog.InChapter(Year, Month).Where(IsExcludedFixture)) photo.Excluded = true;

        Journal = await ParseJournalAsync();

        Book = new Book
        {
            Id = "bk-phase3",
            Title = "Phase Three",
            Year = Year,
            PageSize = PageGeometry.DefaultPageSizeId,
            PrintProfileRef = PrintProfile.GenericId,
            Seed = Seed,
            PdfTimestampUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
    }

    /// <inheritdoc/>
    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _thumbnails?.Dispose();
        _thumbnails = null;
        _workspace.Dispose();
    }

    /// <summary>A snapshot of the project with the given chapter pages — what export and preflight read.</summary>
    public ProjectSnapshot SnapshotWith(IEnumerable<Page> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var chapter = new Chapter { Year = Year, Month = Month, Pages = [.. pages] };
        return new ProjectSnapshot(Book, Catalog, Journal, [chapter]);
    }

    // ---- the month ---------------------------------------------------------------------------------

    /// <summary>How many photographs each day of the fixture month holds.</summary>
    public static IReadOnlyList<(int Day, int Count)> DayPlan { get; } =
    [
        (3, 3),    // ── sparse run: R28's "2-3 days with 2-3 images" …
        (4, 2),
        (5, 2),
        (9, 5),
        (14, 32),  // ── the birthday
        (21, 6),
        (25, 4),
        (28, 6),
    ];

    private static void WriteMonth(string folder)
    {
        var index = 0;
        foreach (var (day, count) in DayPlan)
        {
            for (var i = 0; i < count; i++)
            {
                var name = string.Create(CultureInfo.InvariantCulture, $"IMG_{2000 + index * 10:0000}.jpg");
                SyntheticImages.Write(Path.Combine(folder, name), SpecFor(day, i, index));
                index++;
            }
        }

        // The R25 fixture: an ordinary day photo whose subject is planted in the lower-right corner,
        // as far from the center prior as the frame allows.
        SyntheticImages.Write(
            Path.Combine(folder, SubjectPhotoFileName),
            new SyntheticImages.SceneSpec
            {
                Width = 1800,
                Height = 1350,
                Subject = PlantedSubject,
                Seed = 999,
                Hue = 0.33,
                ExifTaken = new DateTime(Year, Month, 21, 15, 30, 0),
                CameraMake = "PhotoBook",
                CameraModel = "Fixture One",
            });
    }

    private static SyntheticImages.SceneSpec SpecFor(int day, int indexInDay, int index)
    {
        // Three shapes, so slot aspects actually have something to choose between; one panorama.
        var (width, height) = (index % 11) switch
        {
            0 => (1600, 1200),
            3 => (1050, 1400),
            7 => (2000, 700),
            _ => (1400, 1050),
        };

        return new SyntheticImages.SceneSpec
        {
            Width = width,
            Height = height,
            Seed = 1000 + index,
            Hue = (index % 13) / 13.0,
            // Real days are shot in bursts: minutes apart within a cluster, hours between clusters.
            ExifTaken = new DateTime(Year, Month, day, 8, 0, 0)
                .AddHours(indexInDay / 4 * 2)
                .AddMinutes(indexInDay % 4 * 7),
            CameraMake = "PhotoBook",
            CameraModel = "Fixture One",
            // A fifth of the month is soft and a tenth is blown out, so the tiers spread.
            BlurSigma = index % 5 == 2 ? 5.5 : 0,
            ExposureScale = index % 10 == 9 ? 2.6 : 1.0,
        };
    }

    /// <summary>
    /// Two near-duplicates from the middle of the birthday burst — exactly the "two images that were
    /// too much the same" of R17. They stay in the catalog as tombstones and must never be placed.
    /// </summary>
    private static bool IsExcludedFixture(Photo photo) =>
        photo.OriginalFileName is "IMG_2120.jpg" or "IMG_2130.jpg";

    // ---- the journal -------------------------------------------------------------------------------

    private async Task<JournalDocument> ParseJournalAsync()
    {
        var path = _workspace.At("journal", "2024.docx");
        SyntheticJournal.Write(path, JournalParagraphs());

        await using var stream = File.OpenRead(path);
        return await new JournalParser().ParseAsync(
            stream,
            new JournalParseRequest("2024.docx", Year, ImportedAtUtc: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    private static IEnumerable<SyntheticJournal.Para> JournalParagraphs()
    {
        foreach (var date in SparseDays)
        {
            yield return SyntheticJournal.Para.Heading(Heading(date));
            yield return new SyntheticJournal.Para(Filler(250));
        }

        // The birthday: the month's long entry, in two paragraphs.
        yield return SyntheticJournal.Para.Heading(Heading(HeavyDay));
        yield return new SyntheticJournal.Para(Filler(700));
        yield return new SyntheticJournal.Para(Filler(700));

        // A day with words and no pictures.
        yield return SyntheticJournal.Para.Heading(Heading(JournalOnlyDay));
        yield return new SyntheticJournal.Para(Filler(600));

        // …and a one-liner, because most days are one-liners.
        yield return SyntheticJournal.Para.Heading(Heading(new DateOnly(Year, Month, 25)));
        yield return new SyntheticJournal.Para(Filler(120));
    }

    private static string Heading(DateOnly date) =>
        date.ToDateTime(TimeOnly.MinValue).ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture);

    /// <summary>Deterministic prose of about <paramref name="characters"/> characters.</summary>
    public static string Filler(int characters)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(characters);
        const string sentence =
            "We walked down to the creek before breakfast and the kids found a heron standing in the shallows. ";
        var text = string.Concat(Enumerable.Repeat(sentence, characters / sentence.Length + 1));
        return text[..characters];
    }
}

/// <summary>The collection that shares one built month across the Phase 3 test classes.</summary>
[CollectionDefinition(Name)]
public sealed class SyntheticMonthCollection : ICollectionFixture<SyntheticMonth>
{
    /// <summary>The collection name.</summary>
    public const string Name = "synthetic-month";
}
