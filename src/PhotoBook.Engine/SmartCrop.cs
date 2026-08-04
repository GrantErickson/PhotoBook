using PhotoBook.Core.Model;

namespace PhotoBook.Engine;

/// <summary>The outcome of phase 6 for one placement.</summary>
/// <param name="Crop">The emitted <see cref="CropState"/> — the very value the user hand-tweaks (R9).</param>
/// <param name="FocusClipped">The primary Focus Region did not fit the maximal crop window (doc 08 §8 step 3).</param>
/// <param name="FaceNearGutter">A face could not be cleared of the gutter caution zone or safe margin.</param>
public readonly record struct CropResult(CropState Crop, bool FocusClipped, bool FaceNearGutter);

/// <summary>
/// Phase 6 of doc 08 — smart crop. Implements kernel §4 verbatim: choose the maximal crop window of
/// the slot's aspect that contains the primary Focus Region, then express it as a
/// <see cref="CropState"/> through <see cref="CropMath"/>. Automatic and manual crops are therefore
/// one representation, and a user tweak is literally a small edit to engine output (R9, R25).
/// </summary>
public static class SmartCrop
{
    /// <summary>Nominal pixel size used when a photo's dimensions are unknown (first-import race, doc 08 §12).</summary>
    public const double FallbackPixels = 1000.0;

    /// <summary>
    /// Step 1 — the primary Focus Region: the highest-priority, highest-weight region with every
    /// overlapping region merged into it (bounding-box union). A photo with no regions falls back to
    /// the centered rect of half the image's width and height, which is also the M0/unanalyzed
    /// behavior (doc 08 §8, §12).
    /// </summary>
    public static Rect PrimaryFocus(Photo photo)
    {
        ArgumentNullException.ThrowIfNull(photo);
        var regions = OrderedRegions(photo);
        if (regions.Count == 0) return new Rect(0.25, 0.25, 0.5, 0.5);

        var primary = Clamp01(regions[0].Rect);
        for (var i = 1; i < regions.Count; i++)
        {
            var candidate = Clamp01(regions[i].Rect);
            if (primary.Intersects(candidate)) primary = primary.Union(candidate);
        }

        return Clamp01(primary);
    }

    /// <summary>Focus regions in fusion order: kind priority, then weight, then a geometric total order.</summary>
    public static IReadOnlyList<FocusRegion> OrderedRegions(Photo photo)
    {
        ArgumentNullException.ThrowIfNull(photo);
        return photo.FocusRegions
            .Where(r => r.Rect.IsWellFormed)
            .OrderBy(r => r.KindPriority)
            .ThenByDescending(r => r.Weight)
            .ThenBy(r => r.Rect.X)
            .ThenBy(r => r.Rect.Y)
            .ThenBy(r => r.Rect.W)
            .ThenBy(r => r.Rect.H)
            .ToList();
    }

    /// <summary>The weight-weighted centroid of every region; the image center when there are none.</summary>
    public static (double X, double Y) WeightedCentroid(Photo photo)
    {
        ArgumentNullException.ThrowIfNull(photo);
        var totalWeight = 0.0;
        var x = 0.0;
        var y = 0.0;
        foreach (var region in photo.FocusRegions)
        {
            if (!region.Rect.IsWellFormed) continue;
            var weight = region.Weight > 0 ? region.Weight : 1e-6;
            totalWeight += weight;
            x += region.Rect.CenterX * weight;
            y += region.Rect.CenterY * weight;
        }

        if (totalWeight <= 0)
        {
            var focus = PrimaryFocus(photo);
            return (focus.CenterX, focus.CenterY);
        }

        return (x / totalWeight, y / totalWeight);
    }

    /// <summary>The photo's native aspect, with a square fallback when dimensions are unknown.</summary>
    public static double AspectOf(Photo photo)
    {
        ArgumentNullException.ThrowIfNull(photo);
        var aspect = photo.Aspect;
        return double.IsFinite(aspect) && aspect > 0 ? aspect : 1.0;
    }

