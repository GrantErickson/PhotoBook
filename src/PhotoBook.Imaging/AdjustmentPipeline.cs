using ImageMagick;
using PhotoBook.Core.Model;

namespace PhotoBook.Imaging;

/// <summary>
/// Applies a photo's parametric edits to decoded pixels, in the fixed stage order of doc 05 —
/// <b>geometry → exposure → color → finish</b> — so the result depends on the parameters and never on
/// the order the user moved the sliders.
///
/// <para>
/// Non-destructive by construction: this class only ever mutates a decoded copy. Nothing here opens a
/// file for writing, so an original under <c>originals/</c> cannot be touched even by accident
/// (kernel §5). The same parameters replay identically at 256 px, at 1024 px, and at 300 DPI export
/// size, which is what makes the grid an honest preview of the print.
/// </para>
///
/// <para>
/// Speed matters here: Magick.NET-Q8's bundled natives are built without OpenMP, so every operator is
/// a single-threaded full-image pass costing roughly 40 ms at 1024 px. The pipeline is therefore
/// written to minimize passes rather than to read prettily — the seven parameters of the exposure
/// stage <em>and</em> the white-balance gains of the color stage collapse into one per-channel
/// 256-entry lookup table applied in a single <c>Clut</c>. A typical tone edit
/// (brightness/contrast/exposure/temperature/saturation) re-renders a 1024 px preview in about 90 ms,
/// fast enough to drive a slider. Vibrance, sharpening and the geometry stage each add their own
/// passes and are better applied on slider release than on every drag frame.
/// </para>
/// </summary>
public sealed class AdjustmentPipeline
{
    /// <summary>A shared instance; the pipeline is stateless and thread-safe.</summary>
    public static AdjustmentPipeline Default { get; } = new();

    /// <summary>
    /// Applies adjustments to an already-decoded buffer — the interactive-preview path, no file IO.
    /// The source buffer is not modified; a new <see cref="DecodedImage"/> is returned.
    /// </summary>
    /// <param name="source">Decoded BGRA8888 pixels, typically the 1024 px preview tier.</param>
    /// <param name="adjustments">The edits to apply; identity returns the source unchanged.</param>
    /// <param name="ct">Cancellation token.</param>
    public DecodedImage Apply(DecodedImage source, ImageAdjustments? adjustments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var normalized = (adjustments ?? ImageAdjustments.Identity).Normalized();
        if (normalized.IsIdentity) return source;

        ct.ThrowIfCancellationRequested();
        using var image = MagickPipeline.FromDecodedImage(source);
        Apply(image, normalized, ct);
        return MagickPipeline.ToDecodedImage(image);
    }

