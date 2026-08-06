using PhotoBook.Core.Model;
using PhotoBook.Imaging;
using PhotoBook.Imaging.Auto;
using PhotoBook.Tests.Fixtures;

namespace PhotoBook.Tests;

/// <summary>
/// Auto-adjust over real files: decode a real image, measure it, choose parameters, and push them
/// through the real <see cref="AdjustmentPipeline"/>. The unit tests assert the rules given a
/// measurement; these assert that the measurement and the pipeline agree with each other, which no
/// amount of hand-written measurement fixtures can.
/// </summary>
public sealed class AutoAdjustEndToEndTests
{
    private static async Task<AutoAdjustMeasurement> MeasureAsync(string path) =>
        await new PhotoMeasurer().MeasureAsync(path).ConfigureAwait(false);

    /// <summary>Re-measures what the pipeline produced, so a run can be judged by its output.</summary>
    private static async Task<AutoAdjustMeasurement> MeasureAdjustedAsync(string path, AdjustmentStack stack)
    {
        var pixels = await AdjustmentPipeline.Default
            .ApplyToFileAsync(path, stack, PhotoMeasurer.MeasureLongEdgePx)
            .ConfigureAwait(false);

        return PhotoMeasurer.Measure(pixels);
    }

    [Fact]
    public async Task ADarkPhotoComesOutBrighterAndAnOverexposedOneComesOutDarker()
    {
        using var temp = new TempFolder();

        var dark = SyntheticImages.WriteUnderexposed(Path.Combine(temp.Path, "dark.jpg"));
        var bright = SyntheticImages.WriteOverexposed(Path.Combine(temp.Path, "bright.jpg"));
        var look = new LookProfile();

        var darkBefore = await MeasureAsync(dark);
        var darkAfter = await MeasureAdjustedAsync(dark, AutoAdjustRules.Choose(darkBefore, look));

        var brightBefore = await MeasureAsync(bright);
        var brightAfter = await MeasureAdjustedAsync(bright, AutoAdjustRules.Choose(brightBefore, look));

        Assert.True(
            darkAfter.MedianLuma > darkBefore.MedianLuma,
            $"an underexposed photo came out no brighter: {darkBefore.MedianLuma:F3} → {darkAfter.MedianLuma:F3}");

        Assert.True(
            brightAfter.MedianLuma < brightBefore.MedianLuma,
            $"an overexposed photo came out no darker: {brightBefore.MedianLuma:F3} → {brightAfter.MedianLuma:F3}");

        // And both moved toward a normal exposure rather than merely moving.
        Assert.True(
            Math.Abs(darkAfter.MedianLuma - 0.46) < Math.Abs(darkBefore.MedianLuma - 0.46),
            "the dark photo moved, but not toward a normal exposure");
        Assert.True(
            Math.Abs(brightAfter.MedianLuma - 0.46) < Math.Abs(brightBefore.MedianLuma - 0.46),
            "the bright photo moved, but not toward a normal exposure");
    }

    /// <summary>
    /// The property the whole re-runnable design rests on. Auto-adjust always measures the
    /// <em>original</em>, never its own output, so running it twice must produce the same answer
    /// rather than correcting an already-corrected photo a second time.
    /// </summary>
    [Fact]
    public async Task RunningTwiceGivesExactlyTheSameParameters()
    {
        using var temp = new TempFolder();
        var path = SyntheticImages.WriteUnderexposed(Path.Combine(temp.Path, "dark.jpg"));
        var look = new LookProfile();

        var first = AutoAdjustRules.Choose(await MeasureAsync(path), look);
        var second = AutoAdjustRules.Choose(await MeasureAsync(path), look);

        Assert.Equal(first, second);
        Assert.NotEqual(AdjustmentStack.Identity, first);
    }

    /// <summary>
    /// The other half: were auto-adjust ever to measure its own output, the second pass would push a
    /// corrected photo well past the target. This pins the size of that mistake so the property above
    /// is not passing by coincidence on a photo that needed nothing.
    /// </summary>
    [Fact]
    public async Task MeasuringItsOwnOutputWouldOvershoot()
    {
        using var temp = new TempFolder();

        // Moderately dark on purpose. The WriteUnderexposed fixture is so far down that one pass
        // cannot reach the target and both passes clamp at maximum exposure — identical numbers for
        // a reason that has nothing to do with convergence, which would make this test vacuous.
        var path = SyntheticImages.Write(
            Path.Combine(temp.Path, "dim.jpg"),
            new SyntheticImages.SceneSpec { ExposureScale = 0.45 });

        var look = new LookProfile();

        var once = AutoAdjustRules.Choose(await MeasureAsync(path), look);
        var compounded = AutoAdjustRules.Choose(await MeasureAdjustedAsync(path, once), look);

        Assert.True(
            compounded.ExposureEv < once.ExposureEv,
            $"a corrected photo still asked for as much exposure as the original " +
            $"({compounded.ExposureEv:F2} vs {once.ExposureEv:F2}) — this test cannot detect compounding");
    }

    [Fact]
    public async Task ChangingTheLookChangesTheResultOnRealPixels()
    {
        using var temp = new TempFolder();
        var path = SyntheticImages.Write(Path.Combine(temp.Path, "scene.jpg"));

        var measurement = await MeasureAsync(path);

        var neutral = AutoAdjustRules.Choose(measurement, new LookProfile());
        var brighter = AutoAdjustRules.Choose(measurement, new LookProfile { Brightness = 0.8 });

        var neutralResult = await MeasureAdjustedAsync(path, neutral);
        var brighterResult = await MeasureAdjustedAsync(path, brighter);

        Assert.True(
            brighterResult.MedianLuma > neutralResult.MedianLuma + 0.02,
            $"asking the look profile for a brighter book changed nothing on real pixels: " +
            $"{neutralResult.MedianLuma:F3} vs {brighterResult.MedianLuma:F3}");
    }

    [Fact]
    public async Task AnAlreadyGoodPhotoIsBarelyTouched()
    {
        using var temp = new TempFolder();
        var path = SyntheticImages.Write(Path.Combine(temp.Path, "scene.jpg"));

        var before = await MeasureAsync(path);
        var stack = AutoAdjustRules.Choose(before, new LookProfile());
        var after = await MeasureAdjustedAsync(path, stack);

        // R27: "edits should really be tweaks". A well-exposed frame must not be dragged around.
        Assert.True(
            Math.Abs(after.MedianLuma - before.MedianLuma) < 0.20,
            $"a reasonable photo was moved from {before.MedianLuma:F3} to {after.MedianLuma:F3}");
        Assert.True(Math.Abs(stack.ExposureEv) <= 1.2, $"exposure ran to {stack.ExposureEv:F2} EV");
    }

    [Fact]
    public async Task MeasurementIsStableAcrossRunsSoPhotosJsonDoesNotChurn()
    {
        // doc 04 §4 rule 5: two saves of the same model are byte-identical. A measurement that
        // wobbled in the fifth decimal would rewrite every photo row on every run.
        using var temp = new TempFolder();
        var path = SyntheticImages.Write(Path.Combine(temp.Path, "scene.jpg"));

        var first = await MeasureAsync(path);
        var second = await MeasureAsync(path);

        Assert.Equal(first, second);
    }
}
