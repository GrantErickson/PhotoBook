using PhotoBook.Core.Model;
using PhotoBook.Engine;

namespace PhotoBook.Tests;

/// <summary>
/// The <em>crop frame</em> invariant behind R9 and doc 03 invariant 10: a <see cref="CropState"/> is
/// only meaningful against the rect the photo is actually drawn into, and at <c>zoom ≥ 1</c> the
/// drawn image must cover that rect exactly — no page background may show.
/// <para>
/// These tests exist because the product shipped a crop whose reference rect was ambiguous: the
/// engine and the editor clamped against the <em>nominal</em> slot box (<c>slot.Rect × trim</c>),
/// while <c>PageRenderer</c> draws into the box after the bleed extension and after the caption band
/// is reserved. The two boxes have different aspects, so an offset that was legal for the first was
/// illegal for the second and the photo drew short — a black band down one side of a photo that had
/// plenty of pixels to fill the frame.
/// </para>
/// </summary>
public class CropFrameTests
{
    private const double TrimW = PageGeometry.TrimWidthIn;
    private const double TrimH = PageGeometry.TrimHeightIn;

    // ================================================== the reported defect, end to end

    /// <summary>
    /// The reproduction. Template <c>t-04-notext-a</c> slot <c>s1</c>: a 0.476 × 0.468 slot on an
    /// 11 × 8.5 in page is 5.236 × 3.978 in (aspect 1.3162), but a captioned photo is drawn into
    /// 5.236 × 3.678 in (aspect 1.4236) because the renderer reserves the 0.30 in caption band. A
    /// 3:2 photo whose subject sits left of centre gets an offset that is legal at 1.3162 and
    /// illegal at 1.4236.
    /// </summary>
    [Fact]
    public void SmartCrop_OnACaptionedSlot_CoversTheRectTheRendererDrawsInto()
    {
        var slot = CaptionedSlot();
        var photo = PhotoWithFocus(3000, 2000, new Rect(0.02, 0.30, 0.26, 0.40));

        var crop = SmartCrop.Crop(photo, slot, PageSide.Right, TrimW, TrimH, LayoutWeights.Default).Crop;

        Assert.True(crop.Zoom >= 1.0, "the engine never emits a letterbox (doc 08 §8)");

        var drawn = DrawnBox(slot, captioned: true);
        var image = CropMath.ImageRect(crop, photo.Width, photo.Height, drawn);

        Assert.True(Covers(image, drawn),
            $"crop {crop} drew {image} into {drawn} — the page background shows through at zoom {crop.Zoom:0.###}");
    }

    /// <summary>
    /// The same defect seen from the editor's side: a crop authored for the nominal slot aspect is
    /// evaluated against the drawn aspect. This is the arithmetic core of the reproduction above and
    /// is deliberately free of engine heuristics.
    /// </summary>
    [Fact]
    public void ACropAuthoredForTheNominalSlot_StillCoversTheDrawnBox()
    {
        var slot = CaptionedSlot();
        var nominal = new Rect(0, 0, slot.Rect.W * TrimW, slot.Rect.H * TrimH);
        var drawn = DrawnBox(slot, captioned: true);

        const double imageW = 3000;
        const double imageH = 2000;

        // The largest pan the nominal box allows: exactly flush with the nominal slot edge.
        var (maxX, _) = CropMath.OffsetLimits(1.0, nominal.W / nominal.H, imageW / imageH);
        Assert.True(maxX > 0, "a 3:2 photo in a 1.316 slot has real horizontal pan freedom");

        var authored = CropMath.Clamp(new CropState(1.0, maxX, 0), nominal.W / nominal.H, imageW / imageH);
        var image = CropMath.ImageRect(authored, imageW, imageH, drawn);

        Assert.True(Covers(image, drawn),
            $"crop {authored} authored for aspect {nominal.W / nominal.H:0.####} left a gap in the " +
            $"drawn box (aspect {drawn.W / drawn.H:0.####}): {image} vs {drawn}");
    }

