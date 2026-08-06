namespace PhotoBook.Imaging.Auto;

/// <summary>
/// Finds how far a photo leans, so auto-adjust can stand it back up.
///
/// <para><b>How.</b> Straight lines in the world — horizons, door frames, table edges, the join
/// between wall and ceiling — are overwhelmingly horizontal or vertical, and a photo that leans
/// rotates all of them together by the same angle. So: take the Sobel gradient at every pixel, and
/// note that the gradient of an edge points across it, which means a level horizon has a straight-down
/// gradient and a plumb doorframe a straight-across one. Fold every gradient angle into
/// <c>[0°, 90°)</c> and those two cases land on the same value, 0 — as does every other line in a
/// level photo. Tilt the photo by <c>a</c> and the whole population shifts to <c>a</c>. The tilt is
/// therefore the peak of a magnitude-weighted histogram of folded gradient angles.</para>
///
/// <para><b>Why it refuses more often than it answers.</b> A photo of a face, a dog or a forest has no
/// dominant orientation, and its histogram is close to flat; picking the tallest bin of noise would
/// rotate the photo for no reason and crop away real content to hide the wedge. So the peak has to
/// stand well clear of the background before <see cref="Estimate"/> reports any confidence at all, and
/// the caller is expected to ignore low-confidence answers rather than scale them down.</para>
/// </summary>
public static class TiltEstimator
{
    /// <summary>Beyond this the photo is composed at an angle on purpose, not accidentally crooked.</summary>
    public const double MaxTiltDegrees = 8.0;

    /// <summary>Below this the correction costs a full geometry rebuild to move nothing visible.</summary>
    public const double DeadbandDegrees = 0.35;

    private const int BinsPerDegree = 4;
    private const int BinCount = 90 * BinsPerDegree;
    private const double PeakWindowDegrees = 1.5;

    /// <summary>
    /// Half-width of the box the histogram is convolved with before the peak is picked, in bins —
    /// <see cref="PeakWindowDegrees"/> either side.
    /// <para>
    /// This is a matched filter, not cosmetic smoothing, and getting it wrong is the difference
    /// between working and not. The edges of one real scene do not all agree to the quarter degree:
    /// lens distortion bends them, the subject is not perfectly built, and 8-bit quantization of a
    /// two-pixel-wide edge ramp scatters the measured angle. So the true signal is a broad mound a
    /// couple of degrees across. Meanwhile pixel-grid aliasing deposits narrow, tall spikes at
    /// particular angles that no scene feature put there. Picking the tallest single bin picks the
    /// spike; picking the window with the most mass picks the mound.
    /// </para>
    /// </summary>
    private const int SmoothingRadiusBins = (int)(PeakWindowDegrees * BinsPerDegree);

    /// <summary>
    /// The rotation that would level <paramref name="luma"/>, in degrees, together with how much the
    /// pixels agreed on it.
    /// </summary>
    /// <param name="luma">Row-major 8-bit luma, <paramref name="width"/> × <paramref name="height"/>.</param>
    /// <param name="width">Pixel width.</param>
    /// <param name="height">Pixel height.</param>
    /// <returns>
    /// <c>Degrees</c> is the correction, ready to assign to
    /// <see cref="PhotoBook.Core.Model.AdjustmentStack.Straighten"/> — positive rotates the image
    /// clockwise. <c>Confidence</c> is <c>0..1</c>; both are zero when nothing was found.
    /// </returns>
    public static (double Degrees, double Confidence) Estimate(ReadOnlySpan<byte> luma, int width, int height)
    {
        if (width < 16 || height < 16 || luma.Length < width * height) return (0, 0);

        var histogram = new double[BinCount];
        double total = 0;

        // One pass, interior pixels only: the 3×3 Sobel window has no meaning on the border, and a
        // photo's outermost row carries no information worth the bounds checks.
        for (var y = 1; y < height - 1; y++)
        {
            var row = y * width;
            var above = row - width;
            var below = row + width;

            for (var x = 1; x < width - 1; x++)
            {
                int tl = luma[above + x - 1], tc = luma[above + x], tr = luma[above + x + 1];
                int ml = luma[row + x - 1], mr = luma[row + x + 1];
                int bl = luma[below + x - 1], bc = luma[below + x], br = luma[below + x + 1];

                // Scharr's 3×10×3 weights, not Sobel's 1×2×1. Sobel's kernel is not rotationally
                // symmetric: the angle it reports drifts by up to a degree or so depending on the
                // true orientation, which is a large error when the whole answer is a few degrees.
                // Scharr's weights were derived by minimising exactly that anisotropy, at identical
                // cost — a 3×3 window and the same number of multiplies.
                var gx = ((3 * tr) + (10 * mr) + (3 * br)) - ((3 * tl) + (10 * ml) + (3 * bl));
                var gy = ((3 * bl) + (10 * bc) + (3 * br)) - ((3 * tl) + (10 * tc) + (3 * tr));

                // Squared magnitude as the weight, so a crisp architectural edge outvotes a soft
                // gradient across sky. Anything below the floor is texture or sensor noise, and
                // letting it vote is exactly how a photo of grass acquires a confident tilt.
                var magnitude = (gx * gx) + (gy * gy);
                if (magnitude < MagnitudeFloor) continue;

                var angle = Math.Atan2(gy, gx) * (180.0 / Math.PI);

                // Fold into [0, 90). Horizontal and vertical edges are the same evidence about tilt,
                // and a gradient and its opposite are the same edge.
                angle -= Math.Floor(angle / 90.0) * 90.0;

                var bin = (int)(angle * BinsPerDegree);
                if (bin >= BinCount) bin = BinCount - 1;

                var weight = Math.Sqrt(magnitude);
                histogram[bin] += weight;
                total += weight;
            }
        }

        if (total <= 0) return (0, 0);

        // Two stages, because no single estimator does both jobs. The box filter finds WHICH mound is
        // the dominant one and cannot be fooled by a tall narrow alias spike — but its argmax sits on
        // a plateau whenever the true peak is narrower than the window, so it is worthless for
        // precision. The mean-shift then walks to that mound's centre of mass in the unsmoothed
        // histogram, which is precise to a fraction of a bin.
        var smoothed = SmoothCircular(histogram, SmoothingRadiusBins);

        var peak = 0;
        for (var i = 1; i < smoothed.Length; i++)
        {
            if (smoothed[i] > smoothed[peak]) peak = i;
        }

        var refined = CentreOfMass(histogram, peak) / BinsPerDegree;

        // [0, 90) is really (−45, 45] about the nearest axis: 89° of lean is 1° the other way.
        var lean = refined > 45.0 ? refined - 90.0 : refined;

        var confidence = Confidence(smoothed, peak, total);

        if (confidence <= 0 || Math.Abs(lean) < DeadbandDegrees || Math.Abs(lean) > MaxTiltDegrees)
        {
            return (0, confidence);
        }

        // The histogram measured which way the content leans; the correction is the opposite.
        return (Math.Round(-lean, 4, MidpointRounding.AwayFromZero), confidence);
    }

