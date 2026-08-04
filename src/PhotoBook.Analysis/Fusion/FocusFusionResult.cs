using PhotoBook.Core.Model;

namespace PhotoBook.Analysis.Fusion;

/// <summary>
/// The fused region set stored on <see cref="Photo.FocusRegions"/>, plus the primary region
/// smart-crop anchors on (kernel §4). Analysis produces regions; it never produces crops — the
/// layout engine turns the primary into a <see cref="CropState"/>.
/// </summary>
/// <param name="Regions">All surviving regions, ordered by priority then weight.</param>
/// <param name="Primary">The highest-weight region of the highest non-empty priority kind, or null when there is none.</param>
public sealed record FocusFusionResult(IReadOnlyList<FocusRegion> Regions, FocusRegion? Primary)
{
    /// <summary>An empty result — no regions were proposed and the user drew none.</summary>
    public static FocusFusionResult Empty { get; } = new([], null);

    /// <summary>The number of regions of a given kind.</summary>
    public int CountOf(FocusKind kind) => Regions.Count(r => r.Kind == kind);
}