    /// <summary>
    /// The same for <see cref="CropMath.SourceRect"/> — the renderer samples this rect, and a rect
    /// hanging outside the bitmap is exactly what makes a photo draw short.
    /// </summary>
    [Fact]
    public void SourceRect_NeverLeavesTheImage_EvenWhenTheCropWasAuthoredForAnotherAspect()
    {
        var slot = CaptionedSlot();
        var nominalAspect = slot.Rect.W * TrimW / (slot.Rect.H * TrimH);
        var drawn = DrawnBox(slot, captioned: true);

        const double imageW = 3000;
        const double imageH = 2000;

        var (maxX, _) = CropMath.OffsetLimits(1.0, nominalAspect, imageW / imageH);
        var authored = new CropState(1.0, maxX, 0);

        var source = CropMath.SourceRect(authored, imageW, imageH, drawn.W, drawn.H);

        Assert.True(source.X >= -1e-6 && source.Y >= -1e-6 &&
                    source.Right <= imageW + 1e-6 && source.Bottom <= imageH + 1e-6,
            $"source rect {source} runs outside the {imageW} × {imageH} image; the renderer will draw short");
    }

    // ================================================== the frame is part of the model

    [Fact]
    public void PhotoBox_ReservesTheCaptionBandTheRendererReserves()
    {
        var slot = CaptionedSlot();

        var plain = CropMath.PhotoBox(slot.Rect, TrimW, TrimH, captionBelow: false, bleedIn: 0);
        var captioned = CropMath.PhotoBox(slot.Rect, TrimW, TrimH, captionBelow: true, bleedIn: 0);

        Assert.Equal(slot.Rect.W * TrimW, plain.WidthIn, 1e-9);
        Assert.Equal(slot.Rect.H * TrimH, plain.HeightIn, 1e-9);
        Assert.Equal(plain.WidthIn, captioned.WidthIn, 1e-9);
        Assert.Equal(plain.HeightIn - CropMath.CaptionBandIn, captioned.HeightIn, 1e-9);
    }

    [Fact]
    public void PhotoBox_NeverTakesMoreThanHalfTheSlotForTheCaption()
    {
        // A 0.4 in tall slot cannot give up 0.30 in: the renderer caps the band at half the height.
        var tiny = new Rect(0.1, 0.1, 0.3, 0.4 / TrimH);

        var box = CropMath.PhotoBox(tiny, TrimW, TrimH, captionBelow: true, bleedIn: 0);

        Assert.Equal(0.2, box.HeightIn, 1e-9);
    }

    [Fact]
    public void PhotoBox_ExtendsEveryTrimTouchingEdgeToTheBleedBox()
    {
        var fullBleed = new Rect(0, 0, 1, 1);

        var box = CropMath.PhotoBox(fullBleed, TrimW, TrimH, captionBelow: false);

        Assert.Equal(TrimW + (2 * PageGeometry.BleedIn), box.WidthIn, 1e-9);
        Assert.Equal(TrimH + (2 * PageGeometry.BleedIn), box.HeightIn, 1e-9);
    }

    /// <summary>
    /// <see cref="CropMath.PhotoBox"/> models the renderer's box, so the two constants it models must
    /// stay equal. If the renderer ever changes one of these, this test is the tripwire.
    /// </summary>
    [Fact]
    public void PhotoBox_ModelsTheSameConstantsTheRendererUses()
    {
        Assert.Equal(PhotoBook.Rendering.PageRenderer.CaptionBandIn, CropMath.CaptionBandIn, 12);
        Assert.Equal(PhotoBook.Rendering.PageGeometryMapper.BleedSnapTolerance, CropMath.BleedSnapTolerance, 12);
    }

    [Fact]
    public void PhotoBox_LeavesAnInteriorSlotAlone()
    {
        var interior = new Rect(0.2, 0.2, 0.5, 0.5);

        var box = CropMath.PhotoBox(interior, TrimW, TrimH, captionBelow: false);

        Assert.Equal(0.5 * TrimW, box.WidthIn, 1e-9);
        Assert.Equal(0.5 * TrimH, box.HeightIn, 1e-9);
    }

    // ================================================== rebasing across aspects

