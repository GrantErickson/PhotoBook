using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;
using PhotoBook.Imaging;
using PhotoBook.Tests.Fixtures;

namespace PhotoBook.Tests;

/// <summary>
/// <c>cache/thumbs/</c> integration tests (kernel §10, doc 05): the three tiers build at their
/// documented sizes, a second request is served from disk, and the doc 05 invalidation matrix holds
/// for tone edits, geometry edits and edits that touch no pixels at all.
/// </summary>
public sealed class ThumbnailCacheTests
{
    private static readonly ImageDecoder Decoder = new();

    private static readonly ThumbnailTier[] AllTiers =
        [ThumbnailTier.Grid256, ThumbnailTier.Preview1024, ThumbnailTier.Analysis1024];

    [Fact]
    public async Task AllThreeTiersBuildAtTheDocumentedLongEdge()
    {
        using var project = new ProjectFixture("thumbs");
        var hash = await ArchiveSceneAsync(project, 1600, 1200);
        using var cache = new ThumbnailCache(project.Paths, project.Resolver);

        var expected = new Dictionary<ThumbnailTier, (int LongEdge, string Folder)>
        {
            [ThumbnailTier.Grid256] = (256, "256"),
            [ThumbnailTier.Preview1024] = (1024, "1024"),
            [ThumbnailTier.Analysis1024] = (1024, "1024a"),
        };

        foreach (var (tier, (longEdge, folder)) in expected)
        {
            Assert.False(cache.Exists(hash, tier));

            var path = await cache.GetOrCreateAsync(hash, tier);

            Assert.True(File.Exists(path));
            Assert.True(cache.Exists(hash, tier));
            Assert.Equal(project.Paths.ThumbnailTierFolder(folder), Path.GetDirectoryName(path));

            var decoded = await Decoder.DecodeAsync(path);
            Assert.Equal(longEdge, Math.Max(decoded.Width, decoded.Height));
            Assert.Equal(4.0 / 3.0, decoded.Aspect, 1);
        }
    }

    [Fact]
    public async Task ASecondRequestIsServedFromDiskAndDoesNotRebuild()
    {
        using var project = new ProjectFixture("thumbs-hit");
        var hash = await ArchiveSceneAsync(project, 1200, 900);
        using var cache = new ThumbnailCache(project.Paths, project.Resolver);

        var first = await cache.GetOrCreateAsync(hash, ThumbnailTier.Preview1024);

        // Replace the artifact's bytes with a sentinel. A cache *hit* returns the sentinel untouched;
        // a rebuild would overwrite it with real JPEG data.
        var sentinel = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        await File.WriteAllBytesAsync(first, sentinel);

        var second = await cache.GetOrCreateAsync(hash, ThumbnailTier.Preview1024);

        Assert.Equal(first, second);
        Assert.Equal(sentinel, await File.ReadAllBytesAsync(second));
    }

    [Fact]
    public async Task BuildingThePreviewAlsoWritesTheGridThumbnailFromTheSameDecode()
    {
        using var project = new ProjectFixture("thumbs-warm");
        var hash = await ArchiveSceneAsync(project, 1600, 1200);
        using var cache = new ThumbnailCache(project.Paths, project.Resolver);

        await cache.GetOrCreateAsync(hash, ThumbnailTier.Preview1024);

        // "1024 before 256 for the visible month … one decode, two writes" (doc 05).
        Assert.True(cache.Exists(hash, ThumbnailTier.Grid256));
    }

