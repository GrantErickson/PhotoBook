using PhotoBook.Core.Model;
using PhotoBook.Core.Persistence;
using PhotoBook.Ingestion;
using PhotoBook.Tests.Fixtures;

namespace PhotoBook.Tests;

/// <summary>
/// Local-folder ingestion end to end (doc 05, kernel §10): a folder of synthesized images becomes
/// catalog rows and archived originals, the date chain picks EXIF over the file timestamp, a re-scan
/// is a strict no-op, and an excluded photo is never resurrected (R17).
/// </summary>
public sealed class PhotoImportTests
{
    private static readonly DateTime ImportedAt = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime[] ExifDates =
    [
        new(2024, 1, 14, 8, 5, 0),
        new(2024, 3, 2, 16, 42, 30),
        new(2024, 7, 19, 11, 15, 9),
    ];

    [Fact]
    public async Task ImportingTheFolderProducesOneRowPerDistinctImage()
    {
        using var fixture = new ImportFixture();
        var catalog = new PhotoCatalog();

        var report = await fixture.ImportAsync(catalog);

        // Six image files, one of which is a byte-for-byte duplicate under a different name, plus a
        // text file the source must visibly skip rather than silently lose.
        Assert.Equal(5, catalog.Photos.Count);
        Assert.Equal(5, report.AddedCount);
        Assert.Equal(1, report.DuplicateCount);
        Assert.Equal(1, report.UnsupportedCount);
        Assert.Equal(0, report.FailedCount);
        Assert.True(report.Completed);

        // Every row points at an archived original that actually exists and is content-hash-named.
        foreach (var photo in catalog.Photos)
        {
            var original = fixture.Paths.OriginalFile(photo.OriginalPath);
            Assert.True(File.Exists(original), $"missing original for {photo.Id}");
            Assert.StartsWith("originals/", photo.OriginalPath, StringComparison.Ordinal);
            Assert.Equal("ph-" + photo.ContentHash[..16], photo.Id);
            Assert.True(photo.Width > 0 && photo.Height > 0);
            Assert.False(photo.DecodeFailed);
        }

        // photos.json stores rows sorted by id so two saves of the same model are byte-identical.
        Assert.Equal(catalog.Photos.Select(p => p.Id).OrderBy(id => id, StringComparer.Ordinal),
            catalog.Photos.Select(p => p.Id));
    }

    [Fact]
    public async Task TheDateChainPrefersExifAndFlagsEverythingElseAsUncertain()
    {
        using var fixture = new ImportFixture();
        var catalog = new PhotoCatalog();

        await fixture.ImportAsync(catalog);

        var dated = catalog.Photos.Where(p => p.DateSource == DateSource.Exif).ToList();
        Assert.Equal(3, dated.Count);
        Assert.Equal(ExifDates.OrderBy(d => d), dated.Select(p => p.TakenAt).OrderBy(d => d));
        Assert.All(dated, p =>
        {
            Assert.False(p.DateUncertain);
            Assert.Equal(DateTimeKind.Unspecified, p.TakenAt.Kind);
        });

        // No EXIF anywhere: the file timestamp is the last resort and it is loudly uncertain.
        var undated = catalog.Photos.Where(p => p.DateSource == DateSource.FileMtime).ToList();
        Assert.Equal(2, undated.Count);
        Assert.All(undated, p => Assert.True(p.DateUncertain));
        Assert.All(undated, p => Assert.Equal(ImportFixture.MtimeUtc.ToLocalTime().Date, p.TakenAt.Date));

        // The Import Report says so, rather than leaving the user to notice.
        Assert.Equal(2, fixture.LastReport!.Of(PhotoImportOutcome.Added)
            .Count(e => e.Message == "Date taken from the file timestamp."));
    }

    [Fact]
    public async Task ExifDatesDriveChapterMembership()
    {
        using var fixture = new ImportFixture();
        var catalog = new PhotoCatalog();

        await fixture.ImportAsync(catalog);

        Assert.Single(catalog.InChapter(2024, 1));
        Assert.Single(catalog.InChapter(2024, 3));
        Assert.Single(catalog.InChapter(2024, 7));
        Assert.Empty(catalog.InChapter(2024, 2));
    }

