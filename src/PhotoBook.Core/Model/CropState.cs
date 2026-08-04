namespace PhotoBook.Core.Model;

/// <summary>
/// The one crop model (kernel §4, doc 03 §6). Auto-layout emits exactly what the user hand-tweaks:
/// docs 07, 08 and 09 all use this shape and <see cref="CropMath"/> for the arithmetic.
/// <list type="bullet">
/// <item><description><c>coverScale = max(slotW/imgW, slotH/imgH)</c> — the scale at which the
/// image exactly covers the slot.</description></item>
/// <item><description>Effective scale is <c>zoom × coverScale</c>; <c>zoom = 1.0</c> is the
/// minimal-crop cover fit and the default.</description></item>
/// <item><description><see cref="OffsetX"/>/<see cref="OffsetY"/> pan the image center relative to
/// the slot center, in slot-width and slot-height units, clamped so no gap can appear while
/// <c>zoom ≥ 1</c>.</description></item>
/// <item><description><c>zoom &lt; 1</c> is legal: the image no longer fills the slot and the page
/// background shows through as a letterbox (R9).</description></item>
/// </list>
/// </summary>
/// <param name="Zoom">Multiplier on the cover scale; <c>1.0</c> is the minimal-crop cover fit.</param>
/// <param name="OffsetX">Pan in slot-width units; <c>0</c> centers the image horizontally.</param>
/// <param name="OffsetY">Pan in slot-height units; <c>0</c> centers the image vertically.</param>
public readonly record struct CropState(double Zoom, double OffsetX, double OffsetY)
{
    /// <summary>Lowest zoom the editor allows (doc 09 §3.3).</summary>
    public const double MinZoom = 0.25;

    /// <summary>Highest zoom the editor allows (doc 09 §3.3).</summary>
    public const double MaxZoom = 4.0;

    /// <summary>The centered, minimal-crop cover fit — the engine's default emission (doc 08 §8).</summary>
    public static CropState Default => new(1.0, 0, 0);

    /// <summary>True when the image does not fill the slot and the page background shows through (R9).</summary>
    public bool IsLetterboxed => Zoom < 1.0;

    /// <inheritdoc/>
    public override string ToString() => $"zoom {Zoom:0.###} @ ({OffsetX:0.###}, {OffsetY:0.###})";
}