    /// <summary>
    /// Squared-gradient floor: below any edge a person would call an edge, above JPEG ringing and
    /// sensor noise. Scaled for Scharr's weights, whose per-side sum is 16 against Sobel's 4, so a
    /// gradient magnitude of 160 here is the same physical edge a Sobel 40 would be.
    /// </summary>
    private const int MagnitudeFloor = 160 * 160;

    private static double[] SmoothCircular(double[] source, int radius)
    {
        var result = new double[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            double sum = 0;
            for (var d = -radius; d <= radius; d++)
            {
                var j = i + d;
                if (j < 0) j += source.Length;
                else if (j >= source.Length) j -= source.Length;
                sum += source[j];
            }

            result[i] = sum;
        }

        return result;
    }

    /// <summary>
    /// Walks from a coarse peak to the centre of mass of the mound around it — a mean shift over the
    /// unsmoothed histogram. Three passes is comfortably past convergence for a window this size, and
    /// offsets are accumulated as a fraction of a bin so the answer is not quantized to the quarter
    /// degree a bin is worth.
    /// </summary>
    private static double CentreOfMass(double[] histogram, int peak)
    {
        double centre = peak;

        for (var pass = 0; pass < 3; pass++)
        {
            var anchor = (int)Math.Round(centre);
            double weighted = 0, mass = 0;

            for (var d = -SmoothingRadiusBins; d <= SmoothingRadiusBins; d++)
            {
                var j = anchor + d;
                if (j < 0) j += histogram.Length;
                else if (j >= histogram.Length) j -= histogram.Length;

                weighted += d * histogram[j];
                mass += histogram[j];
            }

            if (mass <= 0) break;
            centre = anchor + (weighted / mass);
        }

        if (centre < 0) centre += histogram.Length;
        else if (centre >= histogram.Length) centre -= histogram.Length;
        return centre;
    }

    /// <summary>
    /// How dominant the peak is: the share of gradient energy within ±1.5° of it, rescaled so a flat
    /// histogram reads 0 and a photo whose lines really do agree reads 1.
    /// </summary>
    private static double Confidence(double[] smoothed, int peak, double total)
    {
        // The smoothed value at the peak already IS the mass within ±PeakWindowDegrees — that is what
        // the box filter computed. Every sample was counted once per window it fell in, so nothing
        // needs dividing out here.
        var share = smoothed[peak] / total;

        // A histogram with no dominant orientation spreads its energy over the whole 90°, putting
        // (2 × 1.5)/90 ≈ 3.3% inside the window; noise clusters lift the observed floor to around 6%.
        // A scene with one strong family of lines clears 25% comfortably.
        return Math.Clamp((share - 0.06) / (0.25 - 0.06), 0, 1);
    }
}
