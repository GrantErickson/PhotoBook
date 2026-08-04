using PhotoBook.Core.Model;

namespace PhotoBook.Tests;

/// <summary>
/// Kernel §4 crop arithmetic, tested per doc 13 "Crop-math unit tests": hand-computed
/// <c>coverScale</c> cases, the <c>zoom = 1</c> cover fit, gap-free clamping at <c>zoom ≥ 1</c>,
/// letterboxing at <c>zoom &lt; 1</c> (R9), window round-trips, and degenerate inputs.
/// </summary>
public class CropMathTests
{
    private const double Tol = 1e-9;

    // ------------------------------------------------------------ coverScale

    [Fact]
    public void CoverScale_LandscapeImageInPortraitSlot_IsHeightBound()
    {
        // slot 400 × 600 (portrait), image 1600 × 900 (landscape).
        // max(400/1600, 600/900) = max(0.25, 0.666…) = 2/3.
        var cover = CropMath.CoverScale(400, 600, 1600, 900);

        Assert.Equal(2.0 / 3.0, cover, Tol);
    }

    [Fact]
    public void CoverScale_PortraitImageInLandscapeSlot_IsWidthBound()
    {
        // slot 600 × 400 (landscape), image 900 × 1600 (portrait).
        // max(600/900, 400/1600) = max(0.666…, 0.25) = 2/3.
        var cover = CropMath.CoverScale(600, 400, 900, 1600);

        Assert.Equal(2.0 / 3.0, cover, Tol);
    }

    [Fact]
    public void CoverScale_ExactAspectMatch_IsTheUniformScale()
    {
        // slot 800 × 600, image 1600 × 1200: both ratios are 0.5, so cover is exactly 0.5.
        var cover = CropMath.CoverScale(800, 600, 1600, 1200);

        Assert.Equal(0.5, cover, Tol);
    }

