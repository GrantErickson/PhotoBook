using PhotoBook.Core.Model;

namespace PhotoBook.Analysis.Fusion;

/// <summary>What one month's tier assignment did — for the status bar, the import report and tests.</summary>
/// <param name="Ranked">Photos that took part in the ranking pool.</param>
/// <param name="Skipped">Photos left alone because they are excluded (R17) or not analyzed yet.</param>
/// <param name="Overridden">
/// Ranked photos that carry a <see cref="Photo.UserTierOverride"/>. They keep their computed tier for
/// display ("B, promoted to S") while the override remains what layout actually uses.
/// </param>
/// <param name="Counts">How many photos landed in each tier.</param>
public sealed record TierAssignmentResult(
    int Ranked,
    int Skipped,
    int Overridden,
    IReadOnlyDictionary<Tier, int> Counts)
{
    /// <summary>Nothing to rank.</summary>
    public static TierAssignmentResult Empty { get; } = new(0, 0, 0,
        new Dictionary<Tier, int> { [Tier.S] = 0, [Tier.A] = 0, [Tier.B] = 0, [Tier.C] = 0 });

    /// <summary>How many photos landed in one tier.</summary>
    public int CountOf(Tier tier) => Counts.TryGetValue(tier, out var value) ? value : 0;
}
