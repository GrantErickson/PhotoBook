using System.Text.Json.Serialization;

namespace PhotoBook.Core.Model;

/// <summary>
/// Who last wrote a photo's <see cref="AdjustmentStack"/>. Derived, never stored directly — see
/// <see cref="Photo.AdjustmentOrigin"/> for how the three states are reconstructed from the two
/// fields that <em>are</em> stored.
/// </summary>
public enum AdjustmentOrigin
{
    /// <summary>Nobody has touched this photo's edits. Auto-adjust may write to it freely.</summary>
    Untouched,

    /// <summary>
    /// The stack was produced by auto-adjust and the user has not modified it since. A later
    /// auto-adjust run re-derives it, which is what makes changing the look settings take effect.
    /// </summary>
    Automatic,

    /// <summary>
    /// The user edited this photo by hand. Auto-adjust never touches it again unless explicitly told
    /// to for this one photo (kernel §4: the engine is never entitled to undo a human).
    /// </summary>
    Manual,
}

/// <summary>
/// What auto-adjust measured in a photo's pixels, and therefore everything its rules need as input.
///
/// <para>
/// This is <b>derived</b> data that lives in <c>photos.json</c> rather than in <c>cache/</c>, for one
/// reason: it is what lets the look settings be re-applied to a whole book without decoding a single
/// file again. Measuring is the expensive half (a decode per photo); choosing parameters from a
/// measurement is arithmetic. Persisting the measurement turns "change the warmth and re-run" from a
/// minutes-long pass into an instant one. <see cref="Photo.Quality"/> and <see cref="Photo.Tier"/>
/// set the precedent for derived values living beside intent in the same file.
/// </para>
///
/// <para>
/// Every value is measured on the <b>unadjusted</b> original — the same pixels every time — so
/// re-running auto-adjust converges on one answer instead of compounding corrections onto its own
/// output. All values are rounded to four decimals before they get here, so two runs over an
/// unchanged photo write byte-identical JSON (doc 04 §4 rule 5).
/// </para>
/// </summary>
public sealed record AutoAdjustMeasurement
{
    /// <summary>Median luma, <c>0..1</c>. The signal exposure is chosen from.</summary>
    public double MedianLuma { get; set; }

    /// <summary>Luma at the 0.5th percentile, <c>0..1</c> — where the real black point sits.</summary>
    public double BlackPoint { get; set; }

    /// <summary>Luma at the 99.5th percentile, <c>0..1</c> — where the real white point sits.</summary>
    public double WhitePoint { get; set; }

    /// <summary>Fraction of pixels crushed to black, <c>0..1</c>.</summary>
    public double ClipLow { get; set; }

    /// <summary>Fraction of pixels blown to white, <c>0..1</c>.</summary>
    public double ClipHigh { get; set; }

    /// <summary>Illuminant estimate, red channel, <c>0..1</c>. See <see cref="MeanGreen"/>.</summary>
    public double MeanRed { get; set; }

    /// <summary>
    /// Illuminant estimate, green channel. The three means are a Minkowski <c>p = 6</c> norm rather
    /// than a plain average: grey-world (<c>p = 1</c>) is fooled by a large block of saturated colour,
    /// white-patch (<c>p = ∞</c>) is fooled by a single specular pixel, and the norm in between is the
    /// standard robust compromise. Only their ratios are used.
    /// </summary>
    public double MeanGreen { get; set; }

    /// <summary>Illuminant estimate, blue channel, <c>0..1</c>. See <see cref="MeanGreen"/>.</summary>
    public double MeanBlue { get; set; }

    /// <summary>Mean chroma, <c>0..1</c> — how colourful the photo already is, for the vibrance rule.</summary>
    public double Chroma { get; set; }

    /// <summary>
    /// The tilt auto-straighten found, in degrees; positive means the content leans clockwise and the
    /// correction rotates it back. Zero when nothing was found.
    /// </summary>
    public double TiltDegrees { get; set; }

    /// <summary>
    /// How strongly the pixels agreed on <see cref="TiltDegrees"/>, <c>0..1</c>. The rules refuse to
    /// straighten below a threshold, because a wrong straighten crops away real content.
    /// </summary>
    public double TiltConfidence { get; set; }
}