    [Theory]
    [InlineData(0, 600, 1600, 900)]
    [InlineData(400, 0, 1600, 900)]
    [InlineData(400, 600, -1, 900)]
    [InlineData(400, 600, 1600, double.NaN)]
    public void CoverScale_RejectsNonPositiveOrNonFiniteExtents(double sw, double sh, double iw, double ih) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CropMath.CoverScale(sw, sh, iw, ih));

    // ------------------------------------------------------ zoom = 1 cover fit

    [Fact]
    public void ZoomOne_LandscapeInPortrait_CoversSlotAndOverflowsWidthOnly()
    {
        var slot = new Rect(0, 0, 400, 600);
        var image = CropMath.ImageRect(CropState.Default, 1600, 900, slot);

        // scale = 2/3 ⇒ drawn 1066.66… × 600: the height matches exactly, the width overflows.
        Assert.Equal(1600 * 2.0 / 3.0, image.W, 1e-6);
        Assert.Equal(600, image.H, 1e-6);
        Assert.True(Covers(image, slot), "the zoom = 1 cover fit must leave no gap");
        Assert.Equal(slot.CenterX, image.CenterX, 1e-9);
        Assert.Equal(slot.CenterY, image.CenterY, 1e-9);
    }

    [Fact]
    public void ZoomOne_ExactAspectMatch_DrawsExactlyTheSlot()
    {
        var slot = new Rect(0.1, 0.2, 0.4, 0.3); // 4:3 in page units
        var image = CropMath.ImageRect(CropState.Default, 1600, 1200, slot);

        Assert.Equal(slot.X, image.X, 1e-9);
        Assert.Equal(slot.Y, image.Y, 1e-9);
        Assert.Equal(slot.W, image.W, 1e-9);
        Assert.Equal(slot.H, image.H, 1e-9);
    }

    [Fact]
    public void ZoomOne_SourceRectIsTheMinimalCropAndLiesInsideTheImage()
    {
        // slot 400 × 600, image 1600 × 900 ⇒ 600 × 900 source pixels, centered: x ∈ [500, 1100].
        var source = CropMath.SourceRect(CropState.Default, 1600, 900, 400, 600);

        Assert.Equal(500, source.X, 1e-6);
        Assert.Equal(0, source.Y, 1e-6);
        Assert.Equal(600, source.W, 1e-6);
        Assert.Equal(900, source.H, 1e-6);
        Assert.True(source.X >= -1e-9 && source.Y >= -1e-9 && source.Right <= 1600 + 1e-9 && source.Bottom <= 900 + 1e-9);
    }

    [Fact]
    public void MaximalWindow_IsTheZoomOneCoverFit()
    {
        // slot 400 × 600 over image 1600 × 900: a = (400/600)/(1600/900) = 0.375.
        var (w, h) = CropMath.MaximalWindow(400.0 / 600.0, 1600.0 / 900.0);

        Assert.Equal(0.375, w, Tol);
        Assert.Equal(1.0, h, Tol);

        var normalized = CropMath.SourceRectNormalized(CropState.Default, 1600, 900, 400, 600);
        Assert.Equal(w, normalized.W, Tol);
        Assert.Equal(h, normalized.H, Tol);
    }

    // ----------------------------------------------------- clamping, zoom ≥ 1

    [Fact]
    public void OffsetLimits_AtZoomOne_TheCoveredAxisHasNoPanFreedom()
    {
        // Landscape image in a portrait slot: the image is height-bound, so y cannot pan at all
        // and x may pan (2.666… − 1)/2 slot widths.
        var (maxX, maxY) = CropMath.OffsetLimits(1.0, 400.0 / 600.0, 1600.0 / 900.0);

        Assert.Equal((8.0 / 3.0 - 1.0) / 2.0, maxX, Tol);
        Assert.Equal(0.0, maxY, Tol);
    }

    [Fact]
    public void OffsetLimits_AtZoomOneWithExactAspectMatch_AreZeroOnBothAxes()
    {
        var (maxX, maxY) = CropMath.OffsetLimits(1.0, 4.0 / 3.0, 4.0 / 3.0);

        Assert.Equal(0.0, maxX, Tol);
        Assert.Equal(0.0, maxY, Tol);
    }

    [Theory]
    [MemberData(nameof(AspectGrid))]
    public void Clamp_AtZoomAtLeastOne_LeavesNoGap(double slotAspect, double imageAspect)
    {
        var slot = new Rect(0.05, 0.05, 0.4 * slotAspect, 0.4);
        foreach (var zoom in new[] { 1.0, 1.0000001, 1.25, 2.0, 4.0 })
        {
            foreach (var (ox, oy) in new[] { (0.0, 0.0), (5.0, 5.0), (-5.0, 5.0), (0.3, -0.2), (-9.9, -9.9) })
            {
                var clamped = CropMath.Clamp(new CropState(zoom, ox, oy), slot.W / slot.H, imageAspect);
                var drawn = CropMath.ImageRect(clamped, imageAspect * 1000, 1000, slot);

                Assert.True(Covers(drawn, slot),
                    $"gap at zoom {zoom}, offsets ({ox}, {oy}), slotAspect {slotAspect}, imageAspect {imageAspect}: " +
                    $"drawn {drawn} does not cover slot {slot}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(AspectGrid))]
    public void Clamp_IsIdempotent(double slotAspect, double imageAspect)
    {
        foreach (var zoom in new[] { 0.25, 0.5, 1.0, 1.7, 4.0, 99.0 })
        {
            var once = CropMath.Clamp(new CropState(zoom, 7.5, -7.5), slotAspect, imageAspect);
            var twice = CropMath.Clamp(once, slotAspect, imageAspect);

            Assert.Equal(once, twice);
        }
    }

    [Fact]
    public void Clamp_ClampsZoomIntoTheEditorRange()
    {
        Assert.Equal(CropState.MaxZoom, CropMath.Clamp(new CropState(99, 0, 0), 1.5, 1.5).Zoom, Tol);
        Assert.Equal(CropState.MinZoom, CropMath.Clamp(new CropState(0.001, 0, 0), 1.5, 1.5).Zoom, Tol);
    }

    // ------------------------------------------------------ zoom < 1 letterbox

    [Fact]
    public void ZoomBelowOne_LetterboxesInsideTheSlot()
    {
        var slot = new Rect(0, 0, 0.4, 0.3);
        var crop = new CropState(0.5, 0, 0);

        Assert.True(crop.IsLetterboxed);

        var drawn = CropMath.ImageRect(crop, 1600, 1200, slot); // exact aspect match ⇒ half-size draw
        Assert.Equal(slot.W / 2, drawn.W, 1e-9);
        Assert.Equal(slot.H / 2, drawn.H, 1e-9);
        Assert.True(Covers(slot, drawn), "at zoom < 1 the image must sit inside the slot");
        Assert.False(Covers(drawn, slot), "at zoom < 1 the background must show through");
    }

    [Fact]
    public void ZoomBelowOne_ClampKeepsTheWholeImageInsideTheSlot()
    {
        var slot = new Rect(0.1, 0.1, 0.4, 0.3);
        foreach (var zoom in new[] { 0.25, 0.5, 0.9 })
        {
            var clamped = CropMath.Clamp(new CropState(zoom, 9, -9), slot.W / slot.H, 4.0 / 3.0);
            var drawn = CropMath.ImageRect(clamped, 1600, 1200, slot);

            Assert.True(Covers(slot, drawn), $"at zoom {zoom} the drawn image {drawn} escaped the slot {slot}");
        }
    }

    [Fact]
    public void ZoomBelowOne_LimitPutsTheImageFlushWithTheSlotEdge()
    {
        // Exact aspect match at zoom 0.5: max offset is |0.5 − 1| / 2 = 0.25 slot widths, which lands
        // the half-width image exactly against the slot's right edge.
        var (maxX, maxY) = CropMath.OffsetLimits(0.5, 4.0 / 3.0, 4.0 / 3.0);
        Assert.Equal(0.25, maxX, Tol);
        Assert.Equal(0.25, maxY, Tol);

        var slot = new Rect(0, 0, 0.4, 0.3);
        var drawn = CropMath.ImageRect(new CropState(0.5, maxX, 0), 1600, 1200, slot);
        Assert.Equal(slot.Right, drawn.Right, 1e-9);
    }

    // ------------------------------------------------------------ round-trips

    [Fact]
    public void FromWindow_ThenSourceRectNormalized_ReproducesTheWindow()
    {
        double[] slotAspects = [0.6667, 1.0, 1.3333, 1.5, 2.31];
        (double W, double H)[] images = [(1600, 900), (900, 1600), (1200, 1200), (4000, 3000), (1024, 768)];
        Rect[] windows =
        [
            new(0.0, 0.0, 1.0, 1.0),
            new(0.1, 0.1, 0.8, 0.8),
            new(0.25, 0.25, 0.5, 0.5),
            new(0.0, 0.2, 0.6, 0.6),
            new(0.4, 0.0, 0.6, 0.55),
        ];

        var checkedCases = 0;
        foreach (var slotAspect in slotAspects)
        foreach (var (iw, ih) in images)
        foreach (var authored in windows)
        {
            const double slotH = 3.0;
            var slotW = slotAspect * slotH;

            // Fit the authored window to the slot's aspect and keep it inside the image — the shape
            // the smart-crop step actually produces (doc 08 §8).
            var window = FitToAspect(authored, slotAspect, iw, ih);
            var crop = CropMath.FromWindow(window, iw, ih, slotW, slotH);
            if (crop.Zoom >= CropState.MaxZoom || crop.Zoom <= CropState.MinZoom) continue; // clamped by design

            var back = CropMath.SourceRectNormalized(crop, iw, ih, slotW, slotH);

            Assert.Equal(window.X, back.X, 1e-9);
            Assert.Equal(window.Y, back.Y, 1e-9);
            Assert.Equal(window.W, back.W, 1e-9);
            Assert.Equal(window.H, back.H, 1e-9);
            checkedCases++;
        }

        Assert.True(checkedCases >= 100, $"the round-trip grid degenerated to {checkedCases} cases");
    }

    [Fact]
    public void FromWindow_OnTheMaximalWindow_YieldsTheDefaultCrop()
    {
        var (w, h) = CropMath.MaximalWindow(400.0 / 600.0, 1600.0 / 900.0);
        var crop = CropMath.FromWindow(Rect.FromCenter(0.5, 0.5, w, h), 1600, 900, 400, 600);

        Assert.Equal(1.0, crop.Zoom, 1e-9);
        Assert.Equal(0.0, crop.OffsetX, 1e-9);
        Assert.Equal(0.0, crop.OffsetY, 1e-9);
    }

    [Fact]
    public void FromWindow_RightOfCenter_PansTheImageLeft()
    {
        var (w, h) = CropMath.MaximalWindow(400.0 / 600.0, 1600.0 / 900.0);
        var crop = CropMath.FromWindow(new Rect(1 - w, 0.5 - h / 2, w, h), 1600, 900, 400, 600);

        Assert.True(crop.OffsetX < 0, "a focus region on the right edge slides the image left");
        Assert.Equal(0.0, crop.OffsetY, 1e-9);
    }

    // ------------------------------------------------------------- degenerate

    [Theory]
    [InlineData(1, 1)]
    [InlineData(10000, 1000)]     // 10:1
    [InlineData(1000, 10000)]     // 1:10
    public void DegenerateImages_ProduceFiniteSerializableCrops(double imageW, double imageH)
    {
        foreach (var zoom in new[] { CropState.MinZoom, 1.0, CropState.MaxZoom })
        {
            var crop = CropMath.Clamp(new CropState(zoom, 3, -3), 10.0, imageW / imageH);

            Assert.True(double.IsFinite(crop.Zoom) && double.IsFinite(crop.OffsetX) && double.IsFinite(crop.OffsetY));

            var source = CropMath.SourceRect(crop, imageW, imageH, 10, 1);
            Assert.True(double.IsFinite(source.X) && double.IsFinite(source.Y) &&
                        double.IsFinite(source.W) && double.IsFinite(source.H));
        }
    }

    [Fact]
    public void Clamp_TurnsNaNInputsIntoTheDefaultCrop()
    {
        var crop = CropMath.Clamp(new CropState(double.NaN, double.NaN, double.NaN), 1.5, 1.5);

        Assert.Equal(CropState.Default, crop);
    }

    [Fact]
    public void EffectiveDpi_IsPixelsPerInchOfTheSlot()
    {
        // A 3000 px wide photo cover-filling a 10 in × 7.5 in slot prints at 300 dpi.
        var dpi = CropMath.EffectiveDpi(CropState.Default, 3000, 2250, 10, 7.5);

        Assert.Equal(300, dpi, 1e-6);
    }

    // ---------------------------------------------------------------- helpers

    public static TheoryData<double, double> AspectGrid()
    {
        var data = new TheoryData<double, double>();
        foreach (var slot in new[] { 0.5, 0.75, 1.0, 1.3333, 2.5 })
        foreach (var image in new[] { 0.4, 0.75, 1.0, 1.5, 3.0 })
        {
            data.Add(slot, image);
        }

        return data;
    }

    /// <summary>True when <paramref name="outer"/> covers <paramref name="inner"/> to within slop.</summary>
    private static bool Covers(Rect outer, Rect inner)
    {
        const double slop = 1e-9;
        return outer.X <= inner.X + slop && outer.Y <= inner.Y + slop &&
               outer.Right >= inner.Right - slop && outer.Bottom >= inner.Bottom - slop;
    }

    /// <summary>
    /// Shrinks a window to the slot's aspect and nudges it back inside the unit image square — the
    /// invariant the smart-crop step maintains before calling <see cref="CropMath.FromWindow"/>.
    /// </summary>
    private static Rect FitToAspect(Rect window, double slotAspect, double imageW, double imageH)
    {
        var target = slotAspect / (imageW / imageH); // desired w/h in normalized image units
        var w = window.W;
        var h = window.H;
        if (w / h > target) w = h * target; else h = w / target;
        if (w > 1) { w = 1; h = w / target; }
        if (h > 1) { h = 1; w = h * target; }

        var x = Math.Clamp(window.CenterX - w / 2, 0, 1 - w);
        var y = Math.Clamp(window.CenterY - h / 2, 0, 1 - h);
        return new Rect(x, y, w, h);
    }
}
