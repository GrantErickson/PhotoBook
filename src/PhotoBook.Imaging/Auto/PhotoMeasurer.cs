using PhotoBook.Core.Model;

namespace PhotoBook.Imaging.Auto;

/// <summary>
/// Turns a photo's pixels into the handful of numbers auto-adjust reasons about.
///
/// <para>
/// Everything is measured on the <b>unadjusted</b> original, never on the corrected preview. That is
/// the property that makes the button re-runnable: click it twice and the second run measures exactly
/// what the first one did, so it converges on one answer instead of brightening an already-brightened
/// photo. It is the same reason the analysis copy exists (doc 05: "brightening a dark photo must not
/// re-tier it").
/// </para>
///
/// <para>
/// Measuring happens at <see cref="MeasureLongEdgePx"/>, not full resolution. A histogram is a
/// population statistic and a 640 px decode of a 24-megapixel frame has ~400 000 samples in it — more
/// than enough for percentiles that are stable to well under the fourth decimal the result is rounded
/// to, at a fraction of the decode cost. Tilt detection wants the same order of resolution: enough
/// pixels for a horizon to be a long straight run, few enough that a single Sobel pass is cheap.
/// </para>
/// </summary>
public sealed class PhotoMeasurer
{
    /// <summary>Long edge the measurement decode is scaled to.</summary>
    public const int MeasureLongEdgePx = 640;

    private readonly IImageDecoder _decoder;

    /// <summary>Creates a measurer over a decoder.</summary>
    /// <param name="decoder">The decoder; defaults to a fresh <see cref="ImageDecoder"/>.</param>
    public PhotoMeasurer(IImageDecoder? decoder = null) => _decoder = decoder ?? new ImageDecoder();

    /// <summary>Decodes <paramref name="path"/> small and unadjusted, and measures it.</summary>
    /// <param name="path">Absolute path of the archived original.</param>
    /// <param name="measureTilt">Whether to run the extra Sobel pass for auto-straighten.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ImageDecodeException">The file could not be decoded.</exception>
    public async Task<AutoAdjustMeasurement> MeasureAsync(
        string path, bool measureTilt = true, CancellationToken ct = default)
    {
        var decoded = await _decoder.DecodeAsync(path, MeasureLongEdgePx, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return Measure(decoded, measureTilt);
    }

    /// <summary>Measures an already-decoded, unadjusted buffer.</summary>
    /// <param name="image">BGRA8888 pixels of the original.</param>
    /// <param name="measureTilt">Whether to run the extra Sobel pass for auto-straighten.</param>
    public static AutoAdjustMeasurement Measure(DecodedImage image, bool measureTilt = true)
    {
        ArgumentNullException.ThrowIfNull(image);

        var pixels = image.Pixels;
        var count = image.Width * image.Height;
        var luma = new byte[count];
        var histogram = new int[256];

        // The illuminant estimate is a Minkowski p = 6 norm, accumulated here as a sum of sixth
        // powers. Grey-world (p = 1) reads a red barn as red light and cools the whole photo; the
        // white-patch limit (p = ∞) hands the answer to one specular highlight. p = 6 is the standard
        // compromise, and it costs three multiplies.
        double powerRed = 0, powerGreen = 0, powerBlue = 0;
        double chroma = 0;

        for (int i = 0, p = 0; i < count; i++, p += 4)
        {
            double b = pixels[p];
            double g = pixels[p + 1];
            double r = pixels[p + 2];

            var y = (0.299 * r) + (0.587 * g) + (0.114 * b);
            var level = (byte)(y < 0 ? 0 : y > 255 ? 255 : y);
            luma[i] = level;
            histogram[level]++;

            var r2 = r * r; powerRed += r2 * r2 * r2;
            var g2 = g * g; powerGreen += g2 * g2 * g2;
            var b2 = b * b; powerBlue += b2 * b2 * b2;

            var max = r > g ? (r > b ? r : b) : (g > b ? g : b);
            var min = r < g ? (r < b ? r : b) : (g < b ? g : b);
            chroma += max - min;
        }

        var (tilt, tiltConfidence) = measureTilt
            ? TiltEstimator.Estimate(luma, image.Width, image.Height)
            : (0.0, 0.0);

        var samples = Math.Max(1, count);

        return new AutoAdjustMeasurement
        {
            MedianLuma = Round(Percentile(histogram, count, 0.50) / 255.0),
            BlackPoint = Round(Percentile(histogram, count, 0.005) / 255.0),
            WhitePoint = Round(Percentile(histogram, count, 0.995) / 255.0),

            // "Clipped" is the top and bottom three levels rather than 0 and 255 exactly: a JPEG that
            // was blown out comes back with ringing around 252-254, and demanding a literal 255 would
            // report a blown sky as perfectly exposed.
            ClipLow = Round(Fraction(histogram, 0, 2, samples)),
            ClipHigh = Round(Fraction(histogram, 253, 255, samples)),

            MeanRed = Round(SixthRoot(powerRed / samples) / 255.0),
            MeanGreen = Round(SixthRoot(powerGreen / samples) / 255.0),
            MeanBlue = Round(SixthRoot(powerBlue / samples) / 255.0),

            Chroma = Round(chroma / samples / 255.0),

            TiltDegrees = tilt,
            TiltConfidence = Round(tiltConfidence),
        };
    }

    /// <summary>The luma level at which the cumulative histogram first reaches <paramref name="quantile"/>.</summary>
    private static double Percentile(int[] histogram, int total, double quantile)
    {
        if (total <= 0) return 0;
        var target = quantile * total;
        double running = 0;
        for (var i = 0; i < histogram.Length; i++)
        {
            running += histogram[i];
            if (running >= target) return i;
        }

        return histogram.Length - 1;
    }

    private static double Fraction(int[] histogram, int from, int to, int total)
    {
        long sum = 0;
        for (var i = from; i <= to; i++) sum += histogram[i];
        return (double)sum / total;
    }

    private static double SixthRoot(double value) => value <= 0 ? 0 : Math.Pow(value, 1.0 / 6.0);

    /// <summary>
    /// Four decimals, the same precision <c>AdjustmentHash</c> keeps. Anything finer is invisible to
    /// the thumbnail cache but still churns the photos.json diff on every run (doc 04 §4 rule 5).
    /// </summary>
    private static double Round(double value) =>
        double.IsFinite(value) ? Math.Round(value, 4, MidpointRounding.AwayFromZero) : 0;
}
