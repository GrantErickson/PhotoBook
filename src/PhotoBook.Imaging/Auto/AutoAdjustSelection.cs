using PhotoBook.Core.Model;

namespace PhotoBook.Imaging.Auto;

/// <summary>What an auto-adjust run would do, counted before anything is touched.</summary>
/// <param name="Untouched">Photos nobody has adjusted — always in scope.</param>
/// <param name="Automatic">Photos already on auto, which a run re-derives from the current rules.</param>
/// <param name="Manual">Photos edited by hand, skipped unless the user opts them in.</param>
/// <param name="UpToDate">
/// Automatic photos already carrying the current rules and a measurement of the current bytes. They
/// re-derive to exactly what they already have, so a run leaves them — and their thumbnails — alone.
/// </param>
public readonly record struct AutoAdjustPlan(int Untouched, int Automatic, int Manual, int UpToDate)
{
    /// <summary>How many photos a run would consider, for a given opt-in choice.</summary>
    /// <param name="includeManual">Whether hand-edited photos are being forced back onto auto.</param>
    public int Scope(bool includeManual) => Untouched + Automatic + (includeManual ? Manual : 0);

    /// <summary>How many of those still need their pixels read.</summary>
    /// <param name="includeManual">Whether hand-edited photos are being forced back onto auto.</param>
    public int Work(bool includeManual) => Math.Max(0, Scope(includeManual) - UpToDate);
}

/// <summary>
/// Which photos an auto-adjust run may touch, and which it must leave alone.
///
/// <para>
/// The whole contract of the feature lives here, and it is three rules:
/// </para>
/// <list type="number">
/// <item><description>A photo nobody has adjusted is fair game.</description></item>
/// <item><description>A photo that auto-adjust wrote is re-derived, so changing the look settings and
/// clicking again brings it up to date.</description></item>
/// <item><description>A photo a human edited is never touched — unless the human asks for this one
/// photo specifically, which puts it back on auto.</description></item>
/// </list>
///
/// <para>
/// Pure and free of I/O so it can be tested directly rather than through the job queue: this is the
/// part that must not be wrong, because getting it wrong means silently overwriting someone's work.
/// </para>
/// </summary>
public static class AutoAdjustSelection
{
    /// <summary>Whether a batch run would consider this photo.</summary>
    /// <param name="photo">The photo to test.</param>
    /// <param name="includeManual">Whether hand-edited photos are being forced back onto auto.</param>
    public static bool IsInScope(Photo photo, bool includeManual)
    {
        ArgumentNullException.ThrowIfNull(photo);
        if (photo.Excluded || photo.DecodeFailed) return false;
        return photo.AdjustmentOrigin != AdjustmentOrigin.Manual || includeManual;
    }

    /// <summary>
    /// Whether this photo already carries exactly what the current rules would give it, so a run can
    /// skip even measuring it.
    /// </summary>
    /// <param name="photo">The photo to test.</param>
    /// <param name="rulesVersion">The current <see cref="AutoAdjustRules.VersionFor"/>.</param>
    public static bool IsUpToDate(Photo photo, string rulesVersion)
    {
        ArgumentNullException.ThrowIfNull(photo);
        return photo.AdjustmentOrigin == AdjustmentOrigin.Automatic &&
               photo.AutoAdjust is { } stamp &&
               string.Equals(stamp.RulesVersion, rulesVersion, StringComparison.Ordinal) &&
               stamp.MeasurementAppliesTo(photo);
    }

    /// <summary>
    /// Whether the photo's stored measurement can be reused instead of decoding the file again. True
    /// whenever the bytes have not changed since it was measured — the rules may have.
    /// </summary>
    /// <param name="photo">The photo to test.</param>
    public static AutoAdjustMeasurement? ReusableMeasurement(Photo photo)
    {
        ArgumentNullException.ThrowIfNull(photo);
        return photo.AutoAdjust is { } stamp && stamp.MeasurementAppliesTo(photo) ? stamp.Measurement : null;
    }

    /// <summary>
    /// Whether committing this result would change anything. False means the photo already has these
    /// exact parameters <em>and</em> this exact provenance, and writing would re-key its thumbnails to
    /// produce identical pixels.
    /// </summary>
    /// <param name="photo">The photo about to be written.</param>
    /// <param name="next">The stack the rules chose.</param>
    /// <param name="stamp">The provenance about to be recorded.</param>
    public static bool NeedsWrite(Photo photo, AdjustmentStack next, AutoAdjustStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(stamp);

        if (next != photo.Adjustments) return true;

        return photo.AutoAdjust is not { } current ||
               !string.Equals(current.RulesVersion, stamp.RulesVersion, StringComparison.Ordinal) ||
               !string.Equals(current.SourceHash, stamp.SourceHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Counts what a run would do, so the user can be told before it starts.</summary>
    /// <param name="photos">Every photo in the book.</param>
    /// <param name="look">The book's look profile.</param>
    public static AutoAdjustPlan Describe(IEnumerable<Photo> photos, LookProfile look)
    {
        ArgumentNullException.ThrowIfNull(photos);
        ArgumentNullException.ThrowIfNull(look);

        var version = AutoAdjustRules.VersionFor(look);
        int untouched = 0, automatic = 0, manual = 0, upToDate = 0;

        foreach (var photo in photos)
        {
            if (photo.Excluded || photo.DecodeFailed) continue;

            switch (photo.AdjustmentOrigin)
            {
                case AdjustmentOrigin.Untouched:
                    untouched++;
                    break;

                case AdjustmentOrigin.Automatic:
                    automatic++;
                    if (IsUpToDate(photo, version)) upToDate++;
                    break;

                default:
                    manual++;
                    break;
            }
        }

        return new AutoAdjustPlan(untouched, automatic, manual, upToDate);
    }
}
