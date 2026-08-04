using PhotoBook.Analysis.Caching;
using PhotoBook.Analysis.Classical;
using PhotoBook.Analysis.Running;
using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;
using PhotoBook.Core.Persistence;
using PhotoBook.Imaging;
using PhotoBook.Ingestion;
using PhotoBook.Tests.Fixtures;

namespace PhotoBook.Tests;

/// <summary>
/// The Phase 2 seam test: one folder of synthesized photographs walked through the whole chain the
/// app will drive — Ingestion copies and catalogs, Imaging builds the analysis copies, Analysis
/// measures them and assigns tiers, and the result round-trips through <c>photos.json</c>.
/// <para>
/// This is the test that would break if the three projects had been wired to each other's
/// descriptions rather than to each other.
/// </para>
/// </summary>
public sealed class Phase2PipelineTests
{
    [Fact]
    public async Task AFolderOfPhotosBecomesACataloguedAnalysedTieredMonth()
    {
        using var workspace = new TempWorkspace("pipeline");
        var paths = new ProjectPaths(workspace.Folder("project"));
        paths.EnsureFolders();
        var sourceFolder = workspace.Folder("source");

        // --- twelve photographs of one month, deliberately unequal in quality ---------------------
        var january = new DateTime(2024, 1, 3, 9, 0, 0);
        for (var i = 0; i < 12; i++)
        {
            var spec = new SyntheticImages.SceneSpec
            {
                Width = i % 3 == 0 ? 1600 : 1200,
                Height = i % 3 == 0 ? 1200 : 1600,
                Seed = 500 + i,
                Hue = 0.07 * i,
                ExifTaken = january.AddDays(i * 2).AddHours(i),
                CameraMake = "PhotoBook",
                CameraModel = "Fixture One",
                // A third of the month is soft, a sixth is blown out: a real month is not uniform.
                BlurSigma = i % 3 == 1 ? 6.0 : 0,
                ExposureScale = i % 6 == 5 ? 2.8 : 1.0,
            };
            SyntheticImages.Write(Path.Combine(sourceFolder, $"IMG_{i:0000}.jpg"), spec);
        }

        // --- 1. ingestion --------------------------------------------------------------------------
        var catalog = new PhotoCatalog();
        var report = await new PhotoImporter(paths).ImportAsync(
            new FolderPhotoSource(sourceFolder),
            catalog,
            new PhotoImportOptions { MaxDegreeOfParallelism = 2, ImportedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc) });

        Assert.Equal(12, report.AddedCount);
        Assert.Equal(12, catalog.Photos.Count);
        Assert.All(catalog.Photos, p => Assert.Equal(DateSource.Exif, p.DateSource));
        Assert.Equal(12, catalog.InChapter(2024, 1).Count());

        // --- 2. imaging: the derived pixels analysis reads ------------------------------------------
        using var thumbnails = new ThumbnailCache(paths, ThumbnailSources.FromCatalog(paths, () => catalog));
        foreach (var photo in catalog.Photos) await thumbnails.WarmAsync(photo.ContentHash);

        foreach (var photo in catalog.Photos)
        {
            foreach (var tier in new[] { ThumbnailTier.Grid256, ThumbnailTier.Preview1024, ThumbnailTier.Analysis1024 })
                Assert.True(thumbnails.Exists(photo.ContentHash, tier), $"{photo.Id} is missing its {tier}");
        }

        // --- 3. analysis ---------------------------------------------------------------------------
        var runner = new AnalysisRunner(
            new ClassicalAnalyzer(),
            new ThumbnailCacheAnalysisCopyLocator(thumbnails),
            new FileAnalysisCache(paths));

        var run = await runner.RunAsync(catalog.Photos, new AnalysisRunOptions { MaxDegreeOfParallelism = 2 });

        Assert.Equal(12, run.Analyzed);
        Assert.Equal(0, run.Failed);
        Assert.Equal(12, run.Applied);
        Assert.All(catalog.Photos, p =>
        {
            Assert.NotNull(p.Quality);
            Assert.NotNull(p.Tier);
            Assert.NotEmpty(p.FocusRegions);
            Assert.All(p.FocusRegions, r => Assert.True(r.Rect.IsInsideUnitSquare, $"{p.Id}: {r.Rect}"));
        });

        // The month was ranked, and it spans more than one tier — a flat month would mean the signals
        // are not discriminating.
        var tiers = run.Tiers[(2024, 1)];
        Assert.Equal(12, tiers.Ranked);
        Assert.True(catalog.Photos.Select(p => p.Tier).Distinct().Count() >= 3);

