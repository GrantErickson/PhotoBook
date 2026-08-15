using System.Text.Json;
using PhotoBook.Core.Model;
using PhotoBook.Core.Persistence;

namespace PhotoBook.Tests;

/// <summary>
/// Persistence round-trips (doc 04, doc 13 "Build + unit … persistence round-trips"): create a
/// project, save a book with photos and a chapter, reload it from disk, and assert every meaningful
/// field survived — plus the on-disk contract: camelCase names, a <c>schemaVersion</c> per file, and
/// an atomic save that leaves no temp file behind.
/// </summary>
public class ProjectRoundTripTests
{
    private static readonly DateTime CreatedAt = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// An unreadable image is catalogued with <c>decodeFailed</c> and zero dimensions — the importer
    /// says so explicitly ("0 when unknown") and never aborts a batch over one bad file. Every photo
    /// row must therefore survive a save with no dimensions at all.
    /// </summary>
    [Fact]
    public async Task AnUndecodablePhotoDoesNotStopTheProjectFromSaving()
    {
        using var temp = new TempFolder();
        await ProjectStore.CreateNewAsync(temp.Path, 2024, "Our 2024", seed: 1, createdAtUtc: CreatedAt);
        var store = new ProjectStore(temp.Path);

        var catalog = new PhotoCatalog
        {
            Photos =
            {
                new Photo
                {
                    Id = "ph-broken",
                    ContentHash = new string('c', 64),
                    OriginalFileName = "corrupt.jpg",
                    OriginalPath = "originals/corrupt.jpg",
                    TakenAt = new DateTime(2024, 3, 4, 9, 30, 0),
                    Width = 0,
                    Height = 0,
                    DecodeFailed = true,
                },
            },
        };

        // Before the fix this threw ArgumentException from deep inside the serializer — "positive and
        // negative infinity cannot be written as valid JSON" — because the derived aspect ratio of a
        // zero-height photo is NaN. The project simply stopped saving, for good, from the moment one
        // unreadable file was imported.
        await store.SavePhotosAsync(catalog);

        var reloaded = await store.LoadPhotosAsync();
        Assert.Single(reloaded.Value.Photos);
        Assert.True(reloaded.Value.Photos[0].DecodeFailed);
    }

