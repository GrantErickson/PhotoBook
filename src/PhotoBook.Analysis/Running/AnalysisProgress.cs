namespace PhotoBook.Analysis.Running;

/// <summary>
/// One progress tick from <see cref="AnalysisRunner"/>. The Analyze lane reports
/// <c>(done, total)</c> to the status bar during first import, and the Photos grid clears a photo's
/// "analyzing…" shimmer when its id shows up here (doc 02, doc 06).
/// </summary>
/// <param name="Completed">Photos finished so far, including cache hits, skips and failures.</param>
/// <param name="Total">Photos in this run.</param>
/// <param name="Analyzed">Photos actually put through the analyzer.</param>
/// <param name="FromCache">Photos served from <c>cache/analysis/</c>.</param>
/// <param name="Skipped">Photos left alone (excluded, or with no decodable original).</param>
/// <param name="Failed">Photos whose analysis threw; they keep whatever they had.</param>
/// <param name="PhotoId">The photo this tick is about.</param>
public sealed record AnalysisProgress(
    int Completed,
    int Total,
    int Analyzed,
    int FromCache,
    int Skipped,
    int Failed,
    string? PhotoId)
{
    /// <summary>Fraction complete, <c>0..1</c>.</summary>
    public double Fraction => Total <= 0 ? 1 : (double)Completed / Total;
}
