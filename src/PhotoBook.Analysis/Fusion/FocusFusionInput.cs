using PhotoBook.Core.Model;

namespace PhotoBook.Analysis.Fusion;

/// <summary>
/// Everything <see cref="FocusFusion"/> needs for one photo. Fusion is pure: it reads this record and
/// returns a value, so it is trivially testable and identical for every analyzer (doc 06).
/// </summary>
/// <param name="Derived">
/// The analyzer's proposals — <see cref="FocusKind.Face"/> and <see cref="FocusKind.Saliency"/>
/// regions. Regenerable, and the only part that lives in <c>cache/</c>.
/// </param>
/// <param name="PersonTags">OneDrive person tags; those with a rect become person regions.</param>
/// <param name="UserRegions">
/// Regions the user drew in the Photos tab (<see cref="FocusKind.User"/>), read from
/// <c>photos.json</c>. User intent, never recomputed.
/// </param>
/// <param name="Suppressions">
/// Rects of derived regions the user deleted. Re-analysis would otherwise resurrect them, so the
/// deletion is carried as a suppression (doc 06 step 4).
/// </param>
public sealed record FocusFusionInput(
    IReadOnlyList<FocusRegion> Derived,
    IReadOnlyList<PersonTag> PersonTags,
    IReadOnlyList<FocusRegion> UserRegions,
    IReadOnlyList<Rect> Suppressions)
{
    /// <summary>Fusion input carrying only an analyzer's proposals.</summary>
    public static FocusFusionInput FromDerived(IReadOnlyList<FocusRegion> derived) => new(derived, [], [], []);
}