    [Fact]
    public void Rebase_ToTheSameAspect_IsJustAClamp()
    {
        foreach (var crop in new[] { new CropState(1.0, 0.4, -0.2), new CropState(1.8, 9, 9), new CropState(0.5, 0, 0) })
        {
            Assert.Equal(CropMath.Clamp(crop, 1.5, 1.2), CropMath.Rebase(crop, 1.5, 1.5, 1.2));
        }
    }

    [Fact]
    public void Rebase_KeepsTheSameSubjectCentredWhenTheNewAspectAllowsIt()
    {
        const double imageW = 3000;
        const double imageH = 2000;
        const double from = 1.3162;
        const double to = 1.4236;

        var authored = CropMath.Clamp(new CropState(1.4, 0.12, -0.05), from, imageW / imageH);
        var rebased = CropMath.Rebase(authored, from, to, imageW / imageH);

        var before = CropMath.SourceRectNormalized(authored, imageW, imageH, from, 1.0);
        var after = CropMath.SourceRectNormalized(rebased, imageW, imageH, to, 1.0);

        // Zoom 1.4 leaves slack on both axes at both aspects, so the window centre is preserved exactly.
        Assert.Equal(before.CenterX, after.CenterX, 1e-9);
        Assert.Equal(before.CenterY, after.CenterY, 1e-9);
    }

    [Fact]
    public void Rebase_LandsOnTheNearestLegalOffsetWhenTheNewAspectCannotHonourIt()
    {
        const double imageAspect = 1.5;
        const double from = 1.3162;
        const double to = 1.4236;

        var authored = CropMath.Clamp(new CropState(1.0, 99, 0), from, imageAspect);
        var rebased = CropMath.Rebase(authored, from, to, imageAspect);

        var (maxX, _) = CropMath.OffsetLimits(rebased.Zoom, to, imageAspect);
        Assert.Equal(maxX, rebased.OffsetX, 1e-12);
        Assert.True(rebased.OffsetX < authored.OffsetX, "the tighter aspect must pull the crop back toward centre");
    }

    // ================================================== the property sweep

    /// <summary>
    /// The invariant made unfalsifiable: over a wide sweep of slot aspects, image aspects,
    /// <c>zoom ≥ 1</c> and offsets — including wildly out-of-range ones — the source rect always lies
    /// inside the image and the drawn image always covers the slot exactly.
    /// </summary>
    [Fact]
    public void AtZoomAtLeastOne_EveryCropCoversItsSlotAndSamplesInsideTheImage()
    {
        var checkedCases = 0;
        foreach (var slotAspect in Aspects)
        foreach (var imageAspect in Aspects)
        foreach (var zoom in new[] { 1.0, 1.0 + 1e-12, 1.0000001, 1.05, 1.25, 2.0, 3.7, 4.0, 12.0 })
        foreach (var (ox, oy) in Offsets)
        {
            const double slotH = 3.0;
            var slotW = slotAspect * slotH;
            var imageH = 1600.0;
            var imageW = imageAspect * imageH;

            var crop = CropMath.Clamp(new CropState(zoom, ox, oy), slotAspect, imageAspect);
            var slot = new Rect(0.7, -1.3, slotW, slotH);

            var drawn = CropMath.ImageRect(crop, imageW, imageH, slot);
            Assert.True(Covers(drawn, slot),
                $"gap: slotAspect {slotAspect}, imageAspect {imageAspect}, zoom {zoom}, offsets ({ox}, {oy}) ⇒ {drawn} vs {slot}");

            var source = CropMath.SourceRect(crop, imageW, imageH, slotW, slotH);
            Assert.True(Inside(source, imageW, imageH),
                $"sampled outside: slotAspect {slotAspect}, imageAspect {imageAspect}, zoom {zoom}, " +
                $"offsets ({ox}, {oy}) ⇒ {source} vs {imageW} × {imageH}");

            checkedCases++;
        }

        Assert.True(checkedCases > 5000, $"the sweep degenerated to {checkedCases} cases");
    }

