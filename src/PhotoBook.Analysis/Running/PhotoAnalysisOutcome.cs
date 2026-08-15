using PhotoBook.Analysis.Fusion;
using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;

namespace PhotoBook.Analysis.Running;

/// <summary>What happened to one photo in an analysis run.</summary>
public enum AnalysisOutcomeStatus
{
    /// <summary>The analyzer ran and produced a result.</summary>
    Analyzed,

    /// <summary>A valid cache entry was reused; no model ran.</summary>
    FromCache,

    /// <summary>Deliberately not analyzed — excluded (R17), or previously flagged as undecodable.</summary>
    Skipped,

    /// <summary>Analysis threw. The photo keeps whatever regions and tier it already had (doc 02).</summary>
    Failed,
}

/// <summary>
/// The per-photo result of an analysis run, kept separately from the photo itself so a host that
/// insists on the single-writer rule (doc 02 §"One writer") can apply the outcomes on the UI thread
/// with <see cref="AnalysisApplier"/> instead of letting the runner mutate the model.
/// </summary>
/// <param name="PhotoId">The photo's id.</param>
/// <param name="Status">What happened.</param>
/// <param name="Focus">The fused regions and the primary region, or null when nothing was produced.</param>
/// <param name="Quality">The fused quality score with a percentile of 0 — tiering comes later.</param>
/// <param name="Raw">The analyzer's raw output, for the cache and the inspector.</param>
/// <param name="Error">The failure message when <see cref="Status"/> is <see cref="AnalysisOutcomeStatus.Failed"/>.</param>
public sealed record PhotoAnalysisOutcome(
    string PhotoId,
    AnalysisOutcomeStatus Status,
    FocusFusionResult? Focus,
    QualityScore? Quality,
    AnalysisResult? Raw,
    string? Error)
{
    /// <summary>True when the outcome carries results worth writing to the photo.</summary>
    public bool HasResults => Focus is not null && Quality is not null;
}
