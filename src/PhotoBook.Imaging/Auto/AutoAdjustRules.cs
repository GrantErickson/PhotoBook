using PhotoBook.Core.Model;

namespace PhotoBook.Imaging.Auto;

/// <summary>
/// Chooses an <see cref="AdjustmentStack"/> from what <see cref="PhotoMeasurer"/> measured and what
/// the book's <see cref="LookProfile"/> asks for. Pure arithmetic — no pixels, no I/O, no clock — so
/// the same photo and the same settings always give the same stack, and the whole thing is testable
/// without decoding anything.
///
/// <para><b>The shape of the correction.</b> Each parameter is chosen from the one measurement that
/// actually implies it, rather than from a single "quality" score: exposure from where the median
/// sits, the end points from where the real black and white points sit, shadow and highlight recovery
/// from how much is already clipped, white balance from the estimated illuminant. Deliberately
/// conservative — the photos are family snapshots, not a portfolio, and a book of over-corrected
/// pictures is worse than a book of slightly flat ones (R27: "edits should really be tweaks").</para>
///
/// <para><b>What auto never touches.</b> <see cref="AdjustmentStack.Rotate"/> and
/// <see cref="AdjustmentStack.FlipHorizontal"/> are carried through from whatever was already there.
/// A quarter-turn is someone fixing an orientation the camera got wrong, not a look, and re-running
/// auto-adjust must not put a photo back on its side.</para>
/// </summary>
public static class AutoAdjustRules
{
    /// <summary>
    /// The algorithm's own version, fused into <see cref="AutoAdjustStamp.RulesVersion"/>. Bump this
    /// whenever a constant below changes, or existing books will keep the parameters the old rules
    /// chose and quietly disagree with new ones.
    /// </summary>
    public const string AlgorithmVersion = "auto-1";

    /// <summary>Below this, the tilt estimate is a guess and straightening would crop for nothing.</summary>
    public const double MinTiltConfidence = 0.45;

    /// <summary>Parameters smaller than this are dropped: they are invisible and only churn caches.</summary>
    private const double NoiseFloor = 0.005;

    /// <summary>Identifies the rules a stack was produced by — the algorithm plus the look settings.</summary>
    /// <param name="look">The book's look profile.</param>
    public static string VersionFor(LookProfile look)
    {
        ArgumentNullException.ThrowIfNull(look);
        return $"{AlgorithmVersion};{look.ToRulesKey()}";
    }

    /// <summary>
    /// The stack auto-adjust would give this photo.
    /// </summary>
    /// <param name="measurement">What the original's pixels measured.</param>
    /// <param name="look">The book's look profile.</param>
    /// <param name="basis">
    /// The photo's current stack, for the parameters auto does not own. Pass the existing stack so a
    /// user's quarter-turn or mirror survives; pass null or identity for a fresh photo.
    /// </param>
    public static AdjustmentStack Choose(
        AutoAdjustMeasurement measurement, LookProfile look, AdjustmentStack? basis = null)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        ArgumentNullException.ThrowIfNull(look);

        var strength = Math.Clamp(look.Strength, 0, 1);

        var stack = new AdjustmentStack
        {
            // Not auto's to decide — see the class remarks.
            Rotate = basis?.Rotate ?? 0,
            FlipHorizontal = basis?.FlipHorizontal ?? false,
        };

        stack.Straighten = ChooseStraighten(measurement, look);

        if (strength <= 0) return Clean(stack);

        var black = Math.Clamp(measurement.BlackPoint, 0, 1);
        var white = Math.Clamp(measurement.WhitePoint, 0, 1);
        var median = Math.Clamp(measurement.MedianLuma, 0, 1);
        var clipLow = Math.Clamp(measurement.ClipLow, 0, 1);
        var clipHigh = Math.Clamp(measurement.ClipHigh, 0, 1);

        // ---- exposure ------------------------------------------------------------------------------
        // Where a well-exposed frame's median sits. The look's Brightness slides the target rather
        // than adding a constant, so "brighter" means brighter *after* correction, not brighter than
        // whatever the photo happened to be.
        var target = Math.Clamp(0.46 + (0.10 * Clamp1(look.Brightness)), 0.25, 0.70);
        var wanted = median > 0.01 ? Math.Log2(target / median) : 1.2;
        wanted = Math.Clamp(wanted, -1.2, 1.2);

        // Do not push a photo that is already losing its highlights any further into the ceiling.
        var ev = wanted;
        if (ev > 0 && clipHigh > 0.02) ev *= Math.Clamp(1 - ((clipHigh - 0.02) / 0.10), 0, 1);

        // How much lift the clipping guard just refused. A backlit frame — dark subject, blown window
        // — ends up here with nothing done for it unless the job passes to the shadow recovery, which
        // lifts the subject without touching the window. That is what a person does by hand.
        var withheld = Math.Max(0, wanted - ev);

        // ---- end points ----------------------------------------------------------------------------
        // A washed-out black point means haze or a faded scan: pull it down. A black point of zero
        // with a lot of pixels piled on it means the shadows are already crushed: lift instead.
        double blacks;
        if (black > 0.05)
        {
            blacks = -Math.Clamp((black - 0.03) / 0.25, 0, 1);
        }
        else
        {
            blacks = Math.Clamp((clipLow - 0.02) / 0.15, 0, 0.5);
        }