    [Fact]
    public async Task ReImportingTheSameFolderAddsNothing()
    {
        using var fixture = new ImportFixture();
        var catalog = new PhotoCatalog();

        await fixture.ImportAsync(catalog);
        var firstPass = catalog.Photos.Select(p => (p.Id, p.ContentHash, p.TakenAt, p.OriginalPath)).ToList();
        var originalsAfterFirst = Directory.GetFiles(fixture.Paths.OriginalsFolder).Length;

        var second = await fixture.ImportAsync(catalog);

        Assert.Equal(0, second.AddedCount);
        Assert.Equal(6, second.DuplicateCount);          // every image file matched an existing row
        Assert.Equal(5, catalog.Photos.Count);
        Assert.Equal(firstPass, catalog.Photos.Select(p => (p.Id, p.ContentHash, p.TakenAt, p.OriginalPath)).ToList());
        Assert.Equal(originalsAfterFirst, Directory.GetFiles(fixture.Paths.OriginalsFolder).Length);
        Assert.Equal("Nothing to import — the source has no new photos.",
            new PhotoImportReport { Entries = [] }.Summary);
    }

    [Fact]
    public async Task AnExcludedPhotoStaysExcludedAcrossAReImport()
    {
        using var fixture = new ImportFixture();
        var catalog = new PhotoCatalog();
        await fixture.ImportAsync(catalog);

        // R17: the user removes a photo from the book. The row is a tombstone, never a deletion.
        var excluded = catalog.Photos.OrderBy(p => p.Id, StringComparer.Ordinal).First();
        excluded.Excluded = true;
        var excludedId = excluded.Id;
        var excludedTakenAt = excluded.TakenAt;

        var report = await fixture.ImportAsync(catalog);

        Assert.Equal(1, report.ExcludedCount);
        Assert.Equal(0, report.AddedCount);
        Assert.Equal(5, catalog.Photos.Count);

        var after = catalog.Find(excludedId);
        Assert.NotNull(after);
        Assert.True(after.Excluded);
        Assert.Equal(excludedTakenAt, after.TakenAt);
        Assert.DoesNotContain(after, catalog.InChapter(after.TakenAt.Year, after.TakenAt.Month));
        Assert.Contains("excluded photo skipped", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARescanNeverTouchesUserIntent()
    {
        using var fixture = new ImportFixture();
        var catalog = new PhotoCatalog();
        await fixture.ImportAsync(catalog);

        var photo = catalog.Photos.OrderBy(p => p.Id, StringComparer.Ordinal).First();
        photo.TakenAt = new DateTime(2019, 5, 5, 7, 0, 0);
        photo.DateSource = DateSource.User;
        photo.DateUncertain = false;
        photo.Adjustments = new AdjustmentStack { Brightness = 0.3 };
        photo.UserTierOverride = Tier.S;
        photo.Caption = "The good one.";
        photo.FocusRegions.Add(new FocusRegion { Rect = new Rect(0.2, 0.2, 0.3, 0.3), Kind = FocusKind.User, Weight = 1 });

        await fixture.ImportAsync(catalog);

        var after = catalog.Find(photo.Id)!;
        Assert.Equal(new DateTime(2019, 5, 5, 7, 0, 0), after.TakenAt);
        Assert.Equal(DateSource.User, after.DateSource);
        Assert.Equal(0.3, after.Adjustments.Brightness);
        Assert.Equal(Tier.S, after.UserTierOverride);
        Assert.Equal("The good one.", after.Caption);
        Assert.Single(after.FocusRegions);
    }

    [Fact]
    public async Task APhotoThatVanishedFromTheSourceIsFlaggedAndNeverDeleted()
    {
        using var fixture = new ImportFixture();
        var catalog = new PhotoCatalog();
        await fixture.ImportAsync(catalog);

        var victim = catalog.Photos.Single(p => p.OriginalFileName == "no-exif-a.png");
        File.Delete(Path.Combine(fixture.SourceFolder, "no-exif-a.png"));

        var report = await fixture.ImportAsync(catalog);

        Assert.Equal(1, report.Count(PhotoImportOutcome.RemovedFromSource));
        Assert.Equal(5, catalog.Photos.Count);
        Assert.True(catalog.Find(victim.Id)!.RemovedFromSource);
        Assert.True(File.Exists(fixture.Paths.OriginalFile(victim.OriginalPath)));
    }

    [Fact]
    public async Task ArchivedOriginalsAreMarkedReadOnlyAndByteIdenticalToTheSource()
    {
        using var fixture = new ImportFixture();
        var catalog = new PhotoCatalog();
        await fixture.ImportAsync(catalog);

        foreach (var photo in catalog.Photos)
        {
            var archived = fixture.Paths.OriginalFile(photo.OriginalPath);
            Assert.True(File.GetAttributes(archived).HasFlag(FileAttributes.ReadOnly), $"{photo.Id} is writable");

            var source = Path.Combine(fixture.SourceFolder, photo.OriginalFileName);
            if (File.Exists(source))
                Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(archived));
        }
    }

    [Fact]
    public async Task ImportedRowsSurviveTheJsonRoundTrip()
    {
        using var fixture = new ImportFixture();
        var catalog = new PhotoCatalog();
        await fixture.ImportAsync(catalog);

        var json = ProjectJson.Serialize(catalog);
        var restored = ProjectJson.Deserialize<PhotoCatalog>(json, ProjectPaths.PhotosFileName);

        Assert.Equal(catalog.Photos.Count, restored.Photos.Count);
        Assert.Equal(
            catalog.Photos.Select(p => (p.Id, p.TakenAt, p.DateSource, p.DateUncertain, p.Width, p.Height)),
            restored.Photos.Select(p => (p.Id, p.TakenAt, p.DateSource, p.DateUncertain, p.Width, p.Height)));
    }

    /// <summary>A synthesized source folder plus the project it imports into.</summary>
    private sealed class ImportFixture : IDisposable
    {
        public static readonly DateTime MtimeUtc = new(2024, 9, 30, 18, 0, 0, DateTimeKind.Utc);

        private readonly TempWorkspace _workspace = new("import");

        public ImportFixture()
        {
            Paths = new ProjectPaths(_workspace.Folder("project"));
            Paths.EnsureFolders();
            SourceFolder = _workspace.Folder("source");

            // Three files carrying real EXIF DateTimeOriginal, in three different months.
            for (var i = 0; i < ExifDates.Length; i++)
            {
                SyntheticImages.Write(
                    Path.Combine(SourceFolder, $"exif-{i}.jpg"),
                    new SyntheticImages.SceneSpec
                    {
                        Width = 900 + 40 * i,
                        Height = 600 + 30 * i,
                        Seed = 100 + i,
                        Hue = 0.2 + 0.2 * i,
                        ExifTaken = ExifDates[i],
                        CameraMake = "PhotoBook",
                        CameraModel = "Fixture One",
                    });
            }

            // Two files with no EXIF at all: the date chain must fall through to the file timestamp.
            foreach (var (name, seed) in new[] { ("no-exif-a.png", 200), ("no-exif-b.png", 201) })
            {
                var path = SyntheticImages.Write(
                    Path.Combine(SourceFolder, name),
                    new SyntheticImages.SceneSpec { Width = 800, Height = 800, Seed = seed, Hue = 0.05 * seed });
                File.SetLastWriteTimeUtc(path, MtimeUtc);
            }

            // The same bytes under a second name: identity is content identity, so this is one photo.
            File.Copy(Path.Combine(SourceFolder, "exif-0.jpg"), Path.Combine(SourceFolder, "copy-of-exif-0.jpg"));

            // A file PhotoBook does not read: skipped visibly, never silently.
            File.WriteAllText(Path.Combine(SourceFolder, "notes.txt"), "not a photo");
        }

        public ProjectPaths Paths { get; }

        public string SourceFolder { get; }

        public PhotoImportReport? LastReport { get; private set; }

        public async Task<PhotoImportReport> ImportAsync(PhotoCatalog catalog)
        {
            var importer = new PhotoImporter(Paths);
            var source = new FolderPhotoSource(SourceFolder);
            var options = new PhotoImportOptions
            {
                MaxDegreeOfParallelism = 1,      // deterministic ordering for the assertions below
                ImportedAtUtc = ImportedAt,
            };

            LastReport = await importer.ImportAsync(source, catalog, options);
            return LastReport;
        }

        public void Dispose() => _workspace.Dispose();
    }
}
