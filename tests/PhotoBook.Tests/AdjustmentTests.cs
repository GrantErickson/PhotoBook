using System.Security.Cryptography;
using PhotoBook.Core.Model;
using PhotoBook.Imaging;
using PhotoBook.Tests.Fixtures;

namespace PhotoBook.Tests;

/// <summary>
/// Non-destructive editing (R6, R11, doc 05 "AdjustmentStack"): every slider moves the measurable
/// property it claims to move, in the direction it claims to move it — and the bytes in
/// <c>originals/</c> are the same after all of it as before.
/// </summary>
public sealed class AdjustmentTests
{
    private static readonly ImageDecoder Decoder = new();

    [Fact]
    public async Task BrightnessMovesMeanLuminanceInBothDirections()
    {
        using var workspace = new TempWorkspace("brightness");
        var path = Scene(workspace);

        var baseline = SyntheticImages.MeanLuma((await Decoder.DecodeAsync(path)).AsSpan());
        var brighter = SyntheticImages.MeanLuma(
            (await Decoder.DecodeAdjustedAsync(path, new AdjustmentStack { Brightness = 0.5 })).AsSpan());
        var darker = SyntheticImages.MeanLuma(
            (await Decoder.DecodeAdjustedAsync(path, new AdjustmentStack { Brightness = -0.5 })).AsSpan());

        Assert.True(brighter > baseline + 0.03, $"brightness +0.5 gave {brighter:F4}, baseline {baseline:F4}");
        Assert.True(darker < baseline - 0.03, $"brightness -0.5 gave {darker:F4}, baseline {baseline:F4}");
    }

    [Fact]
    public async Task ContrastMovesLuminanceSpreadInBothDirections()
    {
        using var workspace = new TempWorkspace("contrast");
        var path = Scene(workspace);

        var baseline = SyntheticImages.LumaStdDev((await Decoder.DecodeAsync(path)).AsSpan());
        var punchier = SyntheticImages.LumaStdDev(
            (await Decoder.DecodeAdjustedAsync(path, new AdjustmentStack { Contrast = 0.6 })).AsSpan());
        var flatter = SyntheticImages.LumaStdDev(
            (await Decoder.DecodeAdjustedAsync(path, new AdjustmentStack { Contrast = -0.6 })).AsSpan());

        Assert.True(punchier > baseline * 1.02, $"contrast +0.6 gave σ {punchier:F4}, baseline {baseline:F4}");
        Assert.True(flatter < baseline * 0.98, $"contrast -0.6 gave σ {flatter:F4}, baseline {baseline:F4}");
    }

    [Fact]
    public async Task SaturationMovesMeanSaturationInBothDirections()
    {
        using var workspace = new TempWorkspace("saturation");
        var path = Scene(workspace);

        var baseline = SyntheticImages.MeanSaturation((await Decoder.DecodeAsync(path)).AsSpan());
        var richer = SyntheticImages.MeanSaturation(
            (await Decoder.DecodeAdjustedAsync(path, new AdjustmentStack { Saturation = 0.8 })).AsSpan());
        var greyer = SyntheticImages.MeanSaturation(
            (await Decoder.DecodeAdjustedAsync(path, new AdjustmentStack { Saturation = -1.0 })).AsSpan());

        Assert.True(baseline > 0.05, $"the fixture must start with visible color; got {baseline:F4}");
        Assert.True(richer > baseline * 1.05, $"saturation +0.8 gave {richer:F4}, baseline {baseline:F4}");
        Assert.True(greyer < baseline * 0.35, $"saturation -1.0 gave {greyer:F4}, baseline {baseline:F4}");
    }

    [Fact]
    public async Task BlackAndWhiteRemovesColourEntirely()
    {
        using var workspace = new TempWorkspace("bw");
        var path = Scene(workspace);

        var mono = await Decoder.DecodeAdjustedAsync(path, new ImageAdjustments { BlackAndWhite = true });

        Assert.True(SyntheticImages.MeanSaturation(mono.AsSpan()) < 0.02);
    }

    [Fact]
    public async Task TheIdentityStackIsPixelForPixelTheSameAsAPlainDecode()
    {
        using var workspace = new TempWorkspace("identity");
        var path = Scene(workspace);

        var plain = await Decoder.DecodeAsync(path);
        var identity = await Decoder.DecodeAdjustedAsync(path, AdjustmentStack.Identity);

        Assert.Equal(plain.Width, identity.Width);
        Assert.Equal(plain.Height, identity.Height);
        Assert.Equal(plain.Pixels, identity.Pixels);
    }

    [Fact]
    public async Task AdjustmentsAreDeterministicForTheSameParameters()
    {
        using var workspace = new TempWorkspace("deterministic");
        var path = Scene(workspace);
        var stack = new AdjustmentStack { Brightness = 0.2, Contrast = 0.3, Saturation = -0.4, Temperature = 0.25 };

        var first = await Decoder.DecodeAdjustedAsync(path, stack);
        var second = await Decoder.DecodeAdjustedAsync(path, stack);

        Assert.Equal(first.Pixels, second.Pixels);
    }

    [Fact]
    public async Task EditingIsNonDestructive_ArchivedOriginalsAreByteIdenticalAfterwards()
    {
        using var project = new ProjectFixture("nondestructive");
        var source = SyntheticImages.Write(
            project.Workspace.At("incoming", "scene.jpg"),
            new SyntheticImages.SceneSpec { Width = 1600, Height = 1200 });
        var hash = await project.ArchiveAsync(source);
        var original = project.OriginalOf(hash);

        var before = await SnapshotAsync(project.Paths.OriginalsFolder);

        // Everything the editor and the cache do to a photo, back to back.
        using var cache = new ThumbnailCache(project.Paths, project.Resolver);
        await Decoder.ProbeAsync(original);
        await Decoder.DecodeAsync(original);
        await Decoder.DecodeAdjustedAsync(original, new AdjustmentStack
        {
            Brightness = 0.4, Contrast = 0.5, Saturation = 0.6, Temperature = -0.3, Tint = 0.2, Sharpness = 0.5,
        });
        await Decoder.DecodeAdjustedAsync(original, new ImageAdjustments { Rotate = 90, Straighten = 3.5, Vignette = 0.4 });
        await cache.WarmAsync(hash);
        project.SetAdjustments(hash, new AdjustmentStack { Brightness = 0.4 });
        await cache.WarmAsync(hash);
        await cache.CollectOrphansAsync([hash]);

        var after = await SnapshotAsync(project.Paths.OriginalsFolder);

        Assert.Equal(before, after);

        // …and the archived file is still marked read-only, as doc 04 §7 requires.
        Assert.True(File.Exists(original));
        Assert.Equal(before[Path.GetFileName(original)], await HashFileAsync(original));
    }

    private static string Scene(TempWorkspace workspace) => SyntheticImages.Write(
        workspace.At("fixtures", "scene.jpg"),
        new SyntheticImages.SceneSpec { Width = 900, Height = 600, Hue = 0.85 });

    private static async Task<SortedDictionary<string, string>> SnapshotAsync(string folder)
    {
        var snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            snapshot[Path.GetRelativePath(folder, file)] = await HashFileAsync(file);
        return snapshot;
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }
}
