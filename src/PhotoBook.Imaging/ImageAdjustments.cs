using PhotoBook.Core.Model;

namespace PhotoBook.Imaging;

/// <summary>
/// The imaging layer's view of a photo's non-destructive edits: the full four-stage parameter set of
/// doc 05 "AdjustmentStack", expressed on the <c>-1 .. 1</c> scale that <see cref="AdjustmentStack"/>
/// uses (doc 05 writes the same sliders as <c>-100 .. 100</c>; divide by 100).
///
/// <para>
/// <see cref="AdjustmentStack"/> in <c>PhotoBook.Core</c> is the persisted contract and carries the six
/// parameters the catalog stores today. This record is a superset: it adds the geometry stage, the
/// highlight/shadow recovery, vibrance, vignette and black-and-white that doc 05 specifies, so the
/// pipeline is complete before the catalog schema catches up. Converting from the Core stack is
/// implicit and lossless — <see cref="From"/> maps the six stored parameters and leaves the rest at
/// identity.
/// </para>
///
/// <para>
/// Stage order is fixed — <b>geometry → exposure → color → finish</b> — so the same parameters give the
/// same pixels regardless of the order the user moved the sliders, at thumbnail, preview and export
/// resolution alike.
/// </para>
/// </summary>
public sealed record ImageAdjustments
{
    // ---- geometry -----------------------------------------------------------------------------

    /// <summary>Extra quarter-turn rotation beyond EXIF auto-orient: 0, 90, 180 or 270 degrees clockwise.</summary>
    public int Rotate { get; init; }

    /// <summary>Straightening angle in degrees, <c>-15 .. 15</c>; the rotated wedge is auto-cropped away.</summary>
    public double Straighten { get; init; }

    /// <summary>Mirror horizontally.</summary>
    public bool FlipHorizontal { get; init; }

    // ---- exposure -----------------------------------------------------------------------------

    /// <summary>Exposure in stops, <c>-2 .. 2</c>. A linear multiply by <c>2^ev</c>.</summary>
    public double ExposureEv { get; init; }

    /// <summary>Brightness, <c>-1 .. 1</c> (R6). Applied as a gamma so it lifts without clipping.</summary>
    public double Brightness { get; init; }

    /// <summary>Contrast, <c>-1 .. 1</c> (R6). An S-curve about mid-grey, so extremes compress rather than clip.</summary>
    public double Contrast { get; init; }

    /// <summary>Highlight recovery, <c>-1</c> (pull down) .. <c>1</c> (lift), weighted toward bright tones.</summary>
    public double Highlights { get; init; }

    /// <summary>Shadow recovery, <c>-1</c> (deepen) .. <c>1</c> (open up), weighted toward dark tones.</summary>
    public double Shadows { get; init; }

    // ---- color --------------------------------------------------------------------------------

    /// <summary>White balance, <c>-1</c> (cool/blue) .. <c>1</c> (warm/amber).</summary>
    public double Temperature { get; init; }

    /// <summary>White balance, <c>-1</c> (green) .. <c>1</c> (magenta).</summary>
    public double Tint { get; init; }

    /// <summary>Global saturation, <c>-1</c> (grey) .. <c>1</c> (double).</summary>
    public double Saturation { get; init; }

    /// <summary>
    /// Vibrance, <c>-1 .. 1</c>: saturation weighted by the inverse of each pixel's existing saturation,
    /// so muted colors move and already-saturated skin tones stay put.
    /// </summary>
    public double Vibrance { get; init; }

    // ---- finish -------------------------------------------------------------------------------

    /// <summary>Unsharp-mask amount, <c>0 .. 1</c>.</summary>
    public double Sharpen { get; init; }

    /// <summary>Vignette strength, <c>0 .. 1</c>.</summary>
    public double Vignette { get; init; }

    /// <summary>Convert to black and white (with the slight contrast lift doc 05 calls for).</summary>
    public bool BlackAndWhite { get; init; }

    /// <summary>The identity stack: every parameter neutral, nothing to apply.</summary>
    public static ImageAdjustments Identity { get; } = new();

