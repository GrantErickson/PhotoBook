namespace PhotoBook.Core.Model;

/// <summary>
/// The shared crop arithmetic of kernel §4 — the single implementation the Engine (doc 08 §8),
/// the renderer (doc 12) and the editor (doc 09 §3.3) all call, so automatic and manual crops can
/// never drift apart.
/// <para>
/// Units: <em>slot</em> dimensions are page units (inches, points, screen pixels — any consistent
/// unit); <em>image</em> dimensions are pixels. Only ratios matter, so the two need not agree.
/// </para>
/// </summary>
public static class CropMath
{
    /// <summary>
    /// <c>coverScale = max(slotW/imgW, slotH/imgH)</c> (kernel §4) — the scale, in page units per
    /// image pixel, at which the image exactly covers the slot with the minimal possible crop.
    /// </summary>
    public static double CoverScale(double slotW, double slotH, double imageW, double imageH)
    {
        RequirePositive(slotW, nameof(slotW));
        RequirePositive(slotH, nameof(slotH));
        RequirePositive(imageW, nameof(imageW));
        RequirePositive(imageH, nameof(imageH));
        return Math.Max(slotW / imageW, slotH / imageH);
    }

    /// <summary>
    /// Effective scale <c>zoom × coverScale</c> (kernel §4): page units per image pixel at which
    /// the image is actually drawn.
    /// </summary>
    public static double EffectiveScale(CropState crop, double slotW, double slotH, double imageW, double imageH) =>
        crop.Zoom * CoverScale(slotW, slotH, imageW, imageH);

    /// <summary>
    /// Clamps <paramref name="zoom"/> into the editor's legal range
    /// <c>[<see cref="CropState.MinZoom"/>, <see cref="CropState.MaxZoom"/>]</c> (doc 09 §3.3).
    /// </summary>
    public static double ClampZoom(double zoom) =>
        double.IsNaN(zoom) ? 1.0 : Math.Clamp(zoom, CropState.MinZoom, CropState.MaxZoom);

    /// <summary>
    /// The maximum absolute pan on each axis, in slot-width / slot-height units.
    /// <para>
    /// While <c>zoom ≥ 1</c> the displayed image is at least as large as the slot on both axes and
    /// the limit is the gap-free bound — pan any further and the page background would appear at a
    /// slot edge. While <c>zoom &lt; 1</c> the image is smaller than the slot and the limit keeps it
    /// entirely inside the slot rect (doc 09 §3.3). Both regimes are the same expression,
    /// <c>|displayed/slot − 1| / 2</c>, evaluated per axis.
    /// </para>
    /// </summary>
    /// <param name="zoom">The crop zoom.</param>
    /// <param name="slotAspect">Slot width / height, in page units.</param>
    /// <param name="imageAspect">Image width / height, in pixels.</param>
    public static (double MaxOffsetX, double MaxOffsetY) OffsetLimits(double zoom, double slotAspect, double imageAspect)
    {
        RequirePositive(slotAspect, nameof(slotAspect));
        RequirePositive(imageAspect, nameof(imageAspect));

        // displayedW / slotW and displayedH / slotH, expressed with aspects only:
        //   coverScale·imgW/slotW = max(1, A_img / A_slot),  coverScale·imgH/slotH = max(1, A_slot / A_img).
        var ratioX = zoom * Math.Max(1.0, imageAspect / slotAspect);
        var ratioY = zoom * Math.Max(1.0, slotAspect / imageAspect);
        return (Math.Abs(ratioX - 1.0) / 2.0, Math.Abs(ratioY - 1.0) / 2.0);
    }

    /// <summary>
    /// Returns <paramref name="crop"/> with its zoom clamped to the legal range and its offsets
    /// clamped per <see cref="OffsetLimits"/> — no gap while <c>zoom ≥ 1</c>, image kept inside the
    /// slot while <c>zoom &lt; 1</c> (kernel §4, doc 03 invariant 10).
    /// </summary>
    /// <param name="crop">The crop to clamp.</param>
    /// <param name="slotAspect">Slot width / height, in page units.</param>
    /// <param name="imageAspect">Image width / height, in pixels.</param>
    public static CropState Clamp(CropState crop, double slotAspect, double imageAspect)
    {
        var zoom = ClampZoom(crop.Zoom);
        var (maxX, maxY) = OffsetLimits(zoom, slotAspect, imageAspect);
        var offsetX = double.IsNaN(crop.OffsetX) ? 0 : Math.Clamp(crop.OffsetX, -maxX, maxX);
        var offsetY = double.IsNaN(crop.OffsetY) ? 0 : Math.Clamp(crop.OffsetY, -maxY, maxY);
        return new CropState(zoom, offsetX, offsetY);
    }

    /// <summary>
    /// Convenience overload of <see cref="Clamp(CropState, double, double)"/> taking concrete slot
    /// and image extents instead of aspects.
    /// </summary>
    public static CropState Clamp(CropState crop, double slotW, double slotH, double imageW, double imageH)
    {
        RequirePositive(slotW, nameof(slotW));
        RequirePositive(slotH, nameof(slotH));
        RequirePositive(imageW, nameof(imageW));
        RequirePositive(imageH, nameof(imageH));
        return Clamp(crop, slotW / slotH, imageW / imageH);
    }