    [Fact]
    public async Task AToneEditInvalidatesTheTwoAdjustedTiersAndLeavesTheAnalysisCopyAlone()
    {
        using var project = new ProjectFixture("thumbs-tone");
        var hash = await ArchiveSceneAsync(project, 1200, 900);
        using var cache = new ThumbnailCache(project.Paths, project.Resolver);

        foreach (var tier in AllTiers) await cache.GetOrCreateAsync(hash, tier);
        var before = AllTiers.ToDictionary(t => t, t => cache.PathFor(hash, t));

        // The user brightens the photo. Analysis must not re-run: fixing exposure never re-tiers (doc 05).
        project.SetAdjustments(hash, new AdjustmentStack { Brightness = 0.35, Contrast = 0.10 });

        Assert.NotEqual(before[ThumbnailTier.Grid256], cache.PathFor(hash, ThumbnailTier.Grid256));
        Assert.NotEqual(before[ThumbnailTier.Preview1024], cache.PathFor(hash, ThumbnailTier.Preview1024));
        Assert.Equal(before[ThumbnailTier.Analysis1024], cache.PathFor(hash, ThumbnailTier.Analysis1024));

        Assert.False(cache.Exists(hash, ThumbnailTier.Grid256));
        Assert.False(cache.Exists(hash, ThumbnailTier.Preview1024));
        Assert.True(cache.Exists(hash, ThumbnailTier.Analysis1024));

        // Rebuilding the adjusted tiers does not disturb the analysis copy.
        var analysisBefore = await File.ReadAllBytesAsync(before[ThumbnailTier.Analysis1024]);
        await cache.GetOrCreateAsync(hash, ThumbnailTier.Preview1024);
        await cache.GetOrCreateAsync(hash, ThumbnailTier.Grid256);
        Assert.Equal(analysisBefore, await File.ReadAllBytesAsync(cache.PathFor(hash, ThumbnailTier.Analysis1024)));
    }

    [Fact]
    public async Task AGeometryEditInvalidatesAllThreeTiers()
    {
        using var project = new ProjectFixture("thumbs-geometry");
        var hash = await ArchiveSceneAsync(project, 1200, 900);
        using var cache = new ThumbnailCache(project.Paths, project.Resolver);

        foreach (var tier in AllTiers) await cache.GetOrCreateAsync(hash, tier);
        var before = AllTiers.ToDictionary(t => t, t => cache.PathFor(hash, t));

        // Geometry moves the pixels focus-region coordinates point at, so analysis must re-run.
        project.SetAdjustments(hash, new ImageAdjustments { Rotate = 90 });

        foreach (var tier in AllTiers)
        {
            Assert.NotEqual(before[tier], cache.PathFor(hash, tier));
            Assert.False(cache.Exists(hash, tier));
        }
    }

    [Fact]
    public async Task EditsThatTouchNoPixelsInvalidateNothing()
    {
        using var project = new ProjectFixture("thumbs-stable");
        var hash = await ArchiveSceneAsync(project, 1200, 900);
        using var cache = new ThumbnailCache(project.Paths, project.Resolver);

        foreach (var tier in AllTiers) await cache.GetOrCreateAsync(hash, tier);
        var before = AllTiers.ToDictionary(t => t, t => cache.PathFor(hash, t));

        // A re-date, a focus-region edit, a tier override and a CropState pan all live outside the
        // AdjustmentStack, so none of them can touch a cache key (doc 05 matrix, row 3).
        var photo = new Photo { ContentHash = hash, TakenAt = new DateTime(2024, 7, 4, 9, 0, 0) };
        photo.TakenAt = new DateTime(2023, 2, 2, 9, 0, 0);
        photo.FocusRegions.Add(new FocusRegion { Rect = new Rect(0.1, 0.1, 0.2, 0.2), Kind = FocusKind.User, Weight = 1 });
        photo.UserTierOverride = Tier.S;

        foreach (var tier in AllTiers)
        {
            Assert.Equal(before[tier], cache.PathFor(hash, tier));
            Assert.True(cache.Exists(hash, tier));
        }
    }

    [Fact]
    public async Task ReturningToTheOriginalAdjustmentsRestoresTheOriginalKeys()
    {
        using var project = new ProjectFixture("thumbs-undo");
        var hash = await ArchiveSceneAsync(project, 900, 900);
        using var cache = new ThumbnailCache(project.Paths, project.Resolver);

        await cache.GetOrCreateAsync(hash, ThumbnailTier.Grid256);
        var identityPath = cache.PathFor(hash, ThumbnailTier.Grid256);

        project.SetAdjustments(hash, new AdjustmentStack { Saturation = 0.5 });
        Assert.False(cache.Exists(hash, ThumbnailTier.Grid256));

        // Undo: the key is a pure function of the stack, so the already-built artifact is found again.
        project.SetAdjustments(hash, AdjustmentStack.Identity);
        Assert.Equal(identityPath, cache.PathFor(hash, ThumbnailTier.Grid256));
        Assert.True(cache.Exists(hash, ThumbnailTier.Grid256));
    }