    /// <summary>
    /// The same sweep with the crop <b>never clamped by the caller</b> — the shape the defect took in
    /// the field, where the author used one aspect and the evaluator another. Evaluating a crop must
    /// enforce the frame it is evaluated in.
    /// </summary>
    [Fact]
    public void AtZoomAtLeastOne_TheInvariantHoldsEvenForCropsAuthoredAgainstAnotherAspect()
    {
        var checkedCases = 0;
        foreach (var authoredAspect in Aspects)
        foreach (var drawnAspect in Aspects)
        foreach (var imageAspect in Aspects)
        foreach (var zoom in new[] { 1.0, 1.3, 2.6 })
        {
            var (maxX, maxY) = CropMath.OffsetLimits(zoom, authoredAspect, imageAspect);
            var authored = new CropState(zoom, maxX, -maxY);

            const double slotH = 2.0;
            var slot = new Rect(0, 0, drawnAspect * slotH, slotH);
            var imageH = 1200.0;
            var imageW = imageAspect * imageH;

            var drawn = CropMath.ImageRect(authored, imageW, imageH, slot);
            Assert.True(Covers(drawn, slot),
                $"gap: authored for {authoredAspect}, drawn at {drawnAspect}, image {imageAspect}, zoom {zoom}");

            var source = CropMath.SourceRect(authored, imageW, imageH, slot.W, slot.H);
            Assert.True(Inside(source, imageW, imageH),
                $"sampled outside: authored for {authoredAspect}, drawn at {drawnAspect}, image {imageAspect}, zoom {zoom}");

            checkedCases++;
        }

        Assert.True(checkedCases > 300, $"the sweep degenerated to {checkedCases} cases");
    }

    /// <summary>R9 is not a bug: below zoom 1 the letterbox must survive every fix above.</summary>
    [Fact]
    public void BelowZoomOne_TheLetterboxIsStillThere()
    {
        var checkedCases = 0;
        foreach (var slotAspect in Aspects)
        foreach (var imageAspect in Aspects)
        foreach (var zoom in new[] { 0.25, 0.4, 0.75, 0.99 })
        {
            const double slotH = 3.0;
            var slot = new Rect(0, 0, slotAspect * slotH, slotH);
            var imageH = 1600.0;
            var imageW = imageAspect * imageH;

            var crop = CropMath.Clamp(new CropState(zoom, 9, -9), slotAspect, imageAspect);
            var drawn = CropMath.ImageRect(crop, imageW, imageH, slot);

            Assert.Equal(zoom, crop.Zoom, 1e-12);

            // Below zoom 1 the cover-binding axis is short by exactly the zoom factor, so the page
            // background always shows; the other axis may still be cropped when the aspects differ.
            Assert.False(Covers(drawn, slot), $"at zoom {zoom} the page background must show through: {drawn} vs {slot}");

            var ratioX = zoom * Math.Max(1.0, imageAspect / slotAspect);
            var ratioY = zoom * Math.Max(1.0, slotAspect / imageAspect);
            Assert.True(Math.Min(ratioX, ratioY) < 1.0, "one axis must always be the letterboxed one");

            if (ratioX <= 1)
            {
                Assert.True(drawn.X >= slot.X - 1e-9 && drawn.Right <= slot.Right + 1e-9,
                    $"at zoom {zoom} the letterboxed axis must stay inside the slot: {drawn} vs {slot}");
            }

            if (ratioY <= 1)
            {
                Assert.True(drawn.Y >= slot.Y - 1e-9 && drawn.Bottom <= slot.Bottom + 1e-9,
                    $"at zoom {zoom} the letterboxed axis must stay inside the slot: {drawn} vs {slot}");
            }

            checkedCases++;
        }

        Assert.True(checkedCases > 90, $"the sweep degenerated to {checkedCases} cases");
    }

    // ================================================== nearest-legal, not abandoned

