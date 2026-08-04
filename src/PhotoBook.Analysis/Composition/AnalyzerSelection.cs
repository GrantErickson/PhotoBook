using PhotoBook.Core.Abstractions;

namespace PhotoBook.Analysis.Composition;

/// <summary>
/// What <see cref="AnalyzerFactory"/> picked, and whether it had to settle. The reason is written
/// for a human: it ends up in the log and in the settings page that tells the user which model files
/// to download to get faces and aesthetics.
/// </summary>
/// <param name="Analyzer">The analyzer to use. Dispose it when the book closes.</param>
/// <param name="RequestedId">The id asked for, from <c>book.json</c>'s <c>analysis.analyzerId</c>.</param>
/// <param name="Degraded">True when the selection is not the full requested pipeline.</param>
/// <param name="Reason">Human-readable explanation of the selection.</param>
public sealed record AnalyzerSelection(
    IImageAnalyzer Analyzer,
    string RequestedId,
    bool Degraded,
    string Reason) : IDisposable
{
    /// <summary>The id the results will be cached under (<c>cache/analysis/{hash}.{analyzerId}.json</c>).</summary>
    public string EffectiveId => Analyzer.Id;

    /// <summary>Disposes the analyzer if it holds resources (ONNX sessions).</summary>
    public void Dispose() => (Analyzer as IDisposable)?.Dispose();
}