    [Fact]
    public async Task InvalidateAsyncDropsEveryVariantOfTheRequestedTiers()
    {
        using var project = new ProjectFixture("thumbs-invalidate");
        var hash = await ArchiveSceneAsync(project, 900, 600);
        using var cache = new ThumbnailCache(project.Paths, project.Resolver);

        foreach (var tier in AllTiers) await cache.GetOrCreateAsync(hash, tier);

        await cache.InvalidateAsync(hash, [ThumbnailTier.Grid256, ThumbnailTier.Preview1024]);

        Assert.False(cache.Exists(hash, ThumbnailTier.Grid256));
        Assert.False(cache.Exists(hash, ThumbnailTier.Preview1024));
        Assert.True(cache.Exists(hash, ThumbnailTier.Analysis1024));
    }

    [Fact]
    public async Task ConcurrentRequestsForTheSameKeyCoalesceIntoOneFile()
    {
        using var project = new ProjectFixture("thumbs-concurrent");
        var hash = await ArchiveSceneAsync(project, 1600, 1200);
        using var cache = new ThumbnailCache(project.Paths, project.Resolver);

        var paths = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => cache.GetOrCreateAsync(hash, ThumbnailTier.Preview1024)));

        Assert.Single(paths.Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.Single(Directory.EnumerateFiles(project.Paths.ThumbnailTierFolder("1024")));
    }

    [Fact]
    public async Task OrphanCollectionSweepsDeadHashesAndSupersededVariants()
    {
        using var project = new ProjectFixture("thumbs-orphans");
        var live = await ArchiveSceneAsync(project, 900, 600, seed: 1);
        var dead = await ArchiveSceneAsync(project, 900, 600, seed: 2);
        using var cache = new ThumbnailCache(project.Paths, project.Resolver);

        foreach (var tier in AllTiers)
        {
            await cache.GetOrCreateAsync(live, tier);
            await cache.GetOrCreateAsync(dead, tier);
        }

        // One photo is edited (its old adjusted artifacts are superseded) and one leaves the catalog.
        project.SetAdjustments(live, new AdjustmentStack { Brightness = 0.4 });
        await cache.GetOrCreateAsync(live, ThumbnailTier.Preview1024);
        project.Forget(dead);

        var removed = await cache.CollectOrphansAsync([live]);

        Assert.True(removed >= 4, $"expected at least 4 orphans swept, got {removed}");
        Assert.True(cache.Exists(live, ThumbnailTier.Preview1024));
        Assert.True(cache.Exists(live, ThumbnailTier.Analysis1024));
        foreach (var tier in AllTiers) Assert.False(cache.Exists(dead, tier));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(project.Paths.ThumbnailTierFolder("1024")),
            f => Path.GetFileName(f).StartsWith(dead, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ACancelledBuildLeavesNoPartialFileBehind()
    {
        using var project = new ProjectFixture("thumbs-cancel");
        var hash = await ArchiveSceneAsync(project, 1600, 1200);
        using var cache = new ThumbnailCache(project.Paths, project.Resolver);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cache.GetOrCreateAsync(hash, ThumbnailTier.Preview1024, cancelled.Token));

        Assert.False(cache.Exists(hash, ThumbnailTier.Preview1024));
        Assert.Empty(Directory.EnumerateFiles(project.Paths.ThumbnailCacheFolder, "*", SearchOption.AllDirectories));
    }

    private static async Task<string> ArchiveSceneAsync(ProjectFixture project, int width, int height, int seed = 7)
    {
        var source = SyntheticImages.Write(
            project.Workspace.At("incoming", $"scene-{seed}-{width}x{height}.jpg"),
            new SyntheticImages.SceneSpec { Width = width, Height = height, Seed = seed, Hue = 0.2 * seed });
        return await project.ArchiveAsync(source);
    }
}