    /// <summary>
    /// The rectangle of the source image, <b>in image pixels</b>, that fills the slot — what a
    /// renderer samples.
    /// <para>
    /// While <c>zoom ≥ 1</c> (after <see cref="Clamp(CropState, double, double)"/>) the result lies
    /// inside the image. While <c>zoom &lt; 1</c> it is deliberately larger than the image on at
    /// least one axis: the surplus is the letterbox where the page background shows through (R9).
    /// Renderers that cannot sample outside the bitmap should use
    /// <see cref="ImageRect(CropState, double, double, Rect)"/> to place the whole image instead.
    /// </para>
    /// </summary>
    public static Rect SourceRect(CropState crop, double imageW, double imageH, double slotW, double slotH)
    {
        var scale = EffectiveScale(crop, slotW, slotH, imageW, imageH);
        var visibleW = slotW / scale;                       // source pixels spanned by the slot
        var visibleH = slotH / scale;
        var centerX = imageW / 2.0 - crop.OffsetX * slotW / scale;
        var centerY = imageH / 2.0 - crop.OffsetY * slotH / scale;
        return Rect.FromCenter(centerX, centerY, visibleW, visibleH);
    }

    /// <summary>
    /// <see cref="SourceRect"/> expressed in normalized image coordinates <c>[0,1] × [0,1]</c> —
    /// the space focus regions live in, so crop and focus can be compared directly.
    /// </summary>
    public static Rect SourceRectNormalized(CropState crop, double imageW, double imageH, double slotW, double slotH)
    {
        var r = SourceRect(crop, imageW, imageH, slotW, slotH);
        return new Rect(r.X / imageW, r.Y / imageH, r.W / imageW, r.H / imageH);
    }

    /// <summary>
    /// Where the <b>whole</b> image lands on the page, in the slot's own units — the destination
    /// rect for a draw-the-entire-bitmap renderer. It equals the slot when <c>zoom = 1</c> and the
    /// aspects match, overflows the slot (and is clipped to it) when the image is cropped, and sits
    /// inside the slot when <c>zoom &lt; 1</c>, leaving the letterbox that shows the page
    /// background (R9).
    /// </summary>
    /// <param name="crop">The placement's crop.</param>
    /// <param name="imageW">Image width in pixels.</param>
    /// <param name="imageH">Image height in pixels.</param>
    /// <param name="slot">The slot rect in page units.</param>
    public static Rect ImageRect(CropState crop, double imageW, double imageH, Rect slot)
    {
        var scale = EffectiveScale(crop, slot.W, slot.H, imageW, imageH);
        var drawnW = imageW * scale;
        var drawnH = imageH * scale;
        var centerX = slot.CenterX + crop.OffsetX * slot.W;
        var centerY = slot.CenterY + crop.OffsetY * slot.H;
        return Rect.FromCenter(centerX, centerY, drawnW, drawnH);
    }

    /// <summary>
    /// The maximal (minimal-crop) window of the slot's aspect inside the unit image square, in
    /// normalized image coordinates (doc 08 §8 step 2): with <c>a = A_slot / A_img</c>,
    /// <c>maxW = min(1, a)</c> and <c>maxH = maxW / a</c>. This window is exactly the
    /// <c>zoom = 1.0</c> cover fit.
    /// </summary>
    /// <param name="slotAspect">Slot width / height, in page units.</param>
    /// <param name="imageAspect">Image width / height, in pixels.</param>
    public static (double Width, double Height) MaximalWindow(double slotAspect, double imageAspect)
    {
        RequirePositive(slotAspect, nameof(slotAspect));
        RequirePositive(imageAspect, nameof(imageAspect));
        var a = slotAspect / imageAspect;
        var w = Math.Min(1.0, a);
        return (w, w / a);
    }

    /// <summary>
    /// Turns a crop window chosen in normalized image coordinates into the canonical
    /// <see cref="CropState"/> (doc 08 §8 step 4). A centered window yields zero offsets; a window
    /// right of center yields a negative <see cref="CropState.OffsetX"/> — the image slides left so
    /// the focus stays visible. The result is clamped by the shared no-gap rule.
    /// </summary>
    /// <param name="window">The crop window in normalized image coordinates.</param>
    /// <param name="imageW">Image width in pixels.</param>
    /// <param name="imageH">Image height in pixels.</param>
    /// <param name="slotW">Slot width in page units.</param>
    /// <param name="slotH">Slot height in page units.</param>
    public static CropState FromWindow(Rect window, double imageW, double imageH, double slotW, double slotH)
    {
        if (!window.IsWellFormed) throw new ArgumentException("Crop window must be well formed.", nameof(window));
        RequirePositive(imageW, nameof(imageW));
        RequirePositive(imageH, nameof(imageH));

        var (maxW, _) = MaximalWindow(slotW / slotH, imageW / imageH);
        var zoom = maxW / window.W;
        var scale = zoom * CoverScale(slotW, slotH, imageW, imageH);
        var offsetX = (0.5 - window.CenterX) * (scale * imageW) / slotW;
        var offsetY = (0.5 - window.CenterY) * (scale * imageH) / slotH;
        return Clamp(new CropState(zoom, offsetX, offsetY), slotW / slotH, imageW / imageH);
    }

    /// <summary>
    /// Effective print resolution of a placement, in dots per inch — the input to the preflight
    /// "effective DPI &lt; 200" check (doc 12).
    /// </summary>
    /// <param name="crop">The placement's crop.</param>
    /// <param name="imageW">Image width in pixels.</param>
    /// <param name="imageH">Image height in pixels.</param>
    /// <param name="slotWidthIn">Physical slot width in inches.</param>
    /// <param name="slotHeightIn">Physical slot height in inches.</param>
    public static double EffectiveDpi(CropState crop, double imageW, double imageH, double slotWidthIn, double slotHeightIn)
    {
        var scale = EffectiveScale(crop, slotWidthIn, slotHeightIn, imageW, imageH); // inches per pixel
        return scale <= 0 ? 0 : 1.0 / scale;
    }

    private static void RequirePositive(double value, string name)
    {
        if (!(value > 0) || !double.IsFinite(value))
            throw new ArgumentOutOfRangeException(name, value, "Must be a finite positive number.");
    }
}