    [Fact]
    public async Task CreateSaveReload_PreservesEveryMeaningfulField()
    {
        using var temp = new TempFolder();

        var book = await ProjectStore.CreateNewAsync(temp.Path, 2024, "Our 2024", seed: 0xC0FFEE, createdAtUtc: CreatedAt);
        var store = new ProjectStore(temp.Path);

        book.PageSize = PageGeometry.DefaultPageSizeId;
        book.Source = new BookSource { Kind = BookSourceKind.OneDriveAlbum, Id = "album-2024", Path = "Book 2024" };
        book.Style.JournalText!.SizePt = 11.5;

        var catalog = new PhotoCatalog { Photos = { MakePhoto("aa", new DateTime(2024, 3, 4, 9, 30, 0)), MakePhoto("bb", new DateTime(2024, 3, 5, 18, 0, 0)) } };
        var chapter = MakeChapter(2024, 3, catalog.Photos[0].Id, catalog.Photos[1].Id);

        await store.SaveBookAsync(book);
        await store.SavePhotosAsync(catalog);
        await store.SaveChapterAsync(chapter);

        var reloaded = (await new ProjectStore(temp.Path).LoadAsync()).Value;

        // --- book
        Assert.Equal(book, reloaded.Book); // Book has no collection members, so record equality is deep
        Assert.Equal("Our 2024", reloaded.Book.Title);
        Assert.Equal(2024, reloaded.Book.Year);
        Assert.Equal(0xC0FFEEUL, reloaded.Book.Seed);
        Assert.Equal(CreatedAt, reloaded.Book.PdfTimestampUtc);
        Assert.Equal(PrintProfile.GenericId, reloaded.Book.PrintProfileRef);
        Assert.Equal(BookSourceKind.OneDriveAlbum, reloaded.Book.Source.Kind);
        Assert.Equal("album-2024", reloaded.Book.Source.Id);
        Assert.Equal("Book 2024", reloaded.Book.Source.Path);
        Assert.Equal(11.5, reloaded.Book.Style.JournalText!.SizePt);
        Assert.Equal("Source Serif 4", reloaded.Book.Style.JournalText!.Family);
        Assert.Equal("#000000", reloaded.Book.Style.Background!.Color);

        // --- photos
        Assert.Equal(2, reloaded.Photos.Photos.Count);
        foreach (var expected in catalog.Photos)
        {
            var actual = reloaded.Photos.Find(expected.Id);
            Assert.NotNull(actual);
            AssertPhotoEqual(expected, actual);
        }

        // --- chapter
        var loadedChapter = Assert.Single(reloaded.Chapters);
        Assert.Equal(2024, loadedChapter.Year);
        Assert.Equal(3, loadedChapter.Month);
        Assert.Equal("March 2024", loadedChapter.Title);
        Assert.Equal(9.5, loadedChapter.StyleOverride!.CaptionText!.SizePt);
        Assert.Equal(2, loadedChapter.Pages.Count);

        var page = loadedChapter.Pages[0];
        Assert.Equal(chapter.Pages[0].Id, page.Id);
        Assert.Equal("t-02-text-a", page.TemplateRef);
        Assert.True(page.Pinned);
        Assert.True(page.Mirrored);
        var placement = Assert.Single(page.Placements);
        Assert.Equal("s1", placement.SlotId);
        Assert.Equal(catalog.Photos[0].Id, placement.PhotoId);
        Assert.Equal(new CropState(1.4, -0.12, 0.05), placement.Crop);
        var assignment = Assert.Single(page.JournalAssignments);
        Assert.Equal("t1", assignment.TextSlotId);
        Assert.Equal(["je-abc", "je-def"], assignment.EntryIds);

        // A detached page keeps its inline snapshot, not a library reference (R15).
        var detached = loadedChapter.Pages[1];
        Assert.True(detached.IsDetached);
        Assert.Null(detached.TemplateRef);
        Assert.Equal("t-01-text-a", detached.DetachedTemplate!.BasedOn);
        Assert.Equal(new Rect(0.05, 0.05, 0.5, 0.4), detached.DetachedTemplate.Slots[0].Rect);
        Assert.Equal(TierAffinity.S, detached.DetachedTemplate.Slots[0].TierAffinity);

        // --- chapter membership is computed from dates, never stored (doc 03 §4)
        Assert.Equal(2, reloaded.Photos.InChapter(2024, 3).Count());
    }

    [Fact]
    public async Task SavedJson_IsCamelCaseAndCarriesASchemaVersion()
    {
        using var temp = new TempFolder();
        await ProjectStore.CreateNewAsync(temp.Path, 2024, "Our 2024", seed: 1, createdAtUtc: CreatedAt);
        var store = new ProjectStore(temp.Path);

        var catalog = new PhotoCatalog { Photos = { MakePhoto("aa", new DateTime(2024, 3, 4, 9, 30, 0)) } };
        await store.SavePhotosAsync(catalog);
        await store.SaveChapterAsync(MakeChapter(2024, 3, catalog.Photos[0].Id, catalog.Photos[0].Id));

        foreach (var file in new[] { store.Paths.BookFile, store.Paths.PhotosFile, store.Paths.JournalFile, store.Paths.ChapterFile(2024, 3) })
        {
            var text = await File.ReadAllTextAsync(file);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            Assert.True(root.TryGetProperty("schemaVersion", out var version), $"{Path.GetFileName(file)} has no schemaVersion");
            Assert.Equal(1, version.GetInt32());

            foreach (var name in AllPropertyNames(root))
            {
                Assert.True(char.IsLower(name[0]) || !char.IsLetter(name[0]),
                    $"{Path.GetFileName(file)} property '{name}' is not camelCase");
            }
        }

        // Enum spellings the docs show verbatim: Tier uppercase, everything else camelCase.
        var photosText = await File.ReadAllTextAsync(store.Paths.PhotosFile);
        Assert.Contains("\"tier\": \"S\"", photosText);
        Assert.Contains("\"dateSource\": \"exif\"", photosText);
        Assert.Contains("\"kind\": \"face\"", photosText);

        var chapterText = await File.ReadAllTextAsync(store.Paths.ChapterFile(2024, 3));
        Assert.Contains("\"tierAffinity\": \"S\"", chapterText);
    }