    /// <summary>
    /// <c>focusRisk</c> of doc 08 §7: the fraction of the primary Focus Region's area that cannot be
    /// kept visible at <c>zoom = 1</c> under the best pan. Because the maximal window may be panned
    /// freely on each axis inside the image, the best achievable overlap is separable, so this is
    /// exact rather than a search.
    /// </summary>
    /// <param name="photo">The candidate photo.</param>
    /// <param name="slotAspect">The slot's physical width/height.</param>
    public static double FocusRisk(Photo photo, double slotAspect)
    {
        ArgumentNullException.ThrowIfNull(photo);
        if (!(slotAspect > 0) || !double.IsFinite(slotAspect)) return 0;

        var focus = PrimaryFocus(photo);
        if (focus.Area <= 0) return 0;

        var (windowW, windowH) = CropMath.MaximalWindow(slotAspect, AspectOf(photo));
        var visible = Math.Min(focus.W, windowW) * Math.Min(focus.H, windowH);
        var risk = 1.0 - visible / focus.Area;
        return Math.Clamp(risk, 0, 1);
    }

    /// <summary>
    /// Steps 2–4 of doc 08 §8: derive the maximal crop window, position it over the Focus Regions,
    /// nudge faces out of the gutter caution zone and the safe margin, and emit the
    /// <see cref="CropState"/>. The engine never emits <c>zoom &lt; 1</c> (no letterbox — that is a
    /// choice the user makes by hand) and never above
    /// <see cref="LayoutWeights.MaxEmittedZoom"/>.
    /// </summary>
    /// <param name="photo">The photo being placed.</param>
    /// <param name="slot">The slot, already oriented for the page side (mirrored where applicable).</param>
    /// <param name="side">Which side of the Spread the page falls on — decides where the gutter is.</param>
    /// <param name="trimWidthIn">Page trim width, inches.</param>
    /// <param name="trimHeightIn">Page trim height, inches.</param>
    /// <param name="weights">Engine tunables.</param>
    /// <param name="spanWidthFactor">
    /// Multiplier on the slot's physical width for a gutter-spanning spread photo (R18): the crop is
    /// computed once over the virtual 22 × 8.5 in Spread canvas and each page renders its half.
    /// </param>
    public static CropResult Crop(
        Photo photo,
        ImageSlot slot,
        PageSide side,
        double trimWidthIn,
        double trimHeightIn,
        LayoutWeights weights,
        double spanWidthFactor = 1.0)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(weights);

        var imageW = photo.Width > 0 ? photo.Width : FallbackPixels;
        var imageH = photo.Height > 0 ? photo.Height : FallbackPixels;
        var imageAspect = imageW / imageH;

        var slotWidthIn = Math.Max(1e-6, slot.Rect.W * trimWidthIn * spanWidthFactor);
        var slotHeightIn = Math.Max(1e-6, slot.Rect.H * trimHeightIn);
        var slotAspect = slotWidthIn / slotHeightIn;

        var (windowW, windowH) = CropMath.MaximalWindow(slotAspect, imageAspect);
        var focus = PrimaryFocus(photo);
        var clipped = false;

        double cx, cy;
        double loX, hiX;

        if (focus.W <= windowW && focus.H <= windowH)
        {
            loX = Math.Max(windowW / 2.0, focus.X + focus.W - windowW / 2.0);
            hiX = Math.Min(1 - windowW / 2.0, focus.X + windowW / 2.0);
            var loY = Math.Max(windowH / 2.0, focus.Y + focus.H - windowH / 2.0);
            var hiY = Math.Min(1 - windowH / 2.0, focus.Y + windowH / 2.0);

            if (loX > hiX) (loX, hiX) = Center(windowW);
            if (loY > hiY) (loY, hiY) = Center(windowH);

            var (centroidX, centroidY) = WeightedCentroid(photo);
            cx = Math.Clamp(centroidX, loX, hiX);
            cy = Math.Clamp(centroidY, loY, hiY);
        }
        else
        {
            // The focus is larger than the maximal window on some axis: keep zoom = 1 and center on
            // the focus centroid (doc 08 §8 step 3). Already penalized through focusRisk in §7.
            clipped = true;
            (loX, hiX) = Center(windowW);
            var (loY, hiY) = Center(windowH);
            cx = Math.Clamp(focus.CenterX, loX, hiX);
            cy = Math.Clamp(focus.CenterY, loY, hiY);
        }

