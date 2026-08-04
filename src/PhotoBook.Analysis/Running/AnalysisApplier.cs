using PhotoBook.Analysis.Fusion;
using PhotoBook.Core.Model;

namespace PhotoBook.Analysis.Running;

/// <summary>
/// Writes an analysis outcome onto a <see cref="Photo"/> — the one place that decides what analysis
/// is allowed to touch on the model.
/// <para>
/// It writes <see cref="Photo.FocusRegions"/> (the fused set, which already contains the user's own
/// regions untouched) and <see cref="Photo.Quality"/>. It never writes
/// <see cref="Photo.UserTierOverride"/>, never clears user regions, and never sets
/// <see cref="Photo.Tier"/> — the tier is month-relative and belongs to <see cref="TierAssigner"/>,
/// which runs once the whole month's pool is known.
/// </para>
/// </summary>
public static class AnalysisApplier
{
    /// <summary>Applies one outcome to one photo. Returns false when there was nothing to apply.</summary>
    /// <param name="photo">The catalog record to update.</param>
    /// <param name="outcome">The outcome for that photo.</param>
    public static bool Apply(Photo photo, PhotoAnalysisOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(outcome);
        if (!outcome.HasResults) return false;

        photo.FocusRegions = outcome.Focus!.Regions.ToList();
        photo.Quality = outcome.Quality;
        return true;
    }

    /// <summary>Applies a run's outcomes to a catalog, matching photos by id.</summary>
    /// <param name="photos">The photos to update.</param>
    /// <param name="outcomes">The outcomes to apply.</param>
    /// <returns>How many photos were updated.</returns>
    public static int ApplyAll(IEnumerable<Photo> photos, IEnumerable<PhotoAnalysisOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(photos);
        ArgumentNullException.ThrowIfNull(outcomes);

        var byId = outcomes.Where(o => o.HasResults).ToDictionary(o => o.PhotoId, StringComparer.Ordinal);
        var applied = 0;
        foreach (var photo in photos)
        {
            if (!byId.TryGetValue(photo.Id, out var outcome)) continue;
            if (Apply(photo, outcome)) applied++;
        }

        return applied;
    }
}