    /// <summary>
    /// Off-thread <see cref="Apply(DecodedImage, ImageAdjustments, CancellationToken)"/>, for a UI thread
    /// that must not block on a slider drag.
    /// </summary>
    /// <param name="source">Decoded BGRA8888 pixels.</param>
    /// <param name="adjustments">The edits to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<DecodedImage> ApplyAsync(DecodedImage source, ImageAdjustments? adjustments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var normalized = (adjustments ?? ImageAdjustments.Identity).Normalized();
        if (normalized.IsIdentity) return Task.FromResult(source);
        return Task.Run(() => Apply(source, normalized, ct), ct);
    }

    /// <summary>
    /// Decodes an original and applies adjustments in one pass — what export and full-resolution edit
    /// previews use. The original file is opened read-only.
    /// </summary>
    /// <param name="originalPath">Absolute path of the archived original.</param>
    /// <param name="adjustments">The edits to apply.</param>
    /// <param name="maxLongEdge">Downsample the result to this long edge; 0 for native size.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<DecodedImage> ApplyToFileAsync(
        string originalPath, ImageAdjustments? adjustments, int maxLongEdge = 0, CancellationToken ct = default)
    {
        var normalized = (adjustments ?? ImageAdjustments.Identity).Normalized();
        using var image = await MagickPipeline.LoadOrientedAsync(originalPath, maxLongEdge, ct).ConfigureAwait(false);
        Apply(image, normalized, ct);
        return MagickPipeline.ToDecodedImage(image);
    }

    /// <summary>Convenience overload for a catalog <see cref="AdjustmentStack"/>.</summary>
    /// <param name="originalPath">Absolute path of the archived original.</param>
    /// <param name="stack">The photo's stored adjustments.</param>
    /// <param name="maxLongEdge">Downsample the result to this long edge; 0 for native size.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<DecodedImage> ApplyToFileAsync(
        string originalPath, AdjustmentStack? stack, int maxLongEdge = 0, CancellationToken ct = default) =>
        ApplyToFileAsync(originalPath, ImageAdjustments.From(stack), maxLongEdge, ct);

    /// <summary>
    /// The stage machine itself, operating on a live Magick image. Internal because
    /// <see cref="MagickImage"/> holds native memory that must not escape this assembly.
    /// </summary>
    internal void Apply(MagickImage image, ImageAdjustments adjustments, CancellationToken ct)
    {
        var a = adjustments.Normalized();
        if (a.IsIdentity) return;

        ApplyGeometry(image, a, ct);

        // Stages 2 and 3 are separate in the doc because they are separate to the user; in pixels the
        // exposure curve and the white-balance gains are one per-channel function, so they run as one
        // pass. Saturation and vibrance genuinely mix channels and follow.
        ApplyToneAndWhiteBalance(image, a, ct);
        ApplySaturation(image, a, ct);
        ApplyFinish(image, a, ct);
    }

    // ---- stage 1: geometry ---------------------------------------------------------------------

    private static void ApplyGeometry(MagickImage image, ImageAdjustments a, CancellationToken ct)
    {
        if (!a.HasGeometry) return;
        ct.ThrowIfCancellationRequested();

        if (a.FlipHorizontal) image.Flop();
        if (a.Rotate != 0) image.Rotate(a.Rotate);

        if (a.Straighten != 0)
        {
            var beforeWidth = (double)image.Width;
            var beforeHeight = (double)image.Height;

            // Black background rather than transparent: the wedge is cropped away anyway, and keeping
            // the image opaque saves a whole alpha-flattening pass.
            image.Alpha(AlphaOption.Off);
            image.BackgroundColor = MagickColors.Black;
            image.Rotate(a.Straighten);

            // Auto-crop the wedge the rotation introduced: the largest axis-aligned rectangle of the
            // original aspect that fits inside the rotated frame (doc 05: "auto-crops the wedge").
            // Rounding DOWN matters — rounding up would reach back into the wedge.
            var (cropWidth, cropHeight) = LargestInscribedRect(beforeWidth, beforeHeight, a.Straighten);
            var w = (uint)Math.Max(1, Math.Floor(cropWidth));
            var h = (uint)Math.Max(1, Math.Floor(cropHeight));
            if (w < image.Width || h < image.Height)
            {
                // Rotate leaves a virtual-canvas offset behind, and Crop with a Gravity is resolved
                // against that page rather than the pixels — so the page has to be cleared first or
                // the centred crop is centred on the wrong origin and keeps a wedge.
                image.ResetPage();
                image.Crop(new MagickGeometry(w, h), Gravity.Center);
                image.ResetPage();
            }
        }
    }

    /// <summary>
    /// Dimensions of the largest centred axis-aligned rectangle <b>of the original aspect ratio</b>
    /// that fits inside a <paramref name="width"/>×<paramref name="height"/> rectangle rotated by
    /// <paramref name="degrees"/>.
    /// <para>
    /// Deliberately not the maximum-area inscribed rectangle. That one is wider or taller than the
    /// source — a 16:9 frame straightened by 6.5° comes back at 2.09:1 — and the aspect a photo
    /// reports is load-bearing here: <see cref="PhotoBook.Core.Model.CropMath"/> derives every slot
    /// crop from it, so a straighten that silently reshaped the frame would shift the photo inside
    /// every slot it appears in. It also touches the rotated boundary exactly, which leaves a sliver
    /// of the background wedge once the result is rounded to whole pixels.
    /// </para>
    /// <para>
    /// A centred rect of half-extents (x, y) sits inside the rotated frame when
    /// <c>x·cos + y·sin ≤ W/2</c> and <c>x·sin + y·cos ≤ H/2</c>. Scaling the source by <c>k</c>
    /// gives <c>k = min(W / (W·cos + H·sin), H / (W·sin + H·cos))</c>.
    /// </para>
    /// </summary>
    internal static (double Width, double Height) LargestInscribedRect(double width, double height, double degrees)
    {
        if (width <= 0 || height <= 0) return (width, height);

        var angle = Math.Abs(degrees % 180) * Math.PI / 180.0;
        if (angle > Math.PI / 2) angle = Math.PI - angle;

        var sin = Math.Abs(Math.Sin(angle));
        var cos = Math.Abs(Math.Cos(angle));
        if (sin < 1e-9) return (width, height);

        var scale = Math.Min(
            width / ((width * cos) + (height * sin)),
            height / ((width * sin) + (height * cos)));

        // A whisker inside the boundary: the fit above is exact, and an exact fit rounds outward into
        // the wedge on some sizes.
        scale *= 0.998;

        return (Math.Max(1, width * scale), Math.Max(1, height * scale));
    }

    // ---- stage 2: exposure ---------------------------------------------------------------------

    private static void ApplyToneAndWhiteBalance(MagickImage image, ImageAdjustments a, CancellationToken ct)
    {
        var curves = BuildChannelCurves(a);
        if (curves is null) return;
        ct.ThrowIfCancellationRequested();

        var (red, green, blue) = curves.Value;
        using var lut = new MagickImage(MagickColors.Black, (uint)red.Length, 1);
        lut.Alpha(AlphaOption.Off);
        lut.ColorSpace = ColorSpace.sRGB;
        using (var pixels = lut.GetPixels())
        {
            for (var x = 0; x < red.Length; x++)
            {
                pixels.SetPixel(x, 0, [red[x], green[x], blue[x]]);
            }
        }

        // A colour lookup table maps each channel through its own curve, so this single pass carries
        // exposure, brightness, contrast, highlights, shadows, temperature and tint.
        image.Clut(lut, PixelInterpolateMethod.Nearest, Channels.RGB);
    }

    /// <summary>
    /// Builds the three 256-entry channel curves that carry the exposure stage and the white-balance
    /// half of the color stage, or null when both are neutral.
    /// </summary>
    internal static (byte[] Red, byte[] Green, byte[] Blue)? BuildChannelCurves(ImageAdjustments a)
    {
        var tone = BuildToneCurve(a);
        var (gainRed, gainGreen, gainBlue) = WhiteBalanceGains(a);
        var neutralGains = gainRed == 1 && gainGreen == 1 && gainBlue == 1;
        if (tone is null && neutralGains) return null;

        byte[] Channel(double gain)
        {
            if (gain == 1 && tone is not null) return tone;

            var curve = new byte[256];
            for (var i = 0; i < curve.Length; i++)
            {
                var value = (tone is null ? i : tone[i]) * gain;
                curve[i] = (byte)Math.Clamp(Math.Round(value), 0, 255);
            }

            return curve;
        }

        return (Channel(gainRed), Channel(gainGreen), Channel(gainBlue));
    }

    /// <summary>
    /// Per-channel gains for temperature and tint. Warm lifts red and drops blue; magenta drops green
    /// and lifts the red/blue pair. Doc 05 calls this a "per-channel color matrix"; with no cross-channel
    /// terms it is exactly three multipliers, which is what lets it ride along in the lookup table.
    /// </summary>
    /// <remarks>
    /// The formula itself lives in <see cref="WhiteBalance.Gains"/> so that the eyedropper, which
    /// inverts it, cannot drift out of step with the pipeline that applies it.
    /// </remarks>
    internal static (double Red, double Green, double Blue) WhiteBalanceGains(ImageAdjustments a) =>
        WhiteBalance.Gains(a.Temperature, a.Tint);

    /// <summary>
    /// Builds the 256-entry tone curve for the exposure stage, or null when the stage is neutral.
    /// Exposed internally so tests can assert monotonicity and endpoints without decoding an image.
    /// </summary>
    internal static byte[]? BuildToneCurve(ImageAdjustments a)
    {
        if (a.ExposureEv == 0 && a.Brightness == 0 && a.Contrast == 0 && a.Highlights == 0 &&
            a.Shadows == 0 && a.Whites == 0 && a.Blacks == 0)
            return null;

        var exposureGain = Math.Pow(2, a.ExposureEv);
        var brightnessGamma = Math.Pow(2, -a.Brightness * 1.2);

        var curve = new byte[256];
        for (var i = 0; i < curve.Length; i++)
        {
            var y = i / 255.0;

            // Exposure: a linear gain in stops, exactly as a camera would.
            if (a.ExposureEv != 0) y = Math.Clamp(y * exposureGain, 0, 1);

            // Brightness: gamma rather than an offset, so it lifts midtones without crushing either end.
            if (a.Brightness != 0) y = Math.Pow(y, brightnessGamma);

            // Contrast: blend toward a smoothstep S-curve (positive) or toward a compressed ramp
            // (negative). Both are monotonic and neither clips.
            if (a.Contrast > 0)
            {
                var s = y * y * (3 - 2 * y);
                y += a.Contrast * (s - y);
            }
            else if (a.Contrast < 0)
            {
                var flat = 0.25 + y * 0.5;
                y += -a.Contrast * (flat - y);
            }

            // Shadows and highlights: weighted lifts confined to their end of the range.
            if (a.Shadows != 0)
            {
                var weight = (1 - y) * (1 - y);
                y += a.Shadows * 0.5 * weight * (a.Shadows > 0 ? 1 - y : y);
            }

            if (a.Highlights != 0)
            {
                var weight = y * y;
                y += a.Highlights * 0.5 * weight * (a.Highlights > 0 ? 1 - y : y);
            }

            // Black and white points: the endpoints themselves, with a cubic weight so the effect dies
            // out well before the midtones and the two never overlap. The 0.30 coefficient is the
            // largest that keeps the curve monotonic at full deflection (slope 1 − 0.9·|k| at the end
            // it acts on), which is what stops a hard "Blacks −100" from posterizing the shadows.
            if (a.Blacks != 0)
            {
                var weight = (1 - y) * (1 - y) * (1 - y);
                y += a.Blacks * 0.30 * weight;
            }

            if (a.Whites != 0)
            {
                var weight = y * y * y;
                y += a.Whites * 0.30 * weight;
            }

            curve[i] = (byte)Math.Clamp(Math.Round(y * 255.0), 0, 255);
        }

        return curve;
    }

    // ---- stage 3: color ------------------------------------------------------------------------

    private static void ApplySaturation(MagickImage image, ImageAdjustments a, CancellationToken ct)
    {
        if (a.Saturation == 0 && a.Vibrance == 0) return;
        ct.ThrowIfCancellationRequested();

        if (a.Saturation != 0)
        {
            image.Modulate(new Percentage(100), new Percentage(100 + a.Saturation * 100), new Percentage(100));
        }

        if (a.Vibrance != 0) ApplyVibrance(image, a.Vibrance);
    }

    /// <summary>
    /// Vibrance: a saturation change weighted by the inverse of each pixel's existing saturation, so
    /// muted colors move and already-vivid ones (skin, sky) hold still. Implemented as a masked blend
    /// between the image and a fully saturated copy, the mask being the negated HSL saturation channel.
    /// </summary>
    private static void ApplyVibrance(MagickImage image, double vibrance)
    {
        if (image.HasAlpha)
        {
            // The mask rides in on the alpha channel, so a genuinely transparent source cannot use this
            // path without losing its transparency. Vibrance degrades to a gentler global saturation
            // rather than quietly flattening the image.
            image.Modulate(new Percentage(100), new Percentage(100 + vibrance * 60), new Percentage(100));
            return;
        }

        using var boosted = (MagickImage)image.Clone();
        boosted.Modulate(new Percentage(100), new Percentage(100 + vibrance * 100), new Percentage(100));

        using var hsl = (MagickImage)image.Clone();
        hsl.ColorSpace = ColorSpace.HSL;
        var channels = hsl.Separate(Channels.Green);
        try
        {
            using var saturationChannel = (MagickImage)channels.First();

            // Bright mask = low existing saturation = full effect.
            saturationChannel.Negate();
            saturationChannel.Alpha(AlphaOption.Off);

            boosted.Alpha(AlphaOption.Set);
            boosted.Composite(saturationChannel, CompositeOperator.CopyAlpha);
            image.Composite(boosted, CompositeOperator.Over);
            image.Alpha(AlphaOption.Off);
        }
        finally
        {
            foreach (var channel in channels.Skip(1)) channel.Dispose();
        }
    }

    // ---- stage 4: finish -----------------------------------------------------------------------

    private static void ApplyFinish(MagickImage image, ImageAdjustments a, CancellationToken ct)
    {
        if (a.Sharpen == 0 && a.Vignette == 0 && a.NoiseReduction == 0 && a.Clarity == 0 && !a.BlackAndWhite)
            return;
        ct.ThrowIfCancellationRequested();

        // Denoise first, always: sharpening or clarity applied to noise amplifies the noise, and no
        // ordering the user can choose in the panel changes this — the stage order is the contract.
        if (a.NoiseReduction > 0)
        {
            // Wavelet denoise thresholds in quantum percent. 6% at full strength cleans high-ISO
            // chroma mush without turning faces to plastic; the softness term feathers the threshold
            // so the transition into detail is not a hard edge.
            image.WaveletDenoise(new Percentage(a.NoiseReduction * 6.0), a.NoiseReduction * 0.5);
            ct.ThrowIfCancellationRequested();
        }

        if (a.Clarity != 0)
        {
            // Local contrast at a radius scaled to the image, so the same parameter gives the same
            // look at 256 px, 1024 px and export size — the replay guarantee this whole class exists
            // for. Below ~4 px radius the operator turns into sharpening, hence the floor.
            var radius = Math.Max(4.0, Math.Max(image.Width, image.Height) / 100.0);
            image.LocalContrast(radius, new Percentage(a.Clarity * 45.0));
            ct.ThrowIfCancellationRequested();
        }

        if (a.BlackAndWhite)
        {
            image.Grayscale(PixelIntensityMethod.Rec709Luminance);
            image.ColorSpace = ColorSpace.sRGB;
            image.BrightnessContrast(new Percentage(0), new Percentage(8));
        }

        if (a.Sharpen > 0)
        {
            // A 1 px radius is the right scale for a 1024 px preview and a 300 DPI print alike, and the
            // small kernel is materially cheaper than letting the sigma choose. The threshold keeps
            // sharpening off flat areas so noise and sky do not crawl. Amount tops out firm, not crunchy.
            image.UnsharpMask(1, 0.6, a.Sharpen * 1.5, 0.02);
        }

        if (a.Vignette > 0)
        {
            // ImageMagick's own Vignette blurs a rounded-rectangle mask and costs seconds at preview
            // size on this single-threaded build; a radial gradient multiplied in is the same look for
            // a twentieth of the time.
            using var mask = new MagickImage("radial-gradient:white-black", image.Width, image.Height);
            var floor = (byte)Math.Clamp(Math.Round(255 * (1 - a.Vignette)), 0, 255);
            mask.InverseLevelColors(new MagickColor(floor, floor, floor), MagickColors.White);
            mask.Alpha(AlphaOption.Off);
            image.Composite(mask, CompositeOperator.Multiply, Channels.RGB);
        }
    }
}