    [Fact]
    public async Task AtomicSave_LeavesNoTempFileAndKeepsARollingBackup()
    {
        using var temp = new TempFolder();
        await ProjectStore.CreateNewAsync(temp.Path, 2024, "Our 2024", seed: 1, createdAtUtc: CreatedAt);
        var store = new ProjectStore(temp.Path);

        var book = (await store.LoadBookAsync()).Value;
        book.Title = "Our 2024, revised";
        await store.SaveBookAsync(book);
        await store.SaveChapterAsync(MakeChapter(2024, 3, "ph-a", "ph-a"));

        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.tmp", SearchOption.AllDirectories));
        Assert.True(File.Exists(store.Paths.BookFile + ProjectPaths.BackupSuffix), "the previous good book.json must survive as .bak");

        var backup = await File.ReadAllTextAsync(store.Paths.BookFile + ProjectPaths.BackupSuffix);
        Assert.Contains("Our 2024", backup);
        Assert.DoesNotContain("revised", backup);
        Assert.Equal("Our 2024, revised", (await store.LoadBookAsync()).Value.Title);
    }

    [Fact]
    public async Task Load_SweepsAStrayTempFileAndReportsIt()
    {
        using var temp = new TempFolder();
        await ProjectStore.CreateNewAsync(temp.Path, 2024, "Our 2024", seed: 1, createdAtUtc: CreatedAt);
        var store = new ProjectStore(temp.Path);

        await File.WriteAllTextAsync(store.Paths.BookFile + ProjectPaths.TempSuffix, "{ half-written");

        var result = await store.LoadAsync();

        Assert.False(File.Exists(store.Paths.BookFile + ProjectPaths.TempSuffix));
        Assert.Contains(result.Notices, n => n.Kind == ProjectLoadNoticeKind.DeletedStrayTemp);
        Assert.Equal("Our 2024", result.Value.Book.Title);
    }

