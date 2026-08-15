using PhotoBook.Core.Model;

namespace PhotoBook.Analysis.Running;

/// <summary>Configuration of one <see cref="AnalysisRunner"/> pass.</summary>
public sealed class AnalysisRunOptions
{
    /// <summary>The shared default instance.</summary>
    public static AnalysisRunOptions Default { get; } = new();

    /// <summary>
    /// Worker count. The default leaves one core for the UI and the thumbnail lane
    /// (<c>Environment.ProcessorCount − 1</c>, at least 1).
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = Math.Max(1, Environment.ProcessorCount - 1);

    /// <summary>Whether results are written onto the <see cref="Photo"/> objects as they complete.</summary>
    /// <remarks>
    /// Set false when the host enforces doc 02's single-writer rule: the run then only returns
    /// outcomes and the UI thread applies them with <see cref="AnalysisApplier"/>.
    /// </remarks>
    public bool ApplyToPhotos { get; init; } = true;

    /// <summary>Whether the run assigns month tiers when it finishes. Requires <see cref="ApplyToPhotos"/>.</summary>
    public bool AssignTiers { get; init; } = true;

    /// <summary>
    /// The pool tiers are computed against. Percentiles are only meaningful against a whole month,
    /// so pass the full catalog when analyzing a subset; null means "just the photos in this run".
    /// </summary>
    public IReadOnlyList<Photo>? TierPool { get; init; }

    /// <summary>Whether excluded photos (R17) are skipped. They are out of the layout and out of the tier pool.</summary>
    public bool SkipExcluded { get; init; } = true;

    /// <summary>Whether photos already flagged <see cref="Photo.DecodeFailed"/> are skipped.</summary>
    public bool SkipDecodeFailed { get; init; } = true;

    /// <summary>Whether a valid <c>cache/analysis/</c> entry is reused instead of re-running the analyzer.</summary>
    public bool UseCache { get; init; } = true;

    /// <summary>Whether fresh analyzer output is written to the cache.</summary>
    public bool WriteCache { get; init; } = true;

    /// <summary>
    /// Focus-region suppressions per photo id — derived regions the user deleted, which re-analysis
    /// must not resurrect (doc 06 step 4). Core has no persisted suppression list yet, so the host
    /// supplies them.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Rect>>? Suppressions { get; init; }

    /// <summary>Optional diagnostics sink.</summary>
    public Action<string>? Log { get; init; }
}