    /// <summary>
    /// Complaint 5's other half: when the desired centre cannot be honoured the crop must sit as
    /// close to it as the cover constraint allows — the nearest legal point, never the fallback of
    /// giving up and centring on the image.
    /// </summary>
    [Fact]
    public void Clamp_ReturnsTheNearestLegalOffsetToTheOneAskedFor()
    {
        foreach (var slotAspect in Aspects)
        foreach (var imageAspect in Aspects)
        foreach (var zoom in new[] { 1.0, 1.15, 2.0 })
        foreach (var (ox, oy) in Offsets)
        {
            var desired = new CropState(zoom, ox, oy);
            var clamped = CropMath.Clamp(desired, slotAspect, imageAspect);
            var (maxX, maxY) = CropMath.OffsetLimits(zoom, slotAspect, imageAspect);

            var nearestX = Math.Clamp(ox, -maxX, maxX);
            var nearestY = Math.Clamp(oy, -maxY, maxY);

            Assert.Equal(nearestX, clamped.OffsetX, 1e-12);
            Assert.Equal(nearestY, clamped.OffsetY, 1e-12);

            // And it really is nearest: no legal offset is closer to the request.
            foreach (var candidate in new[] { -maxX, 0.0, maxX })
            {
                Assert.True(Math.Abs(candidate - ox) >= Math.Abs(clamped.OffsetX - ox) - 1e-12);
            }
        }
    }

    /// <summary>
    /// The engine's version of the same promise: a subject hard against the image edge is framed as
    /// close to centred as covering allows, and the subject stays visible.
    /// </summary>
    [Fact]
    public void SmartCrop_ClampsTowardTheSubjectRatherThanAbandoningIt()
    {
        var slot = PlainSlot(new Rect(0.1, 0.1, 0.4, 0.5));
        var photo = PhotoWithFocus(4000, 3000, new Rect(0.0, 0.40, 0.18, 0.30));

        var crop = SmartCrop.Crop(photo, slot, PageSide.Right, TrimW, TrimH, LayoutWeights.Default).Crop;

        var box = CropMath.PhotoBox(slot.Rect, TrimW, TrimH, captionBelow: false);
        var window = CropMath.SourceRectNormalized(crop, photo.Width, photo.Height, box.WidthIn, box.HeightIn);

        Assert.True(window.X <= 0.18, $"the subject at x ∈ [0, 0.18] is not inside the crop window {window}");
        Assert.True(window.X >= -1e-9, "the window must not leave the image");

        var drawn = CropMath.ImageRect(crop, photo.Width, photo.Height, new Rect(0, 0, box.WidthIn, box.HeightIn));
        Assert.True(Covers(drawn, new Rect(0, 0, box.WidthIn, box.HeightIn)), "covering is never traded away for framing");
    }

    // ================================================== the engine sweep

    /// <summary>
    /// Every built-in template, every slot, a grid of photo aspects and subject positions, captioned
    /// and not: the engine's emission must cover the box the renderer draws into. This is the test
    /// that would have caught the shipped defect.
    /// </summary>
    [Fact]
    public void EveryBuiltInSlot_GetsACropThatCoversTheBoxTheRendererDraws()
    {
        var library = PhotoBook.Core.Templates.TemplateLibrary.Default;
        var failures = new List<string>();
        var checkedCases = 0;

        foreach (var template in library.Templates)
        foreach (var slot in template.Slots)
        foreach (var (w, h) in new[] { (4000, 3000), (3000, 4000), (4000, 2250), (2400, 2400), (5000, 1500) })
        foreach (var focus in FocusPositions)
        foreach (var captioned in new[] { false, true })
        {
            var photo = PhotoWithFocus(w, h, focus);
            if (captioned) photo.Caption = "A caption long enough to render.";

            var crop = SmartCrop.Crop(photo, slot, PageSide.Right, TrimW, TrimH, LayoutWeights.Default).Crop;
            var drawn = DrawnBox(slot, captioned && slot.CaptionPolicy == CaptionPolicy.Below);
            var image = CropMath.ImageRect(crop, w, h, drawn);

            checkedCases++;
            if (!Covers(image, drawn))
            {
                failures.Add($"{template.Id}/{slot.Id} {w}×{h} focus {focus} captioned={captioned}: {crop} ⇒ {image} vs {drawn}");
                continue;
            }

            // A gutter-spanning slot's frame is the panorama across both pages (R18), which needs the
            // facing template to resolve; the covering check above is the one that applies to it.
            if (slot.SpanId is not null) continue;

            // And the engine authored it correctly rather than being rescued by the evaluator: the
            // emitted crop is already legal in the frame the renderer draws into.
            var legal = CropMath.Clamp(crop, drawn.W / drawn.H, (double)w / h);
            if (Math.Abs(legal.OffsetX - crop.OffsetX) > 1e-9 || Math.Abs(legal.OffsetY - crop.OffsetY) > 1e-9 ||
                Math.Abs(legal.Zoom - crop.Zoom) > 1e-9)
            {
                failures.Add($"{template.Id}/{slot.Id} {w}×{h} focus {focus} captioned={captioned}: " +
                             $"engine emitted {crop} but the drawn frame only allows {legal}");
            }
        }

        Assert.True(checkedCases > 5000, $"the sweep degenerated to {checkedCases} cases");
        Assert.True(failures.Count == 0,
            $"{failures.Count} of {checkedCases} placements showed page background:{Environment.NewLine}" +
            string.Join(Environment.NewLine, failures.Take(12)));
    }