    [Fact]
    public async Task SavingTwice_ProducesByteIdenticalFiles()
    {
        using var temp = new TempFolder();
        await ProjectStore.CreateNewAsync(temp.Path, 2024, "Our 2024", seed: 7, createdAtUtc: CreatedAt);
        var store = new ProjectStore(temp.Path);

        // Photos are canonicalized on write (sorted by id), so save order must not matter (doc 04 §4).
        var a = MakePhoto("aa", new DateTime(2024, 3, 4, 9, 30, 0));
        var b = MakePhoto("bb", new DateTime(2024, 3, 5, 18, 0, 0));

        await store.SavePhotosAsync(new PhotoCatalog { Photos = { a, b } });
        var first = await File.ReadAllTextAsync(store.Paths.PhotosFile);

        await store.SavePhotosAsync(new PhotoCatalog { Photos = { b, a } });
        var second = await File.ReadAllTextAsync(store.Paths.PhotosFile);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task SavingAChapterWithNoPages_RemovesItsFile()
    {
        using var temp = new TempFolder();
        await ProjectStore.CreateNewAsync(temp.Path, 2024, "Our 2024", seed: 1, createdAtUtc: CreatedAt);
        var store = new ProjectStore(temp.Path);

        await store.SaveChapterAsync(MakeChapter(2024, 3, "ph-a", "ph-a"));
        Assert.True(File.Exists(store.Paths.ChapterFile(2024, 3)));

        await store.SaveChapterAsync(new Chapter { Year = 2024, Month = 3 });
        Assert.False(File.Exists(store.Paths.ChapterFile(2024, 3)));
        Assert.Empty((await store.LoadChaptersAsync()).Value);
    }

    // ---------------------------------------------------------------- helpers

    private static Photo MakePhoto(string hashSeed, DateTime takenAt)
    {
        var hash = string.Concat(Enumerable.Repeat(hashSeed, 32))[..64];
        return new Photo
        {
            Id = Ids.PhotoId(hash),
            ContentHash = hash,
            OriginalFileName = $"IMG_{hashSeed}.heic",
            OriginalPath = ProjectPaths.OriginalRelativePath(hash, $"IMG_{hashSeed}.heic"),
            Source = new PhotoSourceRef { Kind = PhotoSourceKind.OneDrive, DriveItemId = $"drive-{hashSeed}", ImportedAtUtc = CreatedAt },
            TakenAt = takenAt,
            DateSource = DateSource.Exif,
            Width = 4032,
            Height = 3024,
            Adjustments = new AdjustmentStack { Brightness = 0.1, Contrast = -0.05, Saturation = 0.2 },
            FocusRegions = { new FocusRegion { Rect = new Rect(0.3, 0.2, 0.25, 0.3), Weight = 0.9, Kind = FocusKind.Face } },
            Quality = new QualityScore { Aesthetic = 0.8, Sharpness = 0.7, Exposure = 0.6, FaceBonus = 0.5, Fused = 0.72, MonthPercentile = 93 },
            Tier = Tier.S,
            PersonTags = { new PersonTag { Name = "Ada", Source = PersonTagSource.OneDrive } },
            Caption = "First light",
        };
    }

    private static Chapter MakeChapter(int year, int month, string photoA, string photoB) => new()
    {
        Year = year,
        Month = month,
        Title = "March 2024",
        StyleOverride = new Style { CaptionText = new TextStyle { SizePt = 9.5 } },
        Pages =
        {
            new Page
            {
                Id = "pg-0001",
                TemplateRef = "t-02-text-a",
                Mirrored = true,
                Pinned = true,
                Placements =
                {
                    new Placement { SlotId = "s1", PhotoId = photoA, Crop = new CropState(1.4, -0.12, 0.05) },
                },
                JournalAssignments =
                {
                    new JournalAssignment { TextSlotId = "t1", EntryIds = { "je-abc", "je-def" } },
                },
            },
            new Page
            {
                Id = "pg-0002",
                Pinned = true,
                DetachedTemplate = new Template
                {
                    BasedOn = "t-01-text-a",
                    Name = "One up with journal (edited)",
                    Kind = TemplateKind.Standard,
                    PhotoCount = 1,
                    Mirrorable = false,
                    Slots =
                    {
                        new ImageSlot
                        {
                            Id = "s1",
                            Rect = new Rect(0.05, 0.05, 0.5, 0.4),
                            Aspect = 1.6176,
                            TierAffinity = TierAffinity.S,
                            CaptionPolicy = CaptionPolicy.Below,
                        },
                    },
                    TextSlots =
                    {
                        new TextSlot { Id = "t1", Rect = new Rect(0.6, 0.05, 0.3, 0.4), Role = TextRole.Journal, Align = TextAlign.Left },
                    },
                },
                Placements = { new Placement { SlotId = "s1", PhotoId = photoB, Crop = CropState.Default } },
            },
        },
    };

    private static void AssertPhotoEqual(Photo expected, Photo actual)
    {
        Assert.Equal(expected.ContentHash, actual.ContentHash);
        Assert.Equal(expected.OriginalFileName, actual.OriginalFileName);
        Assert.Equal(expected.OriginalPath, actual.OriginalPath);
        Assert.Equal(expected.TakenAt, actual.TakenAt);
        Assert.Equal(expected.DateSource, actual.DateSource);
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Adjustments, actual.Adjustments);
        Assert.Equal(expected.Quality, actual.Quality);
        Assert.Equal(expected.Tier, actual.Tier);
        Assert.Equal(expected.EffectiveTier, actual.EffectiveTier);
        Assert.Equal(expected.Caption, actual.Caption);
        Assert.Equal(expected.Source.Kind, actual.Source.Kind);
        Assert.Equal(expected.Source.DriveItemId, actual.Source.DriveItemId);
        Assert.Equal(expected.Source.ImportedAtUtc, actual.Source.ImportedAtUtc);
        Assert.Equal(expected.FocusRegions.Count, actual.FocusRegions.Count);
        Assert.Equal(expected.FocusRegions[0].Rect, actual.FocusRegions[0].Rect);
        Assert.Equal(expected.FocusRegions[0].Kind, actual.FocusRegions[0].Kind);
        Assert.Equal(expected.FocusRegions[0].Weight, actual.FocusRegions[0].Weight);
        Assert.Equal(expected.PersonTags.Count, actual.PersonTags.Count);
        Assert.Equal(expected.PersonTags[0].Name, actual.PersonTags[0].Name);
    }

    private static IEnumerable<string> AllPropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var nested in AllPropertyNames(property.Value)) yield return nested;
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in AllPropertyNames(item)) yield return nested;
                }

                break;
        }
    }
}

/// <summary>A throwaway project folder that deletes itself at the end of a test.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PhotoBook.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a green test over.
        }
    }
}
