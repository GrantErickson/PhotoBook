using PhotoBook.Analysis.Classical;
using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;
using PhotoBook.Tests.Fixtures;

namespace PhotoBook.Tests;

/// <summary>
/// The model-free analyzer measured against images whose ground truth we planted (doc 06): a crisp
/// frame beats a blurred one on sharpness, a well-exposed frame beats a clipped one on exposure, and
/// the saliency stage finds the high-contrast subject that was deliberately put off-center.
/// </summary>
public sealed class ClassicalAnalysisTests
{
    private static readonly ClassicalAnalyzer Analyzer = new();

    [Fact]
    public async Task TheSharpImageScoresAboveTheBlurredOne()
    {
        using var workspace = new TempWorkspace("sharpness");
        var sharp = SyntheticImages.WriteSharp(workspace.At("fixtures", "sharp.png"), Base);
        var blurred = SyntheticImages.WriteBlurred(workspace.At("fixtures", "blurred.png"), Base);

        var sharpResult = await AnalyzeAsync(sharp);
        var blurredResult = await AnalyzeAsync(blurred);

        Assert.True(
            sharpResult.Signals.Sharpness > blurredResult.Signals.Sharpness,
            $"sharp {sharpResult.Signals.Sharpness:F4} should beat blurred {blurredResult.Signals.Sharpness:F4}");

        // And the gap is decisive, not noise — missed focus is the complaint tiering exists to answer.
        Assert.True(sharpResult.Signals.Sharpness > blurredResult.Signals.Sharpness * 1.5);
    }

    [Fact]
    public async Task TheWellExposedImageScoresAboveTheClippedOnes()
    {
        using var workspace = new TempWorkspace("exposure");
        var wellExposed = SyntheticImages.Write(workspace.At("fixtures", "good.png"), Base);
        var blownOut = SyntheticImages.WriteOverexposed(workspace.At("fixtures", "blown.png"), Base);
        var crushed = SyntheticImages.WriteUnderexposed(workspace.At("fixtures", "crushed.png"), Base);

        var good = await AnalyzeAsync(wellExposed);
        var over = await AnalyzeAsync(blownOut);
        var under = await AnalyzeAsync(crushed);

        Assert.True(
            good.Signals.Exposure > over.Signals.Exposure,
            $"well-exposed {good.Signals.Exposure:F4} should beat blown-out {over.Signals.Exposure:F4}");
        Assert.True(
            good.Signals.Exposure > under.Signals.Exposure,
            $"well-exposed {good.Signals.Exposure:F4} should beat crushed {under.Signals.Exposure:F4}");
    }

    [Fact]
    public async Task ClippingIsMeasuredAtBothEndsOfTheHistogram()
    {
        using var workspace = new TempWorkspace("clipping");
        var blownOut = SyntheticImages.WriteOverexposed(workspace.At("fixtures", "blown.png"), Base);
        var crushed = SyntheticImages.WriteUnderexposed(workspace.At("fixtures", "crushed.png"), Base);

        var over = Measure(blownOut);
        var under = Measure(crushed);

        Assert.True(over.ClipHigh > 0.3, $"expected blown highlights, clipHigh was {over.ClipHigh:F4}");
        Assert.True(under.ClipLow > 0.3, $"expected crushed shadows, clipLow was {under.ClipLow:F4}");
        Assert.True(over.MeanLuma > under.MeanLuma);
    }

    [Fact]
    public async Task AFocusRegionOverlapsThePlantedOffCentreSubject()
    {
        using var workspace = new TempWorkspace("saliency");
        var subject = SyntheticImages.OffCenterSubject;
        var path = SyntheticImages.Write(workspace.At("fixtures", "subject.png"), Base with { Subject = subject });

        var result = await AnalyzeAsync(path);

        Assert.NotEmpty(result.Regions);
        Assert.All(result.Regions, r =>
        {
            Assert.True(r.Rect.IsWellFormed, $"{r.Rect} is not well formed");
            Assert.True(r.Rect.IsInsideUnitSquare, $"{r.Rect} escapes the unit square");
            Assert.InRange(r.Weight, 0, 1);
            Assert.Equal(FocusKind.Saliency, r.Kind);
        });

        var overlapping = result.Regions.Where(r => r.Rect.Intersects(subject)).ToList();
        Assert.True(overlapping.Count > 0,
            $"no region overlapped the planted subject {subject}; got [{string.Join(", ", result.Regions.Select(r => r.Rect))}]");

        // The strongest region is the subject, not the frame centre the prior would otherwise pull toward.
        var strongest = result.Regions.MaxBy(r => r.Weight)!;
        Assert.True(strongest.Rect.Intersects(subject),
            $"the strongest region {strongest.Rect} missed the subject {subject}");
        Assert.True(strongest.Rect.CenterX < 0.5,
            $"the subject sits in the left third but the strongest region centred at x={strongest.Rect.CenterX:F3}");
    }

    [Fact]
    public async Task AFeaturelessFrameFallsBackToTheCentreRegionRatherThanReturningNothing()
    {
        using var workspace = new TempWorkspace("featureless");
        var path = SyntheticImages.Write(
            workspace.At("fixtures", "flat.png"),
            new SyntheticImages.SceneSpec { Width = 800, Height = 600, Subject = null, ExposureScale = 1.0 });

        var measurements = Measure(path);

        // Automatic layout depends on a focus region existing, so the pipeline never returns none.
        Assert.NotEmpty(measurements.Regions);
        Assert.All(measurements.Regions, r => Assert.True(r.Rect.IsInsideUnitSquare));
    }

    [Fact]
    public async Task AnalysisIsDeterministicForIdenticalBytes()
    {
        using var workspace = new TempWorkspace("determinism");
        var path = SyntheticImages.Write(workspace.At("fixtures", "scene.png"), Base);

        var first = await AnalyzeAsync(path);
        var second = await AnalyzeAsync(path);

        Assert.Equal(first.Signals, second.Signals);
        Assert.Equal(
            first.Regions.Select(r => (r.Rect, r.Weight, r.Kind)),
            second.Regions.Select(r => (r.Rect, r.Weight, r.Kind)));
    }

    [Fact]
    public async Task FaceSignalsAreHonestlyZeroWithoutModelFiles()
    {
        using var workspace = new TempWorkspace("faces");
        var path = SyntheticImages.Write(workspace.At("fixtures", "scene.png"), Base);

        var result = await AnalyzeAsync(path);

        // Inventing faces would poison faceBonus and therefore the month ranking (doc 06).
        Assert.Equal(0, result.Signals.FaceCount);
        Assert.Equal(0, result.Signals.LargestFaceArea);
        Assert.InRange(result.Signals.Aesthetic, 0, 1);
    }

    private static SyntheticImages.SceneSpec Base { get; } = new()
    {
        Width = 1024,
        Height = 768,
        Subject = SyntheticImages.OffCenterSubject,
    };

    private static Task<AnalysisResult> AnalyzeAsync(string path)
    {
        var input = new AnalysisInput(
            ContentHash: new string('a', 64),
            AnalysisCopyPath: path,
            PixelWidth: 1024,
            PixelHeight: 768,
            PersonTags: []);
        return Analyzer.AnalyzeAsync(input);
    }

    private static ClassicalMeasurements Measure(string path)
    {
        var image = PhotoBook.Analysis.Pixels.MagickAnalysisImageLoader.Load(path, 1024);
        return Analyzer.Measure(image);
    }
}
