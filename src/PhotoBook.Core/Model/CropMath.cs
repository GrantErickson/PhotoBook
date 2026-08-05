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
    /// The caption band the renderer reserves under a <see cref="CaptionPolicy.Below"/> photo that
    /// actually has a caption, in inches (doc 07). It comes out of the slot's height, so it is part
    /// of the crop's frame — see <see cref="PhotoBox"/>. Must agree with
    /// <c>PageRenderer.CaptionBandIn</c>; a test pins the two together.
    /// </summary>
    public const double CaptionBandIn = 0.30;

    /// <summary>
    /// How close to a trim edge a slot edge must lie, in normalized units, for the renderer to snap
    /// it outward to the bleed edge (doc 12 "Bleed extension"). Must agree with
    /// <c>PageGeometryMapper.BleedSnapTolerance</c>; a test pins the two together.
    /// </summary>
    public const double BleedSnapTolerance = 0.005;

    /// <summary>
    /// The rect a photo is actually drawn into for a slot, in inches — <b>the frame a
    /// <see cref="CropState"/> is expressed in</b>.
    /// <para>
    /// It is <em>not</em> the nominal <c>slot.Rect × trim</c> box. Two things move the edges before a
    /// pixel is drawn (doc 12): every edge lying within <see cref="BleedSnapTolerance"/> of a trim
    /// edge is extended out to the bleed box, and a <see cref="CaptionPolicy.Below"/> slot whose photo
    /// carries a caption gives up <see cref="CaptionBandIn"/> of its height (capped at half) to the
    /// caption. Both change the box's <em>aspect</em>, and an offset that is legal at one aspect is
    /// illegal at another — which is precisely how a photo with plenty of pixels ended up drawn short
    /// with the page background showing down one side.
    /// </para>
    /// <para>
    /// The engine (doc 08 §8) and the editor (doc 09 §3.3) both author and clamp against this box, so
    /// what they intend is what the renderer draws.
    /// </para>
    /// </summary>
    /// <param name="slotRect">The slot box in normalized page coordinates.</param>
    /// <param name="trimWidthIn">Page trim width, inches.</param>
    /// <param name="trimHeightIn">Page trim height, inches.</param>
    /// <param name="captionBelow">
    /// True when a caption will render below this photo — the slot's policy is
    /// <see cref="CaptionPolicy.Below"/> <em>and</em> the photo has caption text.
    /// </param>
    /// <param name="bleedIn">
    /// Bleed per outer edge, inches; pass <c>0</c> to skip the bleed extension (a gutter-spanning slot
    /// resolves its own panorama rect, doc 12 "Spreads").
    /// </param>
    /// <param name="spanWidthFactor">
    /// Multiplier on the slot's width for a gutter-spanning photo (R18): the crop is computed once
    /// over the virtual Spread canvas. Any value other than <c>1</c> suppresses the bleed extension.
    /// </param>
    public static (double WidthIn, double HeightIn) PhotoBox(
        Rect slotRect,
        double trimWidthIn,
        double trimHeightIn,
        bool captionBelow,
        double bleedIn = PageGeometry.BleedIn,
        double spanWidthFactor = 1.0)
    {
        RequirePositive(trimWidthIn, nameof(trimWidthIn));
        RequirePositive(trimHeightIn, nameof(trimHeightIn));

        var span = double.IsFinite(spanWidthFactor) && spanWidthFactor > 0 ? spanWidthFactor : 1.0;
        var bleed = double.IsFinite(bleedIn) && bleedIn > 0 ? bleedIn : 0.0;
        var pageWidthIn = trimWidthIn * span;

        var left = slotRect.X * pageWidthIn;
        var right = slotRect.Right * pageWidthIn;
        var top = slotRect.Y * trimHeightIn;
        var bottom = slotRect.Bottom * trimHeightIn;

        if (bleed > 0 && span == 1.0)
        {
            if (slotRect.X <= BleedSnapTolerance) left = -bleed;
            if (slotRect.Right >= 1 - BleedSnapTolerance) right = trimWidthIn + bleed;
            if (slotRect.Y <= BleedSnapTolerance) top = -bleed;
            if (slotRect.Bottom >= 1 - BleedSnapTolerance) bottom = trimHeightIn + bleed;

            // A rect authored past the trim edge is clamped to the bleed box, never beyond.
            left = Math.Max(left, -bleed);
            right = Math.Min(right, trimWidthIn + bleed);
            top = Math.Max(top, -bleed);
            bottom = Math.Min(bottom, trimHeightIn + bleed);
        }

        var widthIn = Math.Max(1e-6, right - left);
        var heightIn = Math.Max(1e-6, bottom - top);

        if (captionBelow)
        {
            heightIn = Math.Max(1e-6, heightIn - Math.Min(CaptionBandIn, heightIn / 2.0));
        }

        return (widthIn, heightIn);
    }

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
    /// Re-expresses a crop authored for one slot aspect against another — a swap into a differently
    /// shaped slot, a template change, a per-page slot resize (R15), or a caption appearing under a
    /// photo and shortening its box.
    /// <para>
    /// The zoom is kept (it is relative to the cover fit, so <c>1</c> stays the minimal crop in the
    /// new shape) and the offsets are re-scaled so the <em>same point of the image</em> stays at the
    /// centre of the frame. When the new aspect cannot honour that — the image is tighter on that
    /// axis there — the result is the nearest position that still covers, never a reset to centre.
    /// </para>
    /// </summary>
    /// <param name="crop">The crop as authored.</param>
    /// <param name="fromSlotAspect">The slot aspect it was authored for.</param>
    /// <param name="toSlotAspect">The slot aspect it is being used at.</param>
    /// <param name="imageAspect">Image width / height, in pixels.</param>
    public static CropState Rebase(CropState crop, double fromSlotAspect, double toSlotAspect, double imageAspect)
    {
        RequirePositive(fromSlotAspect, nameof(fromSlotAspect));
        RequirePositive(toSlotAspect, nameof(toSlotAspect));
        RequirePositive(imageAspect, nameof(imageAspect));

        var zoom = ClampZoom(crop.Zoom);
        var offsetX = double.IsNaN(crop.OffsetX) ? 0 : crop.OffsetX;
        var offsetY = double.IsNaN(crop.OffsetY) ? 0 : crop.OffsetY;

        // The window centre in normalized image coordinates is 0.5 − offset / ratio, so preserving it
        // is a straight rescale by the ratio of the two frames. Offsets are in slot units, and the
        // slot changed shape underneath them.
        var fromX = Math.Max(1.0, imageAspect / fromSlotAspect);
        var fromY = Math.Max(1.0, fromSlotAspect / imageAspect);
        var toX = Math.Max(1.0, imageAspect / toSlotAspect);
        var toY = Math.Max(1.0, toSlotAspect / imageAspect);

        return Clamp(
            new CropState(zoom, offsetX / fromX * toX, offsetY / fromY * toY),
            toSlotAspect,
            imageAspect);
    }

    /// <summary>
    /// The rectangle of the source image, <b>in image pixels</b>, that fills the slot — what a
    /// renderer samples.
    /// <para>
    /// While <c>zoom ≥ 1</c> the result lies inside the image. While <c>zoom &lt; 1</c> it is
    /// deliberately larger than the image on at least one axis: the surplus is the letterbox where the
    /// page background shows through (R9). Renderers that cannot sample outside the bitmap should use
    /// <see cref="ImageRect(CropState, double, double, Rect)"/> to place the whole image instead.
    /// </para>
    /// <para>
    /// <b>The frame is the arguments.</b> The crop is clamped to the slot given here before it is
    /// evaluated, so "no gap at <c>zoom ≥ 1</c>" is a property of this function rather than a promise
    /// every caller has to keep. A crop authored against a different aspect — a stale placement, a
    /// caption that appeared later, a hand-resized slot, an undo against changed geometry — is drawn
    /// at the nearest covering position instead of sampling off the edge of the bitmap and drawing
    /// short. Clamping is idempotent, so a correctly authored crop passes through untouched.
    /// </para>
    /// </summary>
    public static Rect SourceRect(CropState crop, double imageW, double imageH, double slotW, double slotH)
    {
        RequirePositive(slotW, nameof(slotW));
        RequirePositive(slotH, nameof(slotH));
        RequirePositive(imageW, nameof(imageW));
        RequirePositive(imageH, nameof(imageH));

        crop = Clamp(crop, slotW / slotH, imageW / imageH);
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
    /// <para>
    /// Like <see cref="SourceRect"/>, the crop is clamped to <paramref name="slot"/> first: the rect
    /// passed in <em>is</em> the frame, so the returned rect covers it whenever <c>zoom ≥ 1</c>.
    /// </para>
    /// </summary>
    /// <param name="crop">The placement's crop.</param>
    /// <param name="imageW">Image width in pixels.</param>
    /// <param name="imageH">Image height in pixels.</param>
    /// <param name="slot">The slot rect in page units.</param>
    public static Rect ImageRect(CropState crop, double imageW, double imageH, Rect slot)
    {
        RequirePositive(slot.W, nameof(slot));
        RequirePositive(slot.H, nameof(slot));
        RequirePositive(imageW, nameof(imageW));
        RequirePositive(imageH, nameof(imageH));

        crop = Clamp(crop, slot.W / slot.H, imageW / imageH);
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
