using PhotoBook.Analysis.Classical;
using PhotoBook.Analysis.Pixels;

namespace PhotoBook.Analysis.Composition;

/// <summary>Host-supplied configuration for <see cref="AnalyzerFactory"/>.</summary>
public sealed class AnalyzerFactoryOptions
{
    /// <summary>The shared default instance.</summary>
    public static AnalyzerFactoryOptions Default { get; } = new();

    /// <summary>Folder holding the ONNX model files; the default <c>models/</c> folder when null.</summary>
    public string? ModelsDirectory { get; init; }

    /// <summary>Pixel source shared by every analyzer; the Magick.NET loader when null.</summary>
    public IAnalysisImageLoader? ImageLoader { get; init; }

    /// <summary>Tuning of the model-free pipeline.</summary>
    public ClassicalAnalyzerOptions Classical { get; init; } = ClassicalAnalyzerOptions.Default;

    /// <summary>ONNX Runtime intra-op threads per session (doc 02 caps this at 2).</summary>
    public int IntraOpThreads { get; init; } = 2;

    /// <summary>Optional diagnostics sink for selection and degradation messages.</summary>
    public Action<string>? Log { get; init; }
}
