using PhotoBook.Core.Model;
using PhotoBook.Core.Persistence;
using PhotoBook.Core.Templates;

namespace PhotoBook.Tests;

/// <summary>
/// The book's own settings, which R19 needs and which had no surface at all until the book-settings
/// panel: the year the book covers, the trim it prints at, and the seed that makes its layout
/// reproducible.
/// <para>
/// The three things worth pinning down are the ones with consequences past their own field — a year
/// move takes the chapters with it and strands photos in the Outside-book tray, a page-size move is
/// only honest if the app can say how many layouts exist at each size, and a seed is worthless if it
/// does not survive a save.
/// </para>
/// </summary>
public class BookSettingsTests
{
    private static readonly DateTime CreatedAt = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------ year

    [Fact]
    public void MovingTheYear_TakesEveryChapterWithIt()
    {
        var book = new Book { Title = "Family 2024", Year = 2024 };
        var chapters = Enumerable.Range(1, 12).Select(m => new Chapter { Year = 2024, Month = m }).ToList();

        BookYear.MoveTo(book, chapters, 2025);

        Assert.Equal(2025, book.Year);
        Assert.All(chapters, c => Assert.Equal(2025, c.Year));

        // A default title carries the year, so leaving it behind would have the shell showing a year
        // the book no longer covers.
        Assert.Equal("Family 2025", book.Title);
    }

    [Fact]
    public void MovingTheYear_LeavesOutOfYearPhotosInTheOutsideBookTray()
    {
        var book = new Book { Title = "Our year", Year = 2024 };
        var chapters = Enumerable.Range(1, 12).Select(m => new Chapter { Year = 2024, Month = m }).ToList();

        var catalog = new PhotoCatalog
        {
            Photos =
            {
                MakePhoto("aa", new DateTime(2024, 3, 4, 9, 0, 0)),
                MakePhoto("bb", new DateTime(2024, 7, 1, 9, 0, 0)),
                MakePhoto("cc", new DateTime(2025, 2, 2, 9, 0, 0)),
                MakePhoto("dd", new DateTime(2025, 6, 6, 9, 0, 0)),
                MakePhoto("ee", new DateTime(2025, 9, 9, 9, 0, 0)),
            },
        };

        // Before the move: the two 2024 photos are in chapters, the three 2025 ones are stranded.
        Assert.Equal(3, BookYear.PhotosOutside(catalog, 2024));
        Assert.Equal(2, catalog.InChapter(2024, 3).Count() + catalog.InChapter(2024, 7).Count());

        BookYear.MoveTo(book, chapters, 2025);

        // After: the tray and the chapters swap over. Nothing was deleted either way (R6, R17).
        Assert.Equal(2, BookYear.PhotosOutside(catalog, book.Year));
        var stranded = catalog.OutsideBookTray(book.Year).Select(p => p.TakenAt.Year).ToList();
        Assert.Equal([2024, 2024], stranded);
        Assert.Equal(3, catalog.InChapter(2025, 2).Count()
                        + catalog.InChapter(2025, 6).Count()
                        + catalog.InChapter(2025, 9).Count());
        Assert.Equal(5, catalog.Photos.Count);
    }

    [Fact]
    public void TheOutsideYearNote_CountsWhatAYearWouldStrandBeforeItIsCommitted()
    {
        var catalog = new PhotoCatalog
        {
            Photos =
            {
                MakePhoto("aa", new DateTime(2024, 3, 4, 9, 0, 0)),
                MakePhoto("bb", new DateTime(2025, 3, 4, 9, 0, 0)),
                MakePhoto("cc", new DateTime(2025, 4, 4, 9, 0, 0)),
            },
        };

        Assert.Contains("3 photos are outside 2026", BookYear.OutsideNote(catalog, 2026)!, StringComparison.Ordinal);
        Assert.Contains("2 photos are outside 2024", BookYear.OutsideNote(catalog, 2024)!, StringComparison.Ordinal);
        Assert.Contains("1 photo is outside 2025", BookYear.OutsideNote(catalog, 2025)!, StringComparison.Ordinal);

        // An excluded photo is a tombstone, not a stranded photo (R17), and a year that strands
        // nothing says nothing rather than "0 photos".
        catalog.Photos[0].Excluded = true;
        Assert.Null(BookYear.OutsideNote(catalog, 2025));
        Assert.Equal(0, BookYear.PhotosOutside(catalog, 2025));
    }