    /// <summary>
    /// True when applying this stack is a no-op, so callers can skip the work entirely and cache under
    /// the plain content-hash key.
    /// </summary>
    public bool IsIdentity =>
        Rotate == 0 && Straighten == 0 && !FlipHorizontal &&
        ExposureEv == 0 && Brightness == 0 && Contrast == 0 && Highlights == 0 && Shadows == 0 &&
        Temperature == 0 && Tint == 0 && Saturation == 0 && Vibrance == 0 &&
        Sharpen == 0 && Vignette == 0 && !BlackAndWhite;

    /// <summary>True when the geometry stage moves pixels, which is what forces analysis to re-run (doc 05 matrix).</summary>
    public bool HasGeometry => Rotate != 0 || Straighten != 0 || FlipHorizontal;

    /// <summary>
    /// Just the geometry stage, with every tone parameter at identity — what the analysis copy is built
    /// with. Analysis measures the <em>source</em> photo, so brightening a dark photo must not re-tier
    /// it; but geometry moves the pixels that Focus Region coordinates point at, so it must. Keying the
    /// analysis copy on this projection makes the doc 05 invalidation matrix fall out of the cache key
    /// instead of relying on bookkeeping.
    /// </summary>
    public ImageAdjustments GeometryOnly => HasGeometry
        ? new ImageAdjustments { Rotate = Rotate, Straighten = Straighten, FlipHorizontal = FlipHorizontal }
        : Identity;

    /// <summary>
    /// Projects the persisted <see cref="AdjustmentStack"/> onto this record. Null becomes
    /// <see cref="Identity"/>.
    /// </summary>
    public static ImageAdjustments From(AdjustmentStack? stack) => stack is null || stack.IsIdentity
        ? Identity
        : new ImageAdjustments
        {
            Brightness = stack.Brightness,
            Contrast = stack.Contrast,
            Saturation = stack.Saturation,
            Temperature = stack.Temperature,
            Tint = stack.Tint,
            Sharpen = stack.Sharpness,
        };

    /// <summary>Projects the six persisted parameters back onto a Core stack; the extra stages are dropped.</summary>
    public AdjustmentStack ToAdjustmentStack() => new()
    {
        Brightness = Brightness,
        Contrast = Contrast,
        Saturation = Saturation,
        Temperature = Temperature,
        Tint = Tint,
        Sharpness = Sharpen,
    };

    /// <summary>Lets callers pass a catalog <see cref="AdjustmentStack"/> anywhere this record is expected.</summary>
    public static implicit operator ImageAdjustments(AdjustmentStack stack) => From(stack);

    /// <summary>
    /// Clamps every parameter into its documented range and snaps <see cref="Rotate"/> to a quarter turn.
    /// The pipeline normalizes before applying, so an out-of-range value from a stale project file
    /// degrades instead of producing garbage.
    /// </summary>
    public ImageAdjustments Normalized() => this with
    {
        Rotate = NormalizeRotate(Rotate),
        Straighten = Clamp(Straighten, -15, 15),
        ExposureEv = Clamp(ExposureEv, -2, 2),
        Brightness = Clamp(Brightness, -1, 1),
        Contrast = Clamp(Contrast, -1, 1),
        Highlights = Clamp(Highlights, -1, 1),
        Shadows = Clamp(Shadows, -1, 1),
        Temperature = Clamp(Temperature, -1, 1),
        Tint = Clamp(Tint, -1, 1),
        Saturation = Clamp(Saturation, -1, 1),
        Vibrance = Clamp(Vibrance, -1, 1),
        Sharpen = Clamp(Sharpen, 0, 1),
        Vignette = Clamp(Vignette, 0, 1),
    };

    private static double Clamp(double value, double min, double max) =>
        double.IsNaN(value) ? 0 : Math.Clamp(value, min, max);

    private static int NormalizeRotate(int degrees)
    {
        var turns = ((degrees / 90) % 4 + 4) % 4;
        return turns * 90;
    }
}
