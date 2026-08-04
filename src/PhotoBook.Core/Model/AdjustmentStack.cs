namespace PhotoBook.Core.Model;

/// <summary>
/// Non-destructive, parametric photo edits (doc 03 §3, R6/R11). Applied by the imaging layer at
/// render time in a fixed pipeline order; originals are never touched. Every value defaults to
/// <c>0</c> — the identity stack, which is omitted from JSON entirely.
/// <para>
/// Crop is deliberately <b>not</b> here: framing is a property of photo-in-slot and lives in the
/// placement's <see cref="CropState"/> (doc 05).
/// </para>
/// </summary>
public sealed record AdjustmentStack
{
    /// <summary>Brightness, <c>-1.0 .. 1.0</c>.</summary>
    public double Brightness { get; set; }

    /// <summary>Contrast, <c>-1.0 .. 1.0</c>.</summary>
    public double Contrast { get; set; }

    /// <summary>Saturation, <c>-1.0 .. 1.0</c>.</summary>
    public double Saturation { get; set; }

    /// <summary>White-balance temperature, <c>-1.0</c> (cool) .. <c>1.0</c> (warm).</summary>
    public double Temperature { get; set; }

    /// <summary>White-balance tint, <c>-1.0</c> (green) .. <c>1.0</c> (magenta).</summary>
    public double Tint { get; set; }

    /// <summary>Unsharp-mask amount, <c>0.0 .. 1.0</c>.</summary>
    public double Sharpness { get; set; }

    /// <summary>True when every parameter is at its identity value, so the stack need not be stored or applied.</summary>
    public bool IsIdentity =>
        Brightness == 0 && Contrast == 0 && Saturation == 0 &&
        Temperature == 0 && Tint == 0 && Sharpness == 0;

    /// <summary>A fresh identity stack.</summary>
    public static AdjustmentStack Identity => new();
}