    [Fact]
    public void AYearMove_IsExactlyReversible()
    {
        var book = new Book { Title = "Family 2024", Year = 2024 };
        var chapters = Enumerable.Range(1, 12).Select(m => new Chapter { Year = 2024, Month = m }).ToList();
        var before = BookYear.Capture(book, chapters);

        BookYear.MoveTo(book, chapters, 2031);
        BookYear.Restore(book, chapters, before);

        Assert.Equal(2024, book.Year);
        Assert.Equal("Family 2024", book.Title);
        Assert.All(chapters, c => Assert.Equal(2024, c.Year));
    }

    // ------------------------------------------------------------------ page size

    [Fact]
    public void TemplateAvailability_IsReportedPerPageSize()
    {
        var library = TemplateLibrary.Default;
        var rows = PageSizeAvailability.ForProfile(library, BuiltInPrintProfiles.Generic);

        // Every trim the profile prints gets a row: the picker must be able to say "no layouts yet"
        // rather than hiding a size the geometry genuinely supports (doc 12 "Other page sizes").
        Assert.Equal(BuiltInPrintProfiles.Generic.PageSizes.Count, rows.Count);

        var landscape = rows.Single(r => r.PageSizeId == PageGeometry.DefaultPageSizeId);
        Assert.Equal(library.Count, landscape.TemplateCount);
        Assert.Equal(library.CountForPageSize(PageGeometry.DefaultPageSizeId), landscape.TemplateCount);
        Assert.True(landscape.MonthTitleCount > 0, "a chapter needs a month-title layout (R24)");
        Assert.True(landscape.MaxPhotosPerPage is >= 1 and <= 8, "R20 caps a page at eight photos");
        Assert.True(landscape.CanLayOutChapter);
        Assert.Null(landscape.Blocker);
        Assert.Contains("layouts", landscape.Summary, StringComparison.Ordinal);

        // v1 ships one authored set. Any other trim reports zero honestly instead of borrowing it —
        // templates are never stretched across page shapes (doc 07).
        foreach (var other in rows.Where(r => r.PageSizeId != PageGeometry.DefaultPageSizeId))
        {
            Assert.Equal(0, other.TemplateCount);
            Assert.False(other.HasLayouts);
            Assert.False(other.CanLayOutChapter);
            Assert.NotNull(other.Blocker);
            Assert.Contains(other.Size.DisplayName, other.Blocker!, StringComparison.Ordinal);
        }

        Assert.Equal([PageGeometry.DefaultPageSizeId], library.PageSizes);
    }

