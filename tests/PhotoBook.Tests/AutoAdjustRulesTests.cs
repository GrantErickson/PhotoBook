using PhotoBook.Core.Model;
using PhotoBook.Imaging;
using PhotoBook.Imaging.Auto;

namespace PhotoBook.Tests;

/// <summary>
/// The auto-adjust engine, tested where it is pure: a measurement plus a look profile in, an
/// <see cref="AdjustmentStack"/> out. No files, no decoding, no clock.
/// </summary>
public sealed class AutoAdjustRulesTests
{
    /// <summary>A photo that needs nothing: median in the middle, endpoints at the ends, neutral grey.</summary>
    private static AutoAdjustMeasurement WellExposed() => new()
    {
        MedianLuma = 0.46,
        BlackPoint = 0.02,
        WhitePoint = 0.97,
        ClipLow = 0.002,
        ClipHigh = 0.002,
        MeanRed = 0.50,
        MeanGreen = 0.50,
        MeanBlue = 0.50,
        Chroma = 0.22,
    };

    [Fact]
    public void APhotoThatNeedsNothingIsLeftAlone()
    {
        var stack = AutoAdjustRules.Choose(WellExposed(), new LookProfile());

        Assert.True(
            stack.IsIdentity,
            "a correctly exposed, neutral, contrasty photo was still 'corrected': " +
            $"ev {stack.ExposureEv}, contrast {stack.Contrast}, temp {stack.Temperature}, " +
            $"blacks {stack.Blacks}, whites {stack.Whites}, vibrance {stack.Vibrance}");
    }

    [Fact]
    public void ADarkPhotoIsBrightenedAndABrightOnePulledBack()
    {
        var dark = AutoAdjustRules.Choose(WellExposed() with { MedianLuma = 0.16 }, new LookProfile());
        var bright = AutoAdjustRules.Choose(WellExposed() with { MedianLuma = 0.78 }, new LookProfile());

        Assert.True(dark.ExposureEv > 0.4, $"a median of 0.16 only earned {dark.ExposureEv:F2} EV");
        Assert.True(bright.ExposureEv < -0.2, $"a median of 0.78 only earned {bright.ExposureEv:F2} EV");
    }

    [Fact]
    public void AnAlreadyBlownPhotoIsNotPushedFurtherIntoTheCeiling()
    {
        // Dark median but a fifth of the frame already clipped — a backlit window. Lifting exposure
        // here buys midtone at the cost of the last recoverable highlight detail.
        var blown = WellExposed() with { MedianLuma = 0.20, ClipHigh = 0.20, WhitePoint = 1.0 };

        var stack = AutoAdjustRules.Choose(blown, new LookProfile());

        Assert.True(stack.ExposureEv <= 0.02, $"exposure was still pushed +{stack.ExposureEv:F2} EV into 20% clipping");
        Assert.True(stack.Highlights < 0, "the blown highlights were not pulled back");
        Assert.True(stack.Shadows > 0, "the shadows were not opened to compensate");
    }

    [Fact]
    public void AHazyPhotoGetsItsBlackPointBack()
    {
        var hazy = WellExposed() with { BlackPoint = 0.28, WhitePoint = 0.74, MedianLuma = 0.50 };

        var stack = AutoAdjustRules.Choose(hazy, new LookProfile());

        Assert.True(stack.Blacks < -0.1, $"a black point sitting at 0.28 was only pulled {stack.Blacks:F2}");
        Assert.True(stack.Whites > 0.1, $"a white point stopping at 0.74 was only pushed {stack.Whites:F2}");
        Assert.True(stack.Contrast > 0, "a flat photo got no contrast at all");
    }

    [Fact]
    public void AWarmCastIsCooledAndTheCorrectionIsNotTotal()
    {
        // Tungsten indoors: the illuminant estimate leans hard to red.
        var warm = WellExposed() with { MeanRed = 0.62, MeanGreen = 0.50, MeanBlue = 0.36 };

        var stack = AutoAdjustRules.Choose(warm, new LookProfile());

        Assert.True(stack.Temperature < -0.05, $"an orange cast was not cooled at all (temp {stack.Temperature:F2})");

        // Deliberately partial: neutralising completely turns every warm evening into an overcast one.
        var full = WhiteBalance.FromNeutral(0.62, 0.50, 0.36);
        Assert.True(
            Math.Abs(stack.Temperature) < Math.Abs(full.Temperature),
            "the cast was neutralised completely; the rules are supposed to damp white balance");
    }

    [Fact]
    public void StrengthScalesTheWholeCorrection()
    {
        var dark = WellExposed() with { MedianLuma = 0.16 };

        var normal = AutoAdjustRules.Choose(dark, new LookProfile { Strength = 1.0 });
        var subtle = AutoAdjustRules.Choose(dark, new LookProfile { Strength = 0.5 });
        var off = AutoAdjustRules.Choose(dark, new LookProfile { Strength = 0 });

        Assert.Equal(normal.ExposureEv * 0.5, subtle.ExposureEv, 3);
        Assert.True(off.IsIdentity, "strength 0 still produced a correction");
    }

    [Fact]
    public void TheLookProfileMovesTheTargetRatherThanAddingAConstant()
    {
        var dark = WellExposed() with { MedianLuma = 0.30 };

        var neutral = AutoAdjustRules.Choose(dark, new LookProfile());
        var brighter = AutoAdjustRules.Choose(dark, new LookProfile { Brightness = 1.0 });
        var warmer = AutoAdjustRules.Choose(WellExposed(), new LookProfile { Warmth = 1.0 });

        Assert.True(brighter.ExposureEv > neutral.ExposureEv, "asking for brighter did not brighten");
        Assert.True(warmer.Temperature > 0.1, "asking for warmer did not warm a neutral photo");
    }

