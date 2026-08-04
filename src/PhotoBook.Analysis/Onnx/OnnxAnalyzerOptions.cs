using PhotoBook.Analysis.Classical;
using PhotoBook.Analysis.Pixels;

namespace PhotoBook.Analysis.Onnx;

/// <summary>Configuration of <see cref="OnnxAnalyzer"/>. Every default comes from doc 06.</summary>
public sealed class OnnxAnalyzerOptions
{
    /// <summary>The shared default instance.</summary>
    public static OnnxAnalyzerOptions Default { get; } = new();

    /// <summary>Folder holding the model files; <see cref="OnnxModelSet.DefaultDirectory"/> when null.</summary>
    public string? ModelsDirectory { get; init; }

    /// <summary>Pixel source; the Magick.NET loader when null.</summary>
    public IAnalysisImageLoader? ImageLoader { get; init; }

    /// <summary>Tuning of the classical stage, which runs alongside the models (doc 06).</summary>
    public ClassicalAnalyzerOptions Classical { get; init; } = ClassicalAnalyzerOptions.Default;

    /// <summary>
    /// ONNX Runtime intra-op threads per session. Doc 02 caps this at 2 so the Analyze lane cannot
    /// starve the UI or the thumbnail lane.
    /// </summary>
    public int IntraOpThreads { get; init; } = 2;

    /// <summary>Long edge the YuNet input is letterboxed to (doc 06).</summary>
    public int FaceInputSize { get; init; } = 640;

    /// <summary>YuNet score threshold (doc 06).</summary>
    public double FaceScoreThreshold { get; init; } = 0.70;

    /// <summary>YuNet NMS IoU threshold (doc 06).</summary>
    public double FaceNmsIoU { get; init; } = 0.30;

    /// <summary>Face box area at which the size half of the face weight saturates (doc 06: <c>min(1, faceArea/0.04)</c>).</summary>
    public double FaceFullWeightArea { get; init; } = 0.04;

    /// <summary>Threshold applied to the U2-Netp mask (doc 06).</summary>
    public double SaliencyThreshold { get; init; } = 0.50;

    /// <summary>Square input size of the saliency model (doc 06: 320).</summary>
    public int SaliencyInputSize { get; init; } = 320;

    /// <summary>Square input size of the aesthetic model (doc 06: 224).</summary>
    public int AestheticInputSize { get; init; } = 224;

    /// <summary>
    /// Optional diagnostics sink — model load failures, unexpected tensor shapes, per-photo
    /// inference errors. Analysis never throws for these; it degrades and reports.
    /// </summary>
    public Action<string>? Log { get; init; }
}
