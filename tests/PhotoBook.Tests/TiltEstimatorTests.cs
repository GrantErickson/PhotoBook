using PhotoBook.Core.Model;
using PhotoBook.Imaging;
using PhotoBook.Imaging.Auto;

namespace PhotoBook.Tests;

/// <summary>
/// Auto-straighten, tested against pixels that really are rotated rather than against an argument
/// about which way ImageMagick turns. The load-bearing test is the round trip: measure a crooked
/// photo, apply the angle the estimator returned through the real
/// <see cref="AdjustmentPipeline"/>, measure again, and the result must be level. A sign error fails
/// that test loudly — it would double the tilt instead of removing it.
/// </summary>
public sealed class TiltEstimatorTests
{
    private const int Size = 480;

    /// <summary>
    /// A scene made of straight lines — a bookshelf, a window frame, a tiled floor — rotated by
    /// <paramref name="degrees"/> clockwise on screen. This is the case auto-straighten exists for.
    /// </summary>
    private static DecodedImage Grid(double degrees, int width = Size, int height = Size)
    {
        var pixels = new byte[width * height * 4];
        var radians = degrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var cx = width / 2.0;
        var cy = height / 2.0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // Rotate the sampling grid by -degrees, which draws the content rotated by +degrees.
                var dx = x - cx;
                var dy = y - cy;
                var u = (dx * cos) + (dy * sin);
                var v = (-dx * sin) + (dy * cos);

                // Anti-aliased, not a hard threshold. A hard edge staircases onto the pixel grid and
                // its Sobel gradients point along the axes no matter how the line is really oriented,
                // which is a property of the fixture and not of photographs — every real edge is
                // band-limited by the lens and the sensor. A soft ramp carries the true angle.
                var du = Math.Abs(Mod(u + 20.0, 40.0) - 20.0);
                var dv = Math.Abs(Mod(v + 20.0, 40.0) - 20.0);
                var distance = Math.Min(du, dv);

                // A ~3 px ramp, which is what a lens, a Bayer demosaic and a JPEG actually deliver.
                // A 1 px ramp is sampled at barely one point and carries almost no sub-pixel angle.
                var t = Math.Clamp((distance - 2.0) / 3.0, 0, 1);
                var level = (byte)Math.Round(30 + (t * 180));

                var p = ((y * width) + x) * 4;
                pixels[p] = level;
                pixels[p + 1] = level;
                pixels[p + 2] = level;
                pixels[p + 3] = 255;
            }
        }

        return new DecodedImage(pixels, width, height);
    }

    private static double Mod(double value, double modulus)
    {
        var r = value % modulus;
        return r < 0 ? r + modulus : r;
    }

    private static byte[] Luma(DecodedImage image)
    {
        var luma = new byte[image.Width * image.Height];
        for (int i = 0, p = 0; i < luma.Length; i++, p += 4)
        {
            luma[i] = (byte)((0.299 * image.Pixels[p + 2]) + (0.587 * image.Pixels[p + 1]) + (0.114 * image.Pixels[p]));
        }

        return luma;
    }

    private static (double Degrees, double Confidence) Measure(DecodedImage image) =>
        TiltEstimator.Estimate(Luma(image), image.Width, image.Height);

    [Fact]
    public void ALevelSceneIsLeftAlone()
    {
        var (degrees, confidence) = Measure(Grid(0));

        Assert.True(confidence > AutoAdjustRules.MinTiltConfidence,
            $"a scene made entirely of straight lines only scored {confidence:F2} confidence");
        Assert.Equal(0, degrees);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(-1.5)]
    [InlineData(3.0)]
    [InlineData(-4.25)]
    [InlineData(6.0)]
    public void ACrookedSceneIsMeasuredToWithinAQuarterDegree(double tilt)
    {
        var (degrees, confidence) = Measure(Grid(tilt));

        Assert.True(confidence > AutoAdjustRules.MinTiltConfidence,
            $"a {tilt}° grid only scored {confidence:F2} confidence");

        // The correction is the opposite of the lean.
        Assert.Equal(-tilt, degrees, 0.2);
    }

    [Fact]
    public void ApplyingTheCorrectionActuallyLevelsThePhoto()
    {
        // The test that settles the sign. If Estimate returned the lean instead of the correction,
        // applying it would take a 5° tilt to 10° and this would fail by a wide margin.
        const double Tilt = 5.0;
        var crooked = Grid(Tilt);

        var (correction, confidence) = Measure(crooked);
        Assert.True(confidence > AutoAdjustRules.MinTiltConfidence);

        // Without this the test passes vacuously: an estimator that always answered "level" would
        // apply nothing and measure nothing left over.
        Assert.True(
            Math.Abs(correction) > 1.0,
            $"the estimator called a {Tilt}° tilt {correction:F2}° — there is nothing to round-trip");

        var straightened = AdjustmentPipeline.Default.Apply(
            crooked, new ImageAdjustments { Straighten = correction });

        var (residual, _) = Measure(straightened);

        Assert.True(
            Math.Abs(residual) < TiltEstimator.DeadbandDegrees,
            $"after applying Straighten = {correction:F2} to a {Tilt}° tilt, {residual:F2}° of lean was left — " +
            "the correction has the wrong sign or the wrong magnitude");
    }

    [Fact]
    public void ASceneWithNoStraightLinesIsRefused()
    {
        // Foliage, fur, a crowd: gradients point everywhere, and the tallest bin is noise. Rotating
        // on that evidence would crop away real content for no reason.
        var random = new Random(20260806);
        var pixels = new byte[Size * Size * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var level = (byte)random.Next(256);
            pixels[i] = level;
            pixels[i + 1] = level;
            pixels[i + 2] = level;
            pixels[i + 3] = 255;
        }

        var (degrees, confidence) = Measure(new DecodedImage(pixels, Size, Size));

        Assert.True(
            confidence < AutoAdjustRules.MinTiltConfidence,
            $"random noise was {confidence:F2} confident it was tilted by {degrees:F2}°");
    }

    [Fact]
    public void AFlatFieldProducesNoAnswerAtAll()
    {
        var pixels = new byte[Size * Size * 4];
        Array.Fill(pixels, (byte)128);
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        var (degrees, confidence) = Measure(new DecodedImage(pixels, Size, Size));

        Assert.Equal(0, degrees);
        Assert.Equal(0, confidence);
    }

    [Fact]
    public void ADeliberateDutchAngleIsNotCorrected()
    {
        // 20° is a composition choice. The estimator may well find it; the rules must decline it.
        var (degrees, confidence) = Measure(Grid(20));

        var measurement = new AutoAdjustMeasurement { TiltDegrees = degrees, TiltConfidence = confidence };

        Assert.Equal(0, AutoAdjustRules.ChooseStraighten(measurement, new LookProfile()));
    }

    [Fact]
    public void MeasuringAWholePhotoFillsInTheTiltAlongsideTheTone()
    {
        var measurement = PhotoMeasurer.Measure(Grid(-3.0));

        Assert.Equal(3.0, measurement.TiltDegrees, 0.25);
        Assert.True(measurement.TiltConfidence > AutoAdjustRules.MinTiltConfidence);

        // And the tone half of the same pass is sane: a grid of 30 on 210 is high contrast, neutral.
        Assert.InRange(measurement.MedianLuma, 0.70, 0.90);
        Assert.Equal(measurement.MeanRed, measurement.MeanBlue, 3);
        Assert.True(measurement.Chroma < 0.01, "a greyscale scene measured as colourful");
    }

    [Fact]
    public void TiltMeasurementCanBeSkipped()
    {
        var measurement = PhotoMeasurer.Measure(Grid(4.0), measureTilt: false);

        Assert.Equal(0, measurement.TiltDegrees);
        Assert.Equal(0, measurement.TiltConfidence);
        Assert.True(measurement.MedianLuma > 0, "skipping tilt also skipped the tone measurement");
    }
}