    [Fact]
    public void ChangingPageSize_NamesEveryPageThatNoLongerMatchesTheBook()
    {
        var library = TemplateLibrary.Default;
        var target = BuiltInPrintProfiles.Generic.PageSizes.First(s => s.Id != PageGeometry.DefaultPageSizeId);

        var chapter = new Chapter
        {
            Year = 2024,
            Month = 3,
            Pages =
            {
                new Page { Id = "pg-1", TemplateRef = library.Templates[0].Id },
                new Page { Id = "pg-2", TemplateRef = library.Templates[1].Id },
                new Page { Id = "pg-3", DetachedTemplate = library.Templates[2].ToDetachedSnapshot() },
            },
        };

        var change = PageSizeChange.Inspect(
            [chapter],
            library.Find,
            PageGeometry.DefaultPageSizeId,
            target.Id,
            PageSizeAvailability.For(library, target));

        Assert.True(change.IsChange);
        Assert.Equal(3, change.LaidOutPageCount);
        Assert.Equal(3, change.MismatchedPageCount);

        // The hand-built page is counted apart because no layout run will ever replace it (R15) —
        // promising "re-run layout and it is fixed" would be a lie for that one.
        Assert.Equal(1, change.MismatchedDetachedPageCount);
        Assert.True(change.NeedsRelayout);

        // The new trim has no layouts, so the move is refused with the reason rather than allowed
        // to produce pages whose template does not match the book.
        Assert.False(change.IsAllowed);
        Assert.Contains("No page layouts have been authored", change.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void StayingOnTheSamePageSize_IsNeitherAChangeNorAWarning()
    {
        var library = TemplateLibrary.Default;
        var size = BuiltInPrintProfiles.Generic.PageSizes[0];
        var chapter = new Chapter
        {
            Year = 2024,
            Month = 3,
            Pages = { new Page { Id = "pg-1", TemplateRef = library.Templates[0].Id } },
        };

        var change = PageSizeChange.Inspect(
            [chapter],
            library.Find,
            PageGeometry.DefaultPageSizeId,
            PageGeometry.DefaultPageSizeId,
            PageSizeAvailability.For(library, size));

        Assert.False(change.IsChange);
        Assert.True(change.IsAllowed);
        Assert.Equal(0, change.MismatchedPageCount);
        Assert.False(change.NeedsRelayout);
        Assert.Equal(string.Empty, change.Warning);
    }

    [Fact]
    public void EveryProfilePageSizeCarriesUsableGeometryAndALabel()
    {
        foreach (var size in BuiltInPrintProfiles.Generic.PageSizes)
        {
            Assert.False(string.IsNullOrWhiteSpace(size.Id));
            Assert.True(size.TrimWidthIn > 0 && size.TrimHeightIn > 0);
            Assert.Contains("in", size.DisplayName, StringComparison.Ordinal);
            Assert.Contains(size.Orientation, size.DisplayName, StringComparison.Ordinal);
        }

        var landscape = BuiltInPrintProfiles.Generic.PageSizes[0];
        Assert.Equal(PageGeometry.DefaultPageSizeId, landscape.Id);
        Assert.Equal("landscape", landscape.Orientation);
        Assert.Equal(PageGeometry.DefaultPageAspect, landscape.Aspect, 6);

        // A profile the app does not ship still resolves, so a book from a newer build opens.
        Assert.Equal(PrintProfile.GenericId, BuiltInPrintProfiles.Find("no-such-profile").Id);
        Assert.Equal(PrintProfile.GenericId, BuiltInPrintProfiles.Find(null).Id);
    }

    // ------------------------------------------------------------------ seed

    [Fact]
    public async Task TheSeedRoundTripsThroughBookJson()
    {
        using var temp = new TempFolder();

        var book = await ProjectStore.CreateNewAsync(
            temp.Path, 2024, "Our 2024", seed: 0xC0FFEE, createdAtUtc: CreatedAt);
        var store = new ProjectStore(temp.Path);

        // A shuffle in book settings is exactly this: a new seed written onto the open book.
        var shuffled = ProjectStore.NewSeed();
        Assert.NotEqual(book.Seed, shuffled);
        book.Seed = shuffled;
        await store.SaveBookAsync(book);

        var reloaded = (await new ProjectStore(temp.Path).LoadAsync()).Value.Book;
        Assert.Equal(shuffled, reloaded.Seed);

        // ulong, not long: a seed above 2^63 must survive rather than wrap (kernel §7).
        book.Seed = ulong.MaxValue - 3;
        await store.SaveBookAsync(book);
        Assert.Equal(ulong.MaxValue - 3, (await new ProjectStore(temp.Path).LoadAsync()).Value.Book.Seed);
    }

    [Fact]
    public async Task PageSizeAndPrintProfileRoundTripThroughBookJson()
    {
        using var temp = new TempFolder();

        var book = await ProjectStore.CreateNewAsync(
            temp.Path, 2024, "Our 2024", seed: 1, createdAtUtc: CreatedAt);
        var store = new ProjectStore(temp.Path);

        var target = BuiltInPrintProfiles.Generic.PageSizes.Last();
        book.PageSize = target.Id;
        book.PrintProfileRef = PrintProfile.GenericId;
        await store.SaveBookAsync(book);

        var reloaded = (await new ProjectStore(temp.Path).LoadAsync()).Value.Book;
        Assert.Equal(target.Id, reloaded.PageSize);
        Assert.Equal(PrintProfile.GenericId, reloaded.PrintProfileRef);
        Assert.NotNull(BuiltInPrintProfiles.Find(reloaded.PrintProfileRef).FindPageSize(reloaded.PageSize));
    }

    private static Photo MakePhoto(string hashSeed, DateTime takenAt)
    {
        var hash = string.Concat(Enumerable.Repeat(hashSeed, 32))[..64];
        return new Photo
        {
            Id = Ids.PhotoId(hash),
            ContentHash = hash,
            OriginalFileName = $"IMG_{hashSeed}.jpg",
            TakenAt = takenAt,
            DateSource = DateSource.Exif,
            Width = 4032,
            Height = 3024,
        };
    }
}