        // Symmetrically at the top: a white point well short of 1 wants stretching, a blown one wants
        // pulling back.
        var whites = clipHigh > 0.03
            ? -Math.Clamp((clipHigh - 0.03) / 0.12, 0, 0.8)
            : Math.Clamp((0.96 - white) / 0.35, 0, 1);

        // ---- recovery ------------------------------------------------------------------------------
        var shadows = Math.Min(
            0.75,
            Math.Clamp((clipLow - 0.01) / 0.12, 0, 0.6) + Math.Clamp(withheld * 0.45, 0, 0.55));
        var highlights = -Math.Clamp((clipHigh - 0.005) / 0.08, 0, 0.7);

        // ---- contrast ------------------------------------------------------------------------------
        // Mostly the user's call. The endpoints above already stretch a flat photo, so the automatic
        // component is small and only rescues something genuinely lifeless.
        var range = Math.Max(0, white - black);
        var autoContrast = Math.Clamp((0.70 - range) / 0.60, 0, 0.35);
        var contrast = autoContrast + (0.40 * Clamp1(look.Contrast));

        // ---- white balance -------------------------------------------------------------------------
        // FromNeutral answers "what settings make this colour grey?", and the illuminant estimate is
        // exactly the colour that should have been grey. Damped harder than the tone parameters and
        // clamped tighter: neutralising completely is what turns a warm sunset into an overcast
        // afternoon, and a slightly warm photo is almost never a complaint.
        var (temperature, tint) = WhiteBalance.FromNeutral(
            measurement.MeanRed, measurement.MeanGreen, measurement.MeanBlue);

        temperature = Math.Clamp(temperature * 0.60, -0.5, 0.5) + (0.35 * Clamp1(look.Warmth));
        tint = Math.Clamp(tint * 0.50, -0.35, 0.35);

        // ---- colour --------------------------------------------------------------------------------
        // Vibrance, not saturation, for the automatic part: it lifts a flat, hazy frame without
        // pushing skin tones orange. Saturation is left as the look's own dial.
        var vibrance = Math.Clamp((0.16 - Math.Clamp(measurement.Chroma, 0, 1)) / 0.30, 0, 0.35);
        var saturation = 0.35 * Clamp1(look.Saturation);
        if (look.Saturation > 0) vibrance += 0.15 * look.Saturation;

        // Strength scales the whole correction. It does not scale Straighten: a half-straightened
        // horizon is still crooked and still paid the crop, so straightening is all or nothing.
        stack.ExposureEv = Scale(ev, strength, -2, 2);
        stack.Blacks = Scale(blacks, strength, -1, 1);
        stack.Whites = Scale(whites, strength, -1, 1);
        stack.Shadows = Scale(shadows, strength, -1, 1);
        stack.Highlights = Scale(highlights, strength, -1, 1);
        stack.Contrast = Scale(contrast, strength, -1, 1);
        stack.Temperature = Scale(temperature, strength, -1, 1);
        stack.Tint = Scale(tint, strength, -1, 1);
        stack.Vibrance = Scale(vibrance, strength, -1, 1);
        stack.Saturation = Scale(saturation, strength, -1, 1);

        return Clean(stack);
    }

    /// <summary>The straighten angle the rules would apply, or 0 when they refuse.</summary>
    /// <param name="measurement">What the original's pixels measured.</param>
    /// <param name="look">The book's look profile.</param>
    public static double ChooseStraighten(AutoAdjustMeasurement measurement, LookProfile look)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        ArgumentNullException.ThrowIfNull(look);

        if (!look.Straighten) return 0;
        if (measurement.TiltConfidence < MinTiltConfidence) return 0;

        var degrees = measurement.TiltDegrees;
        return Math.Abs(degrees) < TiltEstimator.DeadbandDegrees ||
               Math.Abs(degrees) > TiltEstimator.MaxTiltDegrees
            ? 0
            : Math.Round(degrees, 4, MidpointRounding.AwayFromZero);
    }

    private static double Clamp1(double value) => double.IsFinite(value) ? Math.Clamp(value, -1, 1) : 0;

    private static double Scale(double value, double strength, double min, double max) =>
        double.IsFinite(value)
            ? Math.Round(Math.Clamp(value * strength, min, max), 4, MidpointRounding.AwayFromZero)
            : 0;

    /// <summary>Drops parameters too small to see, so an already-good photo comes back as identity.</summary>
    private static AdjustmentStack Clean(AdjustmentStack stack)
    {
        stack.ExposureEv = Drop(stack.ExposureEv);
        stack.Brightness = Drop(stack.Brightness);
        stack.Contrast = Drop(stack.Contrast);
        stack.Highlights = Drop(stack.Highlights);
        stack.Shadows = Drop(stack.Shadows);
        stack.Whites = Drop(stack.Whites);
        stack.Blacks = Drop(stack.Blacks);
        stack.Temperature = Drop(stack.Temperature);
        stack.Tint = Drop(stack.Tint);
        stack.Saturation = Drop(stack.Saturation);
        stack.Vibrance = Drop(stack.Vibrance);
        return stack;
    }

    private static double Drop(double value) => Math.Abs(value) < NoiseFloor ? 0 : value;
}