    [Fact]
    public void ChangingAnyLookSettingChangesTheRulesVersion()
    {
        var baseline = AutoAdjustRules.VersionFor(new LookProfile());

        Assert.NotEqual(baseline, AutoAdjustRules.VersionFor(new LookProfile { Strength = 1.0 }));
        Assert.NotEqual(baseline, AutoAdjustRules.VersionFor(new LookProfile { Warmth = 0.3 }));
        Assert.NotEqual(baseline, AutoAdjustRules.VersionFor(new LookProfile { Straighten = false }));

        // Same settings, different instances: the version is about the values, not the object.
        Assert.Equal(baseline, AutoAdjustRules.VersionFor(new LookProfile()));

        // AdjustOnImport is about when the button runs, not what it does, so it must NOT invalidate
        // every photo in the book.
        Assert.Equal(baseline, AutoAdjustRules.VersionFor(new LookProfile { AdjustOnImport = true }));
    }

    [Fact]
    public void AQuarterTurnAndAMirrorSurviveARerun()
    {
        // Someone fixed a sideways photo by hand and then asked for auto-adjust. Putting it back on
        // its side would be the engine undoing a human.
        var basis = new AdjustmentStack { Rotate = 90, FlipHorizontal = true, Contrast = 0.9 };

        var stack = AutoAdjustRules.Choose(WellExposed() with { MedianLuma = 0.2 }, new LookProfile(), basis);

        Assert.Equal(90, stack.Rotate);
        Assert.True(stack.FlipHorizontal);
        Assert.NotEqual(0.9, stack.Contrast);
    }

    [Fact]
    public void StraighteningIsRefusedWhenTheEvidenceIsWeak()
    {
        var tilted = WellExposed() with { TiltDegrees = -3.2, TiltConfidence = 0.9 };

        Assert.Equal(-3.2, AutoAdjustRules.Choose(tilted, new LookProfile()).Straighten, 3);

        // Not confident enough to justify cropping the frame.
        Assert.Equal(0, AutoAdjustRules.Choose(tilted with { TiltConfidence = 0.2 }, new LookProfile()).Straighten);

        // Turned off in the look profile.
        Assert.Equal(0, AutoAdjustRules.Choose(tilted, new LookProfile { Straighten = false }).Straighten);

        // Composed at an angle on purpose, not accidentally crooked.
        Assert.Equal(0, AutoAdjustRules.Choose(tilted with { TiltDegrees = -22 }, new LookProfile()).Straighten);

        // Too small to be worth a geometry rebuild.
        Assert.Equal(0, AutoAdjustRules.Choose(tilted with { TiltDegrees = -0.1 }, new LookProfile()).Straighten);
    }

    [Fact]
    public void StraighteningIsNotScaledDownByStrength()
    {
        // A half-corrected horizon is still visibly crooked and has still paid for the crop.
        var tilted = WellExposed() with { TiltDegrees = 4.0, TiltConfidence = 0.9 };

        Assert.Equal(4.0, AutoAdjustRules.Choose(tilted, new LookProfile { Strength = 0.25 }).Straighten, 3);
    }

    [Fact]
    public void EveryParameterStaysInsideItsDocumentedRange()
    {
        // The worst inputs the measurer can produce, plus a look profile pushed to every extreme.
        var extremes = new[]
        {
            new AutoAdjustMeasurement { MedianLuma = 0.001, BlackPoint = 0, WhitePoint = 0.05, ClipLow = 0.9, MeanRed = 0.01, MeanGreen = 0.9, MeanBlue = 0.01 },
            new AutoAdjustMeasurement { MedianLuma = 0.999, BlackPoint = 0.95, WhitePoint = 1, ClipHigh = 0.9, MeanRed = 0.9, MeanGreen = 0.01, MeanBlue = 0.9 },
            new AutoAdjustMeasurement(),
        };

        foreach (var measurement in extremes)
        {
            foreach (var strength in new[] { 0.0, 0.5, 1.0 })
            {
                foreach (var nudge in new[] { -1.0, 0.0, 1.0 })
                {
                    var look = new LookProfile
                    {
                        Strength = strength,
                        Brightness = nudge,
                        Warmth = nudge,
                        Contrast = nudge,
                        Saturation = nudge,
                    };

                    var s = AutoAdjustRules.Choose(measurement, look);

                    InRange(s.ExposureEv, -2, 2, nameof(s.ExposureEv));
                    InRange(s.Brightness, -1, 1, nameof(s.Brightness));
                    InRange(s.Contrast, -1, 1, nameof(s.Contrast));
                    InRange(s.Highlights, -1, 1, nameof(s.Highlights));
                    InRange(s.Shadows, -1, 1, nameof(s.Shadows));
                    InRange(s.Whites, -1, 1, nameof(s.Whites));
                    InRange(s.Blacks, -1, 1, nameof(s.Blacks));
                    InRange(s.Temperature, -1, 1, nameof(s.Temperature));
                    InRange(s.Tint, -1, 1, nameof(s.Tint));
                    InRange(s.Saturation, -1, 1, nameof(s.Saturation));
                    InRange(s.Vibrance, -1, 1, nameof(s.Vibrance));
                    InRange(s.Straighten, -15, 15, nameof(s.Straighten));
                }
            }
        }
    }

    private static void InRange(double value, double min, double max, string name)
    {
        Assert.True(double.IsFinite(value), $"{name} was {value}");
        Assert.True(value >= min && value <= max, $"{name} was {value}, outside {min}..{max}");
    }
}
