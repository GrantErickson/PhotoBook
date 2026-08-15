using PhotoBook.Analysis.Onnx;
using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;
using PhotoBook.Tests.Fixtures;

namespace PhotoBook.Tests;

/// <summary>
/// The bundled models (models/README.md) ship in the repository and are copied next to the binary
/// by Directory.Build.targets. These tests guard the two things that actually break: the build
/// forgetting to copy them, and a model whose tensor shapes stop matching
/// <see cref="OnnxAnalyzerOptions"/> — YuNet's input is fixed at 640×640 and U²-Netp's at 320×320,
/// so a mismatch fails at inference rather than at load.
/// </summary>
public sealed class BundledOnnxModelTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private static string ModelsDirectory => Path.Combine(AppContext.BaseDirectory, OnnxModelSet.FolderName);

    [Fact]
    public void TheBuildCopiesTheBundledModelsNextToTheBinary()
    {
        Assert.True(Directory.Exists(ModelsDirectory), $"no models folder at {ModelsDirectory}");

        foreach (var expected in new[] { OnnxModelSet.FaceModelFileName, OnnxModelSet.SaliencyModelFileName })
        {
            var path = Path.Combine(ModelsDirectory, expected);
            Assert.True(File.Exists(path), $"{expected} was not copied to the output");
            Assert.True(new FileInfo(path).Length > 100_000, $"{expected} looks truncated");
        }
    }

    [Fact]
    public void AnalysisIsAvailableAndReportsTheUnbundledStageHonestly()
    {
        var availability = OnnxAnalyzer.Probe(ModelsDirectory);

        Assert.True(availability.IsAvailable, availability.Reason);

        // NIMA is deliberately not redistributed; the message must say so rather than imply a
        // complete install (models/README.md).
        Assert.Contains(OnnxModelSet.AestheticModelFileName, availability.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BothBundledModelsRunOnARealImageWithoutShapeErrors()
    {
        // A synthetic frame carries no detectable face, so this asserts what it can: that inference
        // runs end to end and saliency finds the planted subject. Face-detection accuracy is
        // verified against a real photograph outside the test suite (models/README.md).
        var image = SyntheticImages.WriteSharp(_workspace.At("images", "subject.jpg"));

        var failures = new List<string>();
        var options = new OnnxAnalyzerOptions
        {
            ModelsDirectory = ModelsDirectory,
            Log = message => failures.Add(message),
        };

        Assert.True(OnnxAnalyzer.TryCreate(options, out var analyzer, out var reason), reason);
        using (analyzer)
        {
            var input = new AnalysisInput("bundled-test", image, 1200, 900, []);
            var result = await analyzer!.AnalyzeAsync(input, CancellationToken.None);

            Assert.DoesNotContain(failures, f => f.Contains("failed", StringComparison.OrdinalIgnoreCase));
            Assert.NotEmpty(result.Regions);
            Assert.Contains(result.Regions, r => r.Kind == FocusKind.Saliency);

            var subject = SyntheticImages.OffCenterSubject;
            Assert.Contains(result.Regions, r => r.Rect.Intersects(subject));
        }
    }
}