        // The soft frames sank and the crisp ones floated: that is the whole point of tiering.
        var soft = catalog.Photos.Where(p => IsIndex(p, i => i % 3 == 1)).ToList();
        var crisp = catalog.Photos.Where(p => IsIndex(p, i => i % 3 == 0)).ToList();
        Assert.True(
            crisp.Average(p => p.Quality!.Sharpness) > soft.Average(p => p.Quality!.Sharpness) * 1.5,
            "the blurred third of the month should score far below the crisp third");

        // --- 4. the analysis cache is a cache, not a source of truth --------------------------------
        var cached = await runner.RunAsync(catalog.Photos, new AnalysisRunOptions { MaxDegreeOfParallelism = 2 });
        Assert.Equal(12, cached.FromCache);
        Assert.Equal(0, cached.Analyzed);
        Assert.Equal(
            catalog.Photos.Select(p => (p.Id, p.Quality!.Fused, p.Tier)),
            catalog.Photos.Select(p => (p.Id, p.Quality!.Fused, p.Tier)));

        // --- 5. persistence ------------------------------------------------------------------------
        var json = ProjectJson.Serialize(catalog);
        var restored = ProjectJson.Deserialize<PhotoCatalog>(json, ProjectPaths.PhotosFileName);

        Assert.Equal(
            catalog.Photos.Select(p => (p.Id, p.TakenAt, p.Tier, p.Quality!.Fused, p.FocusRegions.Count)),
            restored.Photos.Select(p => (p.Id, p.TakenAt, p.Tier, p.Quality!.Fused, p.FocusRegions.Count)));

        // --- 6. deleting cache/ loses nothing -------------------------------------------------------
        Directory.Delete(paths.CacheFolder, recursive: true);
        paths.EnsureFolders();
        var rebuilt = ProjectJson.Deserialize<PhotoCatalog>(json, ProjectPaths.PhotosFileName);
        Assert.Equal(12, rebuilt.Photos.Count);
        await thumbnails.WarmAsync(rebuilt.Photos[0].ContentHash);
        Assert.True(thumbnails.Exists(rebuilt.Photos[0].ContentHash, ThumbnailTier.Preview1024));

        static bool IsIndex(Photo photo, Func<int, bool> predicate) =>
            int.TryParse(Path.GetFileNameWithoutExtension(photo.OriginalFileName).AsSpan(4), out var index)
            && predicate(index);
    }

    [Fact]
    public async Task ATonEditRebuildsThePreviewButLeavesTheAnalysisResultUntouched()
    {
        using var workspace = new TempWorkspace("pipeline-edit");
        var paths = new ProjectPaths(workspace.Folder("project"));
        paths.EnsureFolders();
        var sourceFolder = workspace.Folder("source");

        SyntheticImages.Write(
            Path.Combine(sourceFolder, "IMG_0001.jpg"),
            new SyntheticImages.SceneSpec { Width = 1400, Height = 1050, ExifTaken = new DateTime(2024, 5, 6, 10, 0, 0) });

        var catalog = new PhotoCatalog();
        await new PhotoImporter(paths).ImportAsync(new FolderPhotoSource(sourceFolder), catalog);
        var photo = catalog.Photos.Single();

        using var thumbnails = new ThumbnailCache(paths, ThumbnailSources.FromCatalog(paths, () => catalog));
        var runner = new AnalysisRunner(
            new ClassicalAnalyzer(),
            new ThumbnailCacheAnalysisCopyLocator(thumbnails),
            new FileAnalysisCache(paths));

        await runner.RunAsync(catalog.Photos);
        var before = (Quality: photo.Quality!.Fused, Regions: photo.FocusRegions.Select(r => r.Rect).ToList());
        var analysisCopy = thumbnails.PathFor(photo.ContentHash, ThumbnailTier.Analysis1024);
        var analysisBytes = await File.ReadAllBytesAsync(analysisCopy);

        // The user brightens the photo in the editor.
        photo.Adjustments = new AdjustmentStack { Brightness = 0.45, Contrast = 0.2 };

        Assert.False(thumbnails.Exists(photo.ContentHash, ThumbnailTier.Preview1024));
        Assert.True(thumbnails.Exists(photo.ContentHash, ThumbnailTier.Analysis1024));

        await thumbnails.WarmAsync(photo.ContentHash);
        Assert.Equal(analysisBytes, await File.ReadAllBytesAsync(analysisCopy));

        // Re-running analysis reads the untouched analysis copy, so the score does not drift.
        await runner.RunAsync(catalog.Photos);
        Assert.Equal(before.Quality, photo.Quality!.Fused);
        Assert.Equal(before.Regions, photo.FocusRegions.Select(r => r.Rect).ToList());
    }
}
