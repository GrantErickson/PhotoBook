namespace PhotoBook.Imaging;

/// <summary>
/// The eyedropper half of white balance: given a pixel the user says <em>should</em> be neutral grey,
/// work out the <see cref="ImageAdjustments.Temperature"/> and <see cref="ImageAdjustments.Tint"/>
/// that make it so.
///
/// <para>
/// This is the exact algebraic inverse of <c>AdjustmentPipeline.WhiteBalanceGains</c> — the same three
/// multipliers, solved the other way round — which is what makes the picker honest: click a white
/// wall, and the wall really does come out neutral rather than approximately neutral. The gains are
///</para>
/// <code>
///   red   = (1 + 0.20·temperature) · (1 + 0.05·tint)
///   green =                          (1 − 0.15·tint)
///   blue  = (1 − 0.20·temperature) · (1 + 0.05·tint)
/// </code>
/// <para>
/// which has exactly two degrees of freedom, so a sampled colour determines both parameters uniquely
/// (overall scale is irrelevant — white balance is about ratios, not exposure). The red/blue ratio
/// gives temperature with no reference to tint; green against the red-blue geometric mean then gives
/// tint.
/// </para>
///
/// <para>
/// <b>Sampling an already-corrected preview.</b> The user clicks the picture in front of them, which
/// already carries the current temperature and tint. Passing those in as
/// <paramref name="currentTemperature"/>/<paramref name="currentTint"/> divides them back out, so the
/// answer is an absolute setting rather than a correction stacked on a correction, and clicking the
/// same neutral twice is idempotent.
/// </para>
/// </summary>
public static class WhiteBalance
{
    /// <summary>
    /// The temperature and tint that neutralize a sampled colour. Channel values may be on any scale
    /// (0–255, 0–1, linear counts) as only their ratios matter; a black or degenerate sample returns
    /// the current setting unchanged.
    /// </summary>
    /// <param name="red">Red of the sampled pixel, as displayed.</param>
    /// <param name="green">Green of the sampled pixel, as displayed.</param>
    /// <param name="blue">Blue of the sampled pixel, as displayed.</param>
    /// <param name="currentTemperature">Temperature already applied to the pixels that were sampled.</param>
    /// <param name="currentTint">Tint already applied to the pixels that were sampled.</param>
    public static (double Temperature, double Tint) FromNeutral(
        double red, double green, double blue, double currentTemperature = 0, double currentTint = 0)
    {
        // Too dark or clipped to carry a reliable hue: refuse rather than invent a wild correction.
        if (!(red > 1e-6) || !(green > 1e-6) || !(blue > 1e-6))
        {
            return (Clamp(currentTemperature), Clamp(currentTint));
        }

        var (currentRed, currentGreen, currentBlue) = Gains(currentTemperature, currentTint);

        // The sample is base × current gain, so the gain that would have neutralized the base is
        // proportional to current ÷ sample. Scale is free; only the three ratios are used below.
        var targetRed = currentRed / red;
        var targetGreen = currentGreen / green;
        var targetBlue = currentBlue / blue;

        // Temperature from the red/blue ratio alone: (1+0.2t)/(1−0.2t) = k  ⇒  t = 5(k−1)/(k+1).
        var ratio = targetRed / targetBlue;
        var temperature = Clamp(5.0 * (ratio - 1.0) / (ratio + 1.0));

        // Tint from green against the red-blue geometric mean, with the temperature term divided out:
        // (1−0.15n) = ρ·q·(1+0.05n)  ⇒  n = (1 − ρq)/(0.15 + 0.05ρq),  q = sqrt(1 − 0.04t²).
        var q = Math.Sqrt(Math.Max(1e-9, 1.0 - (0.04 * temperature * temperature)));
        var mean = Math.Sqrt(targetRed * targetBlue);
        var rho = targetGreen / mean;
        var denominator = 0.15 + (0.05 * rho * q);
        var tint = Clamp(denominator <= 1e-9 ? currentTint : (1.0 - (rho * q)) / denominator);

        return (temperature, tint);
    }

    /// <summary>
    /// The per-channel multipliers a temperature/tint pair produces — the forward direction, exposed so
    /// callers (and tests) can verify a picked value really does neutralize the pixel it was picked from.
    /// </summary>
    /// <param name="temperature">Cool (−1) to warm (+1).</param>
    /// <param name="tint">Green (−1) to magenta (+1).</param>
    public static (double Red, double Green, double Blue) Gains(double temperature, double tint)
    {
        var t = Clamp(temperature);
        var n = Clamp(tint);
        return ((1 + (0.20 * t)) * (1 + (0.05 * n)), 1 - (0.15 * n), (1 - (0.20 * t)) * (1 + (0.05 * n)));
    }

    private static double Clamp(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, -1, 1);
}