        var faceNearGutter = false;
        if (slot.Bleed || slot.SpanId is not null || TouchesTrimEdge(slot.Rect))
        {
            (cx, faceNearGutter) = NudgeForGutterAndSafeArea(photo, slot, side, cx, windowW, loX, hiX, spanWidthFactor);
        }

        var window = new Rect(cx - windowW / 2.0, cy - windowH / 2.0, windowW, windowH);
        var crop = CropMath.FromWindow(window, imageW, imageH, slotWidthIn, slotHeightIn);

        // The engine's contract: exactly the minimal-crop cover fit, never a letterbox (doc 08 §8).
        var zoom = Math.Clamp(crop.Zoom, weights.EmittedZoom, weights.MaxEmittedZoom);
        crop = CropMath.Clamp(crop with { Zoom = zoom }, slotAspect, imageAspect);

        return new CropResult(crop, clipped, faceNearGutter);
    }

    private static (double Lo, double Hi) Center(double extent)
    {
        var half = extent / 2.0;
        var lo = Math.Min(half, 1 - half);
        var hi = Math.Max(half, 1 - half);
        return (lo, hi);
    }

    private static bool TouchesTrimEdge(Rect rect) =>
        rect.X <= 1e-9 || rect.Y <= 1e-9 || rect.Right >= 1 - 1e-9 || rect.Bottom >= 1 - 1e-9;

    /// <summary>
    /// The gutter and safe-area nudge of doc 08 §8: map every <c>face</c>/<c>person</c> region into
    /// page space and shift the window horizontally by the smallest amount that clears the 0.5 in
    /// gutter caution zone and the 0.375 in safe margin. When no shift clears every face, take the
    /// one that minimizes the worst violation and report <c>FaceNearGutter</c>.
    /// </summary>
    private static (double CenterX, bool Violated) NudgeForGutterAndSafeArea(
        Photo photo, ImageSlot slot, PageSide side, double cx, double windowW,
        double loX, double hiX, double spanWidthFactor)
    {
        var faces = photo.FocusRegions
            .Where(r => r.Kind is FocusKind.Face or FocusKind.Person && r.Rect.IsWellFormed)
            .OrderBy(r => r.Rect.X).ThenBy(r => r.Rect.Y)
            .ToList();
        if (faces.Count == 0 || windowW <= 0) return (cx, false);

        // Page-space limits: the gutter side is tighter than the safe margin.
        var gutter = PageGeometry.GutterCautionNormalizedX;
        var safe = PageGeometry.SafeMarginNormalizedX;
        var left = side == PageSide.Right ? gutter : safe;
        var right = side == PageSide.Right ? 1 - safe : 1 - gutter;

        var slotWidth = slot.Rect.W * spanWidthFactor;
        if (!(slotWidth > 0)) return (cx, false);
        var scale = slotWidth / windowW;                 // page units per normalized image unit

        if (slot.SpanId is not null)
        {
            // A gutter-spanning photo works in Spread space: it accepts center loss by design, so the
            // rule is "keep faces off the centerline", not "keep them away from one page edge".
            return NudgeAcrossGutter(faces, slot, cx, windowW, loX, hiX, scale, slotWidth, gutter);
        }

        var deltaLow = loX - cx;
        var deltaHigh = hiX - cx;
        var worst = 0.0;

        foreach (var face in faces)
        {
            var u0 = Math.Max(face.Rect.X, cx - windowW / 2.0);
            var u1 = Math.Min(face.Rect.Right, cx + windowW / 2.0);
            if (u1 <= u0) continue;                      // not visible in the window

            var a = slot.Rect.X + (u0 - (cx - windowW / 2.0)) * scale;
            var b = slot.Rect.X + (u1 - (cx - windowW / 2.0)) * scale;

            // Shifting the window center by δ moves the face by −δ·scale in page space.
            var upper = (a - left) / scale;              // δ ≤ upper keeps the face right of `left`
            var lower = (b - right) / scale;             // δ ≥ lower keeps the face left of `right`
            if (lower > upper)
            {
                // The face is wider than the printable band; it can never be fully cleared.
                worst = Math.Max(worst, lower - upper);
                continue;
            }

            deltaLow = Math.Max(deltaLow, lower);
            deltaHigh = Math.Min(deltaHigh, upper);
        }

        if (deltaLow > deltaHigh)
        {
            // No shift clears every face: minimize the violation by taking the midpoint of the
            // conflicting requirements, clamped into the legal window interval.
            var midpoint = (deltaLow + deltaHigh) / 2.0;
            var delta = Math.Clamp(midpoint, loX - cx, hiX - cx);
            return (Math.Clamp(cx + delta, loX, hiX), true);
        }

        var chosen = Math.Clamp(0, deltaLow, deltaHigh);  // the smallest shift that clears everything
        return (Math.Clamp(cx + chosen, loX, hiX), worst > 0);
    }

    /// <summary>
    /// The gutter rule for a photo spanning the Spread (R18, kernel §3): every visible face should
    /// end up wholly on one side of the 0.5 in caution band around the centerline. The smallest shift
    /// that achieves that wins; when the faces straddle the centerline too widely to be moved off it,
    /// the least-bad position is kept and <c>FaceNearGutter</c> is reported.
    /// </summary>
    private static (double CenterX, bool Violated) NudgeAcrossGutter(
        List<FocusRegion> faces, ImageSlot slot, double cx, double windowW,
        double loX, double hiX, double scale, double slotWidth, double gutter)
    {
        var centerline = slot.Rect.X + slotWidth / 2.0;

        var unionLow = double.PositiveInfinity;
        var unionHigh = double.NegativeInfinity;
        foreach (var face in faces)
        {
            var u0 = Math.Max(face.Rect.X, cx - windowW / 2.0);
            var u1 = Math.Min(face.Rect.Right, cx + windowW / 2.0);
            if (u1 <= u0) continue;

            unionLow = Math.Min(unionLow, slot.Rect.X + (u0 - (cx - windowW / 2.0)) * scale);
            unionHigh = Math.Max(unionHigh, slot.Rect.X + (u1 - (cx - windowW / 2.0)) * scale);
        }

        if (double.IsPositiveInfinity(unionLow)) return (cx, false);
        if (unionHigh <= centerline - gutter || unionLow >= centerline + gutter) return (cx, false);

        // Push the whole face union to the left of the band, or to the right of it: pick the smaller move.
        var toLeft = (unionHigh - (centerline - gutter)) / scale;     // δ ≥ toLeft moves faces left
        var toRight = (unionLow - (centerline + gutter)) / scale;     // δ ≤ toRight moves faces right
        var lowerBound = loX - cx;
        var upperBound = hiX - cx;

        var candidates = new[] { toLeft, toRight };
        var bestDelta = double.NaN;
        foreach (var candidate in candidates)
        {
            if (candidate < lowerBound || candidate > upperBound) continue;
            if (double.IsNaN(bestDelta) || Math.Abs(candidate) < Math.Abs(bestDelta)) bestDelta = candidate;
        }

        if (double.IsNaN(bestDelta))
        {
            var fallback = Math.Clamp(Math.Abs(toLeft) <= Math.Abs(toRight) ? toLeft : toRight, lowerBound, upperBound);
            return (Math.Clamp(cx + fallback, loX, hiX), true);
        }

        return (Math.Clamp(cx + bestDelta, loX, hiX), false);
    }

    private static Rect Clamp01(Rect rect)
    {
        var x = Math.Clamp(rect.X, 0, 1);
        var y = Math.Clamp(rect.Y, 0, 1);
        var right = Math.Clamp(rect.Right, 0, 1);
        var bottom = Math.Clamp(rect.Bottom, 0, 1);
        var w = Math.Max(1e-6, right - x);
        var h = Math.Max(1e-6, bottom - y);
        if (x + w > 1) x = Math.Max(0, 1 - w);
        if (y + h > 1) y = Math.Max(0, 1 - h);
        return new Rect(x, y, w, h);
    }
}
