using System.Text.Json.Serialization;

namespace PhotoBook.Core.Model;

/// <summary>
/// Non-destructive, parametric photo edits (doc 03 §3, R6/R11) — the full four-stage parameter set of
/// doc 05 "AdjustmentStack". Applied by the imaging layer at render time in a fixed stage order —
/// <b>geometry → exposure → color → finish</b> — so the same parameters give the same pixels no matter
/// what order the user moved the sliders in, at thumbnail, preview and 300 DPI export resolution
/// alike. Originals are never touched.
/// <para>
/// Sliders are stored on the <c>-1 .. 1</c> scale (doc 05 writes the same sliders as
/// <c>-100 .. 100</c>; divide by 100). This record is the persisted contract in <c>photos.json</c>
/// and mirrors <c>PhotoBook.Imaging.ImageAdjustments</c> parameter for parameter, so the round trip
/// between catalog and pipeline is lossless in both directions — a user's advanced edits survive a
/// save. The single spelling difference is <see cref="Sharpness"/>, which the imaging record calls
/// <c>Sharpen</c>; it is mapped explicitly on both sides. The property name is kept because renaming
/// it would be a shape change under doc 04 §5 for no gain.
/// </para>
/// <para>
/// Every value defaults to its neutral value — the identity stack (<see cref="IsIdentity"/>), which
/// serializes to <c>{}</c>: each parameter is omitted while it sits at its default, so photos.json
/// shows real edits and nothing else (doc 04 §1, doc 05 "all-zero/false … is omitted from JSON").
/// Growing this record was purely additive — new optional fields that default to identity — so
/// <c>photos.json</c> stays at <c>schemaVersion</c> 1 and a file written before the widening loads
/// unchanged, with the new parameters reading as identity (doc 04 §5).
/// </para>
/// <para>
/// Crop is deliberately <b>not</b> here: framing is a property of photo-in-slot and lives in the
/// placement's <see cref="CropState"/> (doc 05).
/// </para>
/// </summary>
public sealed record AdjustmentStack
{
    // ---- geometry ---------------------------------------------------------------------------------

    /// <summary>Extra quarter-turn rotation beyond EXIF auto-orient: <c>0 | 90 | 180 | 270</c> degrees clockwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Rotate { get; set; }

    /// <summary>Straightening angle in degrees, <c>-15 .. 15</c>; the rotated wedge is auto-cropped away.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Straighten { get; set; }

    /// <summary>Mirror horizontally (doc 05 <c>flipH</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool FlipHorizontal { get; set; }

    // ---- exposure ---------------------------------------------------------------------------------

    /// <summary>Exposure in stops, <c>-2.0 .. 2.0</c>. A linear multiply by <c>2^ev</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double ExposureEv { get; set; }

    /// <summary>Brightness, <c>-1.0 .. 1.0</c> (R6). Applied as a gamma so it lifts without clipping.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Brightness { get; set; }

    /// <summary>Contrast, <c>-1.0 .. 1.0</c> (R6). An S-curve about mid-grey, so extremes compress rather than clip.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Contrast { get; set; }

    /// <summary>Highlight recovery, <c>-1.0</c> (pull down) .. <c>1.0</c> (lift), weighted toward bright tones.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Highlights { get; set; }

    /// <summary>Shadow recovery, <c>-1.0</c> (deepen) .. <c>1.0</c> (open up), weighted toward dark tones.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Shadows { get; set; }

    // ---- color ------------------------------------------------------------------------------------

    /// <summary>White-balance temperature, <c>-1.0</c> (cool/blue) .. <c>1.0</c> (warm/amber).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Temperature { get; set; }

    /// <summary>White-balance tint, <c>-1.0</c> (green) .. <c>1.0</c> (magenta).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Tint { get; set; }

    /// <summary>Global saturation, <c>-1.0</c> (grey) .. <c>1.0</c> (double).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Saturation { get; set; }

    /// <summary>
    /// Vibrance, <c>-1.0 .. 1.0</c>: saturation weighted by the inverse of each pixel's existing
    /// saturation, so muted colors move and already-saturated skin tones stay put.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Vibrance { get; set; }

    // ---- finish -----------------------------------------------------------------------------------

    /// <summary>Unsharp-mask amount, <c>0.0 .. 1.0</c> (doc 05 <c>sharpen</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Sharpness { get; set; }

    /// <summary>Vignette strength, <c>0.0 .. 1.0</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Vignette { get; set; }

    /// <summary>Convert to black and white (with the slight contrast lift doc 05 calls for).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool BlackAndWhite { get; set; }

    /// <summary>True when every parameter is at its identity value, so the stack need not be stored or applied.</summary>
    [JsonIgnore]
    public bool IsIdentity =>
        Rotate == 0 && Straighten == 0 && !FlipHorizontal &&
        ExposureEv == 0 && Brightness == 0 && Contrast == 0 && Highlights == 0 && Shadows == 0 &&
        Temperature == 0 && Tint == 0 && Saturation == 0 && Vibrance == 0 &&
        Sharpness == 0 && Vignette == 0 && !BlackAndWhite;

    /// <summary>
    /// True when the geometry stage moves pixels — the one class of edit that invalidates the analysis
    /// copy and remaps Focus Region coordinates (doc 05 cache-invalidation matrix).
    /// </summary>
    [JsonIgnore]
    public bool HasGeometry => Rotate != 0 || Straighten != 0 || FlipHorizontal;

    /// <summary>A fresh identity stack.</summary>
    public static AdjustmentStack Identity => new();
}