    // ---------------------------------------------------------------- helpers

    private static double[] Aspects => [0.28, 0.5, 0.7, 0.9999999, 1.0, 1.0000001, 1.3333, 1.7778, 2.5, 4.0];

    private static (double, double)[] Offsets =>
    [
        (0, 0), (0.03, -0.03), (0.5, 0.5), (-0.5, 0.5), (12, -12), (-1e9, 1e9), (0.0001, -0.0001),
    ];

    private static Rect[] FocusPositions =>
    [
        new(0.25, 0.25, 0.5, 0.5),
        new(0.0, 0.0, 0.2, 0.2),
        new(0.8, 0.8, 0.2, 0.2),
        new(0.0, 0.4, 0.15, 0.2),
        new(0.85, 0.4, 0.15, 0.2),
        new(0.4, 0.0, 0.2, 0.15),
        new(0.05, 0.05, 0.9, 0.9),
    ];

    private static ImageSlot CaptionedSlot() => new()
    {
        Id = "s1",
        Rect = new Rect(0.012, 0.024, 0.476, 0.468),
        Aspect = 0.476 * TrimW / (0.468 * TrimH),
        CaptionPolicy = CaptionPolicy.Below,
    };

    private static ImageSlot PlainSlot(Rect rect) => new()
    {
        Id = "s1",
        Rect = rect,
        Aspect = rect.W * TrimW / (rect.H * TrimH),
        CaptionPolicy = CaptionPolicy.None,
    };

    private static Photo PhotoWithFocus(int width, int height, Rect focus) => new()
    {
        Id = "ph-test",
        Width = width,
        Height = height,
        FocusRegions = [new FocusRegion { Rect = focus, Weight = 1.0, Kind = FocusKind.Saliency }],
    };

    /// <summary>The box <c>PageRenderer</c> draws the photo into, computed the long way round.</summary>
    private static Rect DrawnBox(ImageSlot slot, bool captioned)
    {
        var bleed = slot.SpanId is null ? PageGeometry.BleedIn : 0.0;
        const double tol = 0.005;

        var left = slot.Rect.X <= tol ? -bleed : slot.Rect.X * TrimW;
        var right = slot.Rect.Right >= 1 - tol ? TrimW + bleed : slot.Rect.Right * TrimW;
        var top = slot.Rect.Y <= tol ? -bleed : slot.Rect.Y * TrimH;
        var bottom = slot.Rect.Bottom >= 1 - tol ? TrimH + bleed : slot.Rect.Bottom * TrimH;

        if (captioned)
        {
            bottom -= Math.Min(CropMath.CaptionBandIn, (bottom - top) / 2);
        }

        return new Rect(left, top, right - left, bottom - top);
    }

    private static bool Covers(Rect outer, Rect inner)
    {
        const double slop = 1e-9;
        return outer.X <= inner.X + slop && outer.Y <= inner.Y + slop &&
               outer.Right >= inner.Right - slop && outer.Bottom >= inner.Bottom - slop;
    }

    private static bool Inside(Rect source, double imageW, double imageH)
    {
        const double slop = 1e-6;
        return source.X >= -slop && source.Y >= -slop &&
               source.Right <= imageW + slop && source.Bottom <= imageH + slop;
    }
}