/// <summary>
/// The mark auto-adjust leaves on a photo: proof that the current <see cref="Photo.Adjustments"/> is
/// machine-derived, plus everything needed to re-derive it.
///
/// <para>
/// Presence is the whole signal — a photo carrying a stamp is on auto, a photo without one is not.
/// The stamp is cleared the moment the user moves a slider, which is what implements "as soon as a
/// picture is modified by hand this should not happen for this photo".
/// </para>
///
/// <para>
/// There is deliberately <b>no timestamp</b>. A wall-clock field would rewrite every photo row on
/// every run and break the guarantee that two saves of the same model are byte-identical (doc 04 §4
/// rule 5), for information nothing reads.
/// </para>
/// </summary>
public sealed record AutoAdjustStamp
{
    /// <summary>
    /// Identifies the rules that produced the stack: the algorithm version fused with the book's look
    /// settings. When this differs from the current rules the photo is out of date and the next run
    /// re-derives it; when it matches, and the measurement is still valid, the run can skip the photo
    /// entirely and touch no thumbnails.
    /// </summary>
    public string RulesVersion { get; set; } = string.Empty;

    /// <summary>
    /// The <see cref="Photo.ContentHash"/> the measurement was taken from. A re-import that replaces
    /// the bytes changes this, and the photo is re-measured rather than adjusted from stale numbers.
    /// </summary>
    public string SourceHash { get; set; } = string.Empty;

    /// <summary>What the pixels measured. Null only for a stamp written by a future build that dropped it.</summary>
    public AutoAdjustMeasurement? Measurement { get; set; }

    /// <summary>True when this stamp can be re-derived without decoding <paramref name="photo"/> again.</summary>
    /// <param name="photo">The photo the stamp belongs to.</param>
    public bool MeasurementAppliesTo(Photo photo) =>
        Measurement is not null &&
        photo is not null &&
        string.Equals(SourceHash, photo.ContentHash, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The book-wide "how should my photos look" settings (R6/R11) — the guidance auto-adjust applies on
/// top of what it measured, so a whole book comes out consistent rather than each photo being
/// corrected in isolation toward a neutral average.
///
/// <para>
/// Every value defaults to neutral, so a book that has never opened the panel gets the plain
/// correction. Changing anything here changes <see cref="AutoAdjustStamp.RulesVersion"/>, which is
/// what makes a second click of "Auto-adjust all" update every automatic photo.
/// </para>
/// </summary>
public sealed record LookProfile
{
    /// <summary>
    /// How far to go, <c>0..1</c>. Scales every correction the rules choose; 0 makes auto-adjust a
    /// no-op, 1 applies the full measured correction. Subtle ≈ 0.5, normal ≈ 0.75, strong = 1.0.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Strength { get; set; } = DefaultStrength;

    /// <summary>Nudges the brightness auto-adjust aims for, <c>-1</c> (darker) .. <c>1</c> (brighter).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Brightness { get; set; }

    /// <summary>Nudges the white balance, <c>-1</c> (cooler) .. <c>1</c> (warmer).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Warmth { get; set; }

    /// <summary>Nudges the contrast, <c>-1</c> (flatter) .. <c>1</c> (punchier).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Contrast { get; set; }

    /// <summary>Nudges the colour intensity, <c>-1</c> (muted) .. <c>1</c> (vivid).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Saturation { get; set; }

    /// <summary>
    /// Whether auto-adjust may also straighten a tilted photo. On by default. This is the one setting
    /// that moves pixels: the rotated wedge is cropped away, so a few percent of the frame edge is
    /// lost, and a later analysis run measures the straightened image.
    /// <para>
    /// Always written, unlike its neighbours. <c>WhenWritingDefault</c> compares against
    /// <c>default(bool)</c> — which is <c>false</c>, not this property's initializer — so omitting it
    /// would drop exactly the value worth storing and load it back as <c>true</c>. Turning
    /// auto-straighten off would not survive a save.
    /// </para>
    /// </summary>
    public bool Straighten { get; set; } = true;

    /// <summary>Whether newly imported photos are auto-adjusted as they arrive. Off by default.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AdjustOnImport { get; set; }

    /// <summary>The default <see cref="Strength"/> — "normal" on the three-stop dial.</summary>
    public const double DefaultStrength = 0.75;

    /// <summary>True when every value is at its default, so the profile need not be stored.</summary>
    [JsonIgnore]
    public bool IsDefault =>
        Strength == DefaultStrength && Brightness == 0 && Warmth == 0 && Contrast == 0 &&
        Saturation == 0 && Straighten && !AdjustOnImport;

    /// <summary>
    /// A short stable text identifying these settings, for <see cref="AutoAdjustStamp.RulesVersion"/>.
    /// Values are rounded to two decimals first: the dial cannot express finer than that, and a raw
    /// double would make two visually identical profiles read as different rules.
    /// </summary>
    public string ToRulesKey() => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"s{Round(Strength)};b{Round(Brightness)};w{Round(Warmth)};c{Round(Contrast)};v{Round(Saturation)};{(Straighten ? "t" : "-")}");

    private static double Round(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
