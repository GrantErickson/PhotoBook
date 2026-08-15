namespace PhotoBook.Core.Model;

/// <summary>
/// Which real-world line the user just traced: the thing they dragged along should end up level, or
/// it should end up plumb.
/// </summary>
public enum StraightenReference
{
    /// <summary>
    /// Decide from the line itself — a mostly-across drag is a horizon, a mostly-down one is a door
    /// frame. This is what the tool uses unless the user pins it, and it is right essentially always
    /// because nobody traces a horizon at 50°.
    /// </summary>
    Auto,

    /// <summary>The traced line should become horizontal (a horizon, a table edge, a roof line).</summary>
    Horizontal,

    /// <summary>The traced line should become vertical (a door frame, a lamp post, a building corner).</summary>
    Vertical,
}

/// <summary>
/// Turns a line the user dragged across a photo into the <see cref="AdjustmentStack.Straighten"/>
/// angle that levels it. Pure geometry, no imaging and no UI, so the tool's one load-bearing
/// calculation is unit-testable on its own.
///
/// <para>
/// <b>Sign convention.</b> Inputs are in the on-screen coordinates the user actually dragged in —
/// x to the right, <b>y downward</b>, matching WPF and every image coordinate system in this app.
/// The result is in degrees on the same scale and sign as <c>Straighten</c>, which the imaging layer
/// feeds to a clockwise-positive rotation. So a horizon that sags to the right (positive dy for
/// positive dx) yields a <em>negative</em> angle: the frame turns anticlockwise to bring the right
/// end back up.
/// </para>
///
/// <para>
/// <b>It is a delta.</b> The angle describes the line as it appears in the picture the user is
/// looking at, which already carries whatever straightening is applied. Callers add it to the
/// current value (see <see cref="Combine"/>) rather than replacing it, so two successive corrections
/// converge the way a spirit level does instead of fighting each other.
/// </para>
/// </summary>
public static class StraightenMath
{
    /// <summary>The documented limit of <see cref="AdjustmentStack.Straighten"/>, in degrees.</summary>
    public const double MaxDegrees = 15.0;

    /// <summary>
    /// Shortest drag, in the same units as the points, that counts as a line. Below this the angle is
    /// dominated by the pointer's last pixel and a stray click would tilt the photo.
    /// </summary>
    public const double MinimumLength = 24.0;

    /// <summary>True when a drag is long enough for its angle to mean anything.</summary>
    /// <param name="x1">Start x.</param>
    /// <param name="y1">Start y.</param>
    /// <param name="x2">End x.</param>
    /// <param name="y2">End y.</param>
    public static bool IsUsableLine(double x1, double y1, double x2, double y2) =>
        Length(x1, y1, x2, y2) >= MinimumLength;

    /// <summary>Length of the dragged line.</summary>
    /// <param name="x1">Start x.</param>
    /// <param name="y1">Start y.</param>
    /// <param name="x2">End x.</param>
    /// <param name="y2">End y.</param>
    public static double Length(double x1, double y1, double x2, double y2) =>
        Math.Sqrt(((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1)));

    /// <summary>
    /// Which reference <see cref="StraightenReference.Auto"/> resolves to for a given line: the axis
    /// the line already lies closer to.
    /// </summary>
    /// <param name="x1">Start x.</param>
    /// <param name="y1">Start y.</param>
    /// <param name="x2">End x.</param>
    /// <param name="y2">End y.</param>
    public static StraightenReference Resolve(double x1, double y1, double x2, double y2) =>
        Math.Abs(x2 - x1) >= Math.Abs(y2 - y1) ? StraightenReference.Horizontal : StraightenReference.Vertical;

    /// <summary>
    /// The rotation, in degrees, that brings the dragged line onto its reference axis — the value to
    /// add to the photo's current <c>Straighten</c>. Clamped to ±<see cref="MaxDegrees"/>; a degenerate
    /// (too short) line returns 0 rather than an arbitrary angle.
    /// </summary>
    /// <param name="x1">Start x, screen coordinates with y downward.</param>
    /// <param name="y1">Start y.</param>
    /// <param name="x2">End x.</param>
    /// <param name="y2">End y.</param>
    /// <param name="reference">Level, plumb, or let the line decide.</param>
    public static double AngleFromLine(
        double x1, double y1, double x2, double y2, StraightenReference reference = StraightenReference.Auto)
    {
        if (!IsUsableLine(x1, y1, x2, y2)) return 0;

        var resolved = reference == StraightenReference.Auto ? Resolve(x1, y1, x2, y2) : reference;

        // Direction is taken without regard to which end the drag started at: a line and its reverse
        // describe the same tilt, so normalize into the half-plane that makes the arithmetic obvious.
        var dx = x2 - x1;
        var dy = y2 - y1;

        double degrees;
        if (resolved == StraightenReference.Horizontal)
        {
            if (dx < 0) (dx, dy) = (-dx, -dy);

            // theta is the line's angle below horizontal; rotating clockwise by -theta levels it.
            degrees = -(Math.Atan2(dy, dx) * 180.0 / Math.PI);
        }
        else
        {
            if (dy < 0) (dx, dy) = (-dx, -dy);

            // theta measured from +x; a plumb line points straight down at +90°.
            degrees = 90.0 - (Math.Atan2(dy, dx) * 180.0 / Math.PI);
        }

        return Clamp(degrees);
    }

    /// <summary>
    /// Adds a freshly measured delta to the angle already applied, clamped to the documented range.
    /// </summary>
    /// <param name="current">The photo's current straighten angle.</param>
    /// <param name="delta">The angle just measured off the preview.</param>
    public static double Combine(double current, double delta) => Clamp(current + delta);

    /// <summary>Clamps to ±<see cref="MaxDegrees"/>, mapping NaN to 0.</summary>
    /// <param name="degrees">A candidate angle.</param>
    public static double Clamp(double degrees) =>
        double.IsNaN(degrees) ? 0 : Math.Clamp(degrees, -MaxDegrees, MaxDegrees);
}
