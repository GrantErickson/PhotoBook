using PhotoBook.Analysis.Fusion;
using PhotoBook.Core.Model;

namespace PhotoBook.Analysis.Running;

/// <summary>The result of one <see cref="AnalysisRunner"/> pass.</summary>
/// <param name="Outcomes">One entry per photo, in the order the photos were supplied.</param>
/// <param name="Analyzed">How many photos went through the analyzer.</param>
/// <param name="FromCache">How many were served from <c>cache/analysis/</c>.</param>
/// <param name="Skipped">How many were deliberately left alone.</param>
/// <param name="Failed">How many threw; those photos keep whatever they had.</param>
/// <param name="Applied">How many photos the run wrote results onto.</param>
/// <param name="Tiers">Per-month tier assignment results, empty when tiering was not requested.</param>
/// <param name="Elapsed">Wall-clock duration of the run.</param>
public sealed record AnalysisRunResult(
    IReadOnlyList<PhotoAnalysisOutcome> Outcomes,
    int Analyzed,
    int FromCache,
    int Skipped,
    int Failed,
    int Applied,
    IReadOnlyDictionary<(int Year, int Month), TierAssignmentResult> Tiers,
    TimeSpan Elapsed)
{
    /// <summary>An empty run.</summary>
    public static AnalysisRunResult Empty { get; } = new(
        [], 0, 0, 0, 0, 0, new Dictionary<(int, int), TierAssignmentResult>(), TimeSpan.Zero);

    /// <summary>Total photos considered.</summary>
    public int Total => Outcomes.Count;

    /// <summary>The failures, for the Import Report and the per-photo error badge (doc 02).</summary>
    public IEnumerable<PhotoAnalysisOutcome> Failures =>
        Outcomes.Where(o => o.Status == AnalysisOutcomeStatus.Failed);
}
