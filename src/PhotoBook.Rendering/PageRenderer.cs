using System.Globalization;
using PhotoBook.Core.Model;
using SkiaSharp;
using CoreRect = PhotoBook.Core.Model.Rect;

namespace PhotoBook.Rendering;

/// <summary>
/// The one page renderer. It draws a <see cref="Page"/> onto an <see cref="SKCanvas"/> — any canvas:
/// the editor's preview surface or a page of <c>SKDocument.CreatePdf</c>.
/// <para>
/// <b>This is the WYSIWYG guarantee of ADR-0003 made concrete.</b> There is no second draw path. The
/// screen and the PDF differ in exactly two ways, both of which live in the request rather than in
/// this code: which pixel tier <see cref="IRenderImageSource"/> hands back, and whether the amber
/// empty-slot flags of doc 09 §3.6 are drawn — they never are in export.
/// </para>
/// <para>
/// Draw order per page (doc 10 §6): background, then image slots in <see cref="Template.SlotsInPaintOrder"/>
/// — ascending <see cref="ImageSlot.Layer"/>, authored order within a layer — with their captions, then
/// text slots (each on a panel scrim where it sits on a photo), then editor-only overlays. Crops come from
/// <see cref="CropMath.SourceRect"/>, so what the engine computed and what the editor edits is
/// exactly what prints (kernel §4).
/// </para>
/// </summary>
public static class PageRenderer
{
    /// <summary>The caption band reserved under a <see cref="CaptionPolicy.Below"/> photo, in inches (doc 07).</summary>
    public const double CaptionBandIn = 0.30;

    /// <summary>
    /// The hard line cap for a caption (doc 10 §4). Doc 07 also caps the below-band at what fits in
    /// 0.30 in, which at the 8.5 pt default is two lines — the renderer honors both by taking the
    /// smaller of this cap and what the band physically holds.
    /// </summary>
    public const int MaxCaptionLines = 3;

    /// <summary>Gap between a photo's bottom edge and its below-caption, in points.</summary>
    public const double CaptionGapPt = 3.0;

    /// <summary>Stroke width of the empty-slot flag, in points (2 px at 96 DPI — doc 09 §3.6).</summary>
    public const double EmptySlotStrokePt = 1.5;

    /// <summary>Opacity of the empty-slot fill (doc 09 §3.6).</summary>
    public const double EmptySlotFillOpacity = 0.12;

    /// <summary>Gap between the month title and its year subtitle, in points (doc 10 §7).</summary>
    public const double MonthTitleSubtitleGapPt = 8.0;

    /// <summary>Size of the month-title year subtitle, in points (doc 10 §7).</summary>
    public const double MonthTitleSubtitleSizePt = 14.0;

    /// <summary>Tracking of the month-title year subtitle, in ems (doc 10 §7).</summary>
    public const double MonthTitleSubtitleTrackingEm = 0.05;

    /// <summary>Feather radius of the lift under an overlapping photo, in points (doc 07 "Deliberate overlap").</summary>
    public const double OverlapLiftFeatherPt = 7.0;

    /// <summary>How far the overlap lift sits below its photo, in points — enough to read as depth, not as a border.</summary>
    public const double OverlapLiftOffsetPt = 2.0;

    /// <summary>Opacity of the overlap lift at its core.</summary>
    public const double OverlapLiftOpacity = 0.55;

    /// <summary>Sampling for export: a Mitchell cubic, which downsamples photographs cleanly.</summary>
    public static SKSamplingOptions ExportSampling { get; } = new(new SKCubicResampler(1f / 3f, 1f / 3f));

    /// <summary>Sampling for the screen: linear with mipmaps, cheap enough for interactive pan and zoom.</summary>
    public static SKSamplingOptions ScreenSampling { get; } = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <summary>
    /// Draws one page. The canvas is left in the state it was found in; all clipping and transforms
    /// are balanced.
    /// </summary>
    /// <param name="canvas">The target canvas — a preview surface or a PDF page.</param>
    /// <param name="request">Everything to draw and where to draw it.</param>
    /// <returns>The diagnostics and the device geometry the draw produced.</returns>
    public static PageRenderResult Render(SKCanvas canvas, PageRenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(request);
        using var pass = new RenderPass(canvas, request);
        return pass.Run();
    }

    /// <summary>
    /// Convenience for the editor: draws a page scaled to fit <paramref name="target"/>, returning the
    /// mapper that was used so the caller can hit-test against the same geometry.
    /// </summary>
    /// <param name="canvas">The target canvas.</param>
    /// <param name="request">The page request; its <see cref="PageRenderRequest.Geometry"/> supplies the profile and page size.</param>
    /// <param name="target">The device rect to fit the sheet into.</param>
    public static (PageRenderResult Result, PageGeometryMapper Geometry) RenderFitted(
        SKCanvas canvas, PageRenderRequest request, SKRect target)
    {
        ArgumentNullException.ThrowIfNull(request);
        var geometry = PageGeometryMapper.Fit(
            request.Geometry.Profile, request.Geometry.PageSize.Id, target, request.Geometry.Surface);
        var fitted = request with { Geometry = geometry };
        return (Render(canvas, fitted), geometry);
    }

    private sealed class RenderPass : IDisposable
    {
        private readonly SKCanvas _canvas;
        private readonly PageRenderRequest _request;
        private readonly PageGeometryMapper _geo;
        private readonly Style _style;
        private readonly List<RenderDiagnostic> _diagnostics = [];
        private readonly Dictionary<string, SKRect> _slotRects = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SKRect> _visibleRects = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SKRect> _textRects = new(StringComparer.Ordinal);
        private readonly List<SKFont> _fonts = [];

        public RenderPass(SKCanvas canvas, PageRenderRequest request)
        {
            _canvas = canvas;
            _request = request;
            _geo = request.Geometry;
            _style = request.Style;
        }

        private PairSide Side => _request.Half switch
        {
            PageHalf.Left => PairSide.Left,
            PageHalf.Right => PairSide.Right,
            _ => _request.Side,
        };

        private SKSamplingOptions Sampling =>
            _request.Target.IsExport() ? ExportSampling : ScreenSampling;

        public PageRenderResult Run()
        {
            var save = _canvas.Save();
            try
            {
                var pageRegion = _geo.PageMediaRect(_request.Half);
                _canvas.ClipRect(pageRegion, SKClipOperation.Intersect, antialias: false);

                if (_request.DrawBackground) DrawBackground(pageRegion);
                ReportFontSubstitutions();

                // Paint order, not reading order: a slot on a higher layer sits on top of the ones
                // below it, which is what makes a declared overlap an inset rather than a collision
                // (doc 07 "Deliberate overlap"). Templates with no overlap have every layer at 0, so
                // this is the authored order for all but the 8 that declare one.
                foreach (var slot in _request.Template.SlotsInPaintOrder) DrawImageSlot(slot);
                foreach (var textSlot in _request.Template.TextSlots) DrawTextSlot(textSlot);

                if (_request.WatermarkVisible) DrawWatermark(pageRegion);
                if (_request.DrawGuides && !_request.Target.IsExport()) DrawGuides();
            }
            finally
            {
                _canvas.RestoreToCount(save);
            }

            return new PageRenderResult(_diagnostics, _slotRects, _visibleRects, _textRects);
        }

        public void Dispose()
        {
            foreach (var font in _fonts) font.Dispose();
            _fonts.Clear();
        }

        // ---- background -------------------------------------------------------------------------

        private void DrawBackground(SKRect region)
        {
            var background = _style.Background;
            if (background?.Kind == BackgroundKind.Image)
            {
                Report(RenderDiagnosticKind.UnsupportedStyle, RenderSeverity.Info,
                    "Image backgrounds are reserved for a later version (R21); painting the solid color instead.");
            }

            using var paint = new SKPaint
            {
                Color = SkiaColors.Parse(background?.Color, SkiaColors.Background),
                Style = SKPaintStyle.Fill,
                IsAntialias = false,
            };
            _canvas.DrawRect(region, paint);
        }

        // ---- image slots ------------------------------------------------------------------------

        private void DrawImageSlot(ImageSlot slot)
        {
            var spanning = !string.IsNullOrEmpty(slot.SpanId);
            var spine = spanning ? (Side == PairSide.Left ? PairSide.Right : PairSide.Left) : (PairSide?)null;

            var mapped = _geo.MapPageRect(slot.Rect, _request.Half);
            var slotRect = _geo.ApplyBleedExtension(slot.Rect, mapped, _request.Half, spine);

            var placement = _request.Page.PlacementFor(slot.Id);
            var photo = placement is null ? null : _request.Photos.Find(placement.PhotoId);
            var caption = photo?.Caption;
            var hasCaption = slot.CaptionPolicy != CaptionPolicy.None && !string.IsNullOrWhiteSpace(caption);

            var imageRect = slotRect;
            var captionBand = SKRect.Empty;
            if (hasCaption && slot.CaptionPolicy == CaptionPolicy.Below)
            {
                var band = Math.Min(_geo.Inches(CaptionBandIn), slotRect.Height * 0.5f);
                captionBand = new SKRect(slotRect.Left, slotRect.Bottom - band, slotRect.Right, slotRect.Bottom);
                imageRect = new SKRect(slotRect.Left, slotRect.Top, slotRect.Right, slotRect.Bottom - band);
            }

            _slotRects[slot.Id] = imageRect;

            if (placement is null || string.IsNullOrEmpty(placement.PhotoId))
            {
                Report(RenderDiagnosticKind.EmptySlot, RenderSeverity.Warning,
                    "This image slot has no photo.", slot.Id);
                DrawEmptySlotFlag(imageRect);
                return;
            }

            if (photo is null)
            {
                Report(RenderDiagnosticKind.MissingPhoto, RenderSeverity.Error,
                    $"Photo '{placement.PhotoId}' is placed here but is not in the catalog.", slot.Id, placement.PhotoId);
                DrawEmptySlotFlag(imageRect);
                return;
            }

            DrawOverlapLift(slot, imageRect);

            // The crop is computed over the whole panorama for a gutter-spanning photo (R18); each page
            // then draws only the part that lands inside its own slot.
            var cropRect = spanning ? ResolveSpanRect(slot, imageRect) : imageRect;
            var visible = DrawPhoto(slot, placement, photo, imageRect, cropRect);
            if (visible is { } visibleRect)
            {
                _visibleRects[slot.Id] = visibleRect;
                DrawImageBorder(visibleRect);
                if (hasCaption && slot.CaptionPolicy == CaptionPolicy.Overlay) DrawOverlayCaption(caption!, visibleRect, slot.Id);
            }

            if (hasCaption && slot.CaptionPolicy == CaptionPolicy.Below) DrawBelowCaption(caption!, captionBand, slot.Id);
            ReportEffectiveDpi(slot, placement, photo, cropRect);
        }

        private SKRect ResolveSpanRect(ImageSlot slot, SKRect fallback)
        {
            var facing = _request.FacingTemplate?.Slots
                .FirstOrDefault(s => string.Equals(s.SpanId, slot.SpanId, StringComparison.Ordinal));
            if (facing is null)
            {
                Report(RenderDiagnosticKind.UnresolvedSpan, RenderSeverity.Info,
                    $"Slot '{slot.Id}' spans the gutter but the facing page was not supplied; drawing it page-local.",
                    slot.Id);
                return fallback;
            }

            var (leftRect, rightRect) = Side == PairSide.Left ? (slot.Rect, facing.Rect) : (facing.Rect, slot.Rect);
            var span = _geo.MapSpanRect(leftRect, rightRect, Side);

            // The panorama's own outer edges bleed; the spine does not (doc 12 "Spreads").
            var bleed = _geo.Inches(_geo.BleedIn);
            const double tol = PageGeometryMapper.BleedSnapTolerance;
            var left = leftRect.X <= tol ? span.Left - bleed : span.Left;
            var right = rightRect.Right >= 1 - tol ? span.Right + bleed : span.Right;
            var top = Math.Min(leftRect.Y, rightRect.Y) <= tol ? span.Top - bleed : span.Top;
            var bottom = Math.Max(leftRect.Bottom, rightRect.Bottom) >= 1 - tol ? span.Bottom + bleed : span.Bottom;
            return new SKRect(left, top, right, bottom);
        }

        /// <summary>
        /// Samples the photo through <see cref="CropMath.SourceRect"/> and draws it, returning the rect
        /// the visible pixels actually occupy. When <c>zoom &lt; 1</c> the source window runs off the
        /// image; the surplus is the letterbox where the page background shows through (R9), and the
        /// returned rect is the smaller, visible image — which is what the border must wrap
        /// (doc 10 §5).
        /// </summary>
        private SKRect? DrawPhoto(ImageSlot slot, Placement placement, Photo photo, SKRect clipRect, SKRect cropRect)
        {
            if (cropRect.Width <= 0 || cropRect.Height <= 0) return null;

            var render = _request.Images.GetImage(
                new RenderImageRequest(photo.Id, RequiredLongEdge(photo, placement.Crop, cropRect), _request.Target));
            if (render is null)
            {
                Report(RenderDiagnosticKind.MissingImageData, RenderSeverity.Error,
                    $"No pixels available for photo '{photo.Id}'; the slot is showing the page background.",
                    slot.Id, photo.Id);
                DrawEmptySlotFlag(clipRect);
                return null;
            }

            double imgW = render.PixelWidth;
            double imgH = render.PixelHeight;
            if (imgW <= 0 || imgH <= 0) return null;

            var source = CropMath.SourceRect(placement.Crop, imgW, imgH, cropRect.Width, cropRect.Height);
            if (!source.IsWellFormed) return null;

            // Clip the source window to the bitmap and carry the same proportional clip into the
            // destination — one expression that covers both the zoom ≥ 1 and the zoom < 1 regimes.
            var left = Math.Max(source.X, 0);
            var top = Math.Max(source.Y, 0);
            var right = Math.Min(source.Right, imgW);
            var bottom = Math.Min(source.Bottom, imgH);
            if (right <= left || bottom <= top) return null;

            var destLeft = cropRect.Left + (float)((left - source.X) / source.W * cropRect.Width);
            var destRight = cropRect.Left + (float)((right - source.X) / source.W * cropRect.Width);
            var destTop = cropRect.Top + (float)((top - source.Y) / source.H * cropRect.Height);
            var destBottom = cropRect.Top + (float)((bottom - source.Y) / source.H * cropRect.Height);

            var srcRect = new SKRect((float)left, (float)top, (float)right, (float)bottom);
            var destRect = new SKRect(destLeft, destTop, destRight, destBottom);

            var radius = _geo.Points(_style.ImageBorder?.CornerRadiusPt ?? 0);
            var save = _canvas.Save();
            try
            {
                if (radius > 0)
                {
                    using var rounded = new SKRoundRect(clipRect, radius, radius);
                    _canvas.ClipRoundRect(rounded, SKClipOperation.Intersect, antialias: true);
                }
                else
                {
                    _canvas.ClipRect(clipRect, SKClipOperation.Intersect, antialias: false);
                }

                using var paint = new SKPaint { IsAntialias = true, IsDither = _request.Target.IsExport() };
                _canvas.DrawImage(render.Image, srcRect, destRect, Sampling, paint);
            }
            finally
            {
                _canvas.RestoreToCount(save);
            }

            var visible = destRect;
            if (!visible.IntersectsWith(clipRect)) return null;
            visible.Intersect(clipRect);
            return visible;
        }

        /// <summary>
        /// The smallest source long edge that still meets the target resolution for this placement.
        /// Downsampling is fine; upsampling is never requested (doc 12 "Image handling" step 3).
        /// </summary>
        private int RequiredLongEdge(Photo photo, CropState crop, SKRect dest)
        {
            if (photo.Width <= 0 || photo.Height <= 0) return 0;
            var dpi = _request.Target.RasterDpi();
            var effectiveDpi = dpi > 0 ? dpi : _geo.Scale;
            var destWidthPx = _geo.ToInches(dest.Width) * effectiveDpi;
            var source = CropMath.SourceRect(crop, photo.Width, photo.Height, dest.Width, dest.Height);
            if (!(source.W > 0)) return 0;
            var factor = Math.Clamp(destWidthPx / source.W, 0.0, 1.0);
            return (int)Math.Ceiling(Math.Max(photo.Width, photo.Height) * factor);
        }

        private void ReportEffectiveDpi(ImageSlot slot, Placement placement, Photo photo, SKRect dest)
        {
            if (photo.Width <= 0 || photo.Height <= 0) return;
            var (widthIn, heightIn) = _geo.PhysicalSizeIn(dest);
            if (!(widthIn > 0) || !(heightIn > 0)) return;

            var dpi = CropMath.EffectiveDpi(placement.Crop, photo.Width, photo.Height, widthIn, heightIn);
            if (dpi >= PageGeometry.ExportDpi) return;

            var severity = dpi < PageGeometry.MinimumEffectiveDpi ? RenderSeverity.Warning : RenderSeverity.Info;
            Report(RenderDiagnosticKind.LowEffectiveDpi, severity,
                string.Create(CultureInfo.InvariantCulture,
                    $"Photo '{photo.OriginalFileName}' prints at {dpi:0} DPI in this slot (target {PageGeometry.ExportDpi})."),
                slot.Id, photo.Id, dpi);
        }

        // ---- borders ----------------------------------------------------------------------------

        /// <summary>
        /// Strokes the border <b>inside</b> the visible image rect so enabling it never moves any
        /// geometry (doc 10 §5, R23). Edges that run off the bleed box are skipped — a border the
        /// trimmer cuts through looks like a mistake.
        /// </summary>
        private void DrawImageBorder(SKRect visible)
        {
            var border = _style.ImageBorder;
            if (border?.Enabled != true) return;

            var width = _geo.Points(border.WidthPt ?? 2.0);
            if (width <= 0) return;

            var inset = width / 2f;
            var rect = new SKRect(visible.Left + inset, visible.Top + inset, visible.Right - inset, visible.Bottom - inset);
            if (rect.Width <= 0 || rect.Height <= 0) return;

            const float epsilon = 0.5f;
            var media = _geo.MediaRect;
            var drawLeft = visible.Left > media.Left + epsilon;
            var drawRight = visible.Right < media.Right - epsilon;
            var drawTop = visible.Top > media.Top + epsilon;
            var drawBottom = visible.Bottom < media.Bottom - epsilon;
            if (!drawLeft && !drawRight && !drawTop && !drawBottom) return;

            using var paint = new SKPaint
            {
                Color = SkiaColors.Parse(border.Color, SkiaColors.Text),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = width,
                IsAntialias = true,
            };

            var radius = _geo.Points(border.CornerRadiusPt ?? 0);
            if (drawLeft && drawRight && drawTop && drawBottom)
            {
                if (radius > 0) _canvas.DrawRoundRect(rect, radius, radius, paint);
                else _canvas.DrawRect(rect, paint);
                return;
            }

            if (drawTop) _canvas.DrawLine(rect.Left, rect.Top, rect.Right, rect.Top, paint);
            if (drawBottom) _canvas.DrawLine(rect.Left, rect.Bottom, rect.Right, rect.Bottom, paint);
            if (drawLeft) _canvas.DrawLine(rect.Left, rect.Top, rect.Left, rect.Bottom, paint);
            if (drawRight) _canvas.DrawLine(rect.Right, rect.Top, rect.Right, rect.Bottom, paint);
        }

        // ---- captions ---------------------------------------------------------------------------

        private void DrawBelowCaption(string caption, SKRect band, string slotId)
        {
            if (band.Height <= 0 || band.Width <= 0) return;

            var font = CaptionFont();
            var lineHeight = _style.CaptionText?.LineHeight ?? 1.25;
            var gap = _geo.Points(CaptionGapPt);
            var available = band.Height - gap;
            if (available <= 0) return;

            var spacing = (float)(font.Size * (lineHeight > 0 ? lineHeight : 1.25));
            var fitLines = spacing > 0 ? (int)Math.Floor((available + spacing - font.Size) / spacing) : 1;
            var maxLines = Math.Clamp(fitLines, 1, MaxCaptionLines);

            var layout = TextLayout.Layout([caption], font, lineHeight, band.Width, available, maxLines, ellipsize: true);
            if (layout.IsEmpty) return;

            if (layout.Truncated || layout.Overflowed)
            {
                Report(RenderDiagnosticKind.CaptionTruncated, RenderSeverity.Warning,
                    "This caption is longer than its band and was shortened with an ellipsis.", slotId);
            }

            using var paint = TextPaint(_style.CaptionText?.Color);
            TextLayout.Draw(_canvas, layout, font, paint, new SKPoint(band.Left, band.Top + gap), band.Width);
        }

        /// <summary>
        /// The overlay caption of doc 10 §4: white text inside the photo's bottom edge on a vertical
        /// black gradient running from 0% at the scrim's top to <c>maxOpacity</c> at the photo's bottom.
        /// The text is kept inside the safe area even when the photo bleeds off the page (kernel §3).
        /// </summary>
        private void DrawOverlayCaption(string caption, SKRect photoRect, string slotId)
        {
            var font = CaptionFont();
            var lineHeight = _style.CaptionText?.LineHeight ?? 1.25;
            var padding = _geo.Points(_style.OverlayScrim?.PaddingPt ?? 12);

            var textLeft = Math.Max(photoRect.Left + padding, _geo.SafeRect.Left);
            var textRight = Math.Min(photoRect.Right - padding, _geo.SafeRect.Right);
            var textBottom = Math.Min(photoRect.Bottom - padding, _geo.SafeRect.Bottom);
            var width = textRight - textLeft;
            if (width <= 0) return;

            var layout = TextLayout.Layout([caption], font, lineHeight, width,
                Math.Max(photoRect.Height - 2 * padding, 0), MaxCaptionLines, ellipsize: true);
            if (layout.IsEmpty) return;

            if (layout.Truncated || layout.Overflowed)
            {
                Report(RenderDiagnosticKind.CaptionTruncated, RenderSeverity.Warning,
                    "This overlay caption is longer than the photo allows and was shortened.", slotId);
            }

            var textTop = textBottom - layout.Height;
            if (_style.OverlayScrim?.Enabled != false)
            {
                var scrimTop = textTop - padding;
                DrawScrim(new SKRect(photoRect.Left, scrimTop, photoRect.Right, photoRect.Bottom));
            }

            using var paint = TextPaint(_style.CaptionText?.Color);
            TextLayout.Draw(_canvas, layout, font, paint, new SKPoint(textLeft, textTop), width);
        }

        /// <summary>
        /// The panel scrim of doc 10 §4: the darkening under a text block that a template deliberately
        /// placed <em>on</em> a photo (<see cref="TextSlot.Scrim"/>, doc 07 "Deliberate overlap"). The
        /// overlay-caption gradient of <see cref="DrawScrim"/> is anchored to the photo's bottom edge
        /// and is wrong here: these blocks sit in the middle of an image, where a one-sided ramp leaves
        /// the first line on bare photo and cuts hard at the last. So the panel is <c>maxOpacity</c>
        /// black over the text plus <c>paddingPt</c>, feathered to nothing over a further
        /// <c>paddingPt</c> on all four sides, and clipped to the photos it belongs to so it never
        /// darkens the page around them.
        /// <para>
        /// It is built from flat fills and plain linear/radial gradients rather than a blur, because
        /// those are the primitives <c>SKDocument.CreatePdf</c> writes natively — a mask filter would
        /// rasterize on the PDF path and break the one-draw-path guarantee of ADR-0003.
        /// </para>
        /// </summary>
        private void DrawPanelScrim(SKRect block, TextSlot slot)
        {
            if (_style.OverlayScrim?.Enabled == false) return;
            var maxOpacity = Math.Clamp(_style.OverlayScrim?.MaxOpacity ?? 0.6, 0, 1);
            if (maxOpacity <= 0 || block.Width <= 0 || block.Height <= 0) return;

            var padding = Math.Max(_geo.Points(_style.OverlayScrim?.PaddingPt ?? 12), 1f);
            var core = new SKRect(block.Left - padding, block.Top - padding, block.Right + padding, block.Bottom + padding);
            var feather = padding;

            var save = _canvas.Save();
            try
            {
                if (ScrimClip(slot) is { } clip)
                {
                    using (clip) _canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
                }

                FeatheredBlock(core, feather, SKColors.Black.WithOpacity(maxOpacity));
            }
            finally
            {
                _canvas.RestoreToCount(save);
            }
        }

        /// <summary>
        /// The box the drawn glyphs actually occupy inside a text slot: the widest line governs the
        /// width, alignment decides which edge it hangs from, and the laid-out height governs the
        /// bottom. A scrim sized to the <em>slot</em> instead would be a dark plate reaching across
        /// empty page wherever the words ran short — which is what the scrim is supposed to avoid.
        /// </summary>
        private static SKRect InkedBounds(TextBlockLayout layout, SKRect column, SKTextAlign align)
        {
            var widest = 0f;
            foreach (var line in layout.Lines) widest = Math.Max(widest, line.Width);
            widest = Math.Min(widest, column.Width);
            var height = Math.Min(layout.Height, column.Height);

            var (left, right) = align switch
            {
                SKTextAlign.Center => (column.MidX - widest / 2f, column.MidX + widest / 2f),
                SKTextAlign.Right => (column.Right - widest, column.Right),
                _ => (column.Left, column.Left + widest),
            };

            return new SKRect(left, column.Top, right, column.Top + height);
        }

        /// <summary>
        /// The lift under a photo that a template deliberately laid over another one
        /// (<see cref="ImageSlot.Layer"/> above zero). Overlap with no separation reads as a collision —
        /// two frames of similar tone simply merge at the seam — so the slot on top casts a soft shadow
        /// onto whatever is beneath it. It is drawn <em>before</em> the photo, so the photo covers the
        /// solid core and only the offset skirt shows.
        /// <para>
        /// Layer-zero slots never get one: a shadow around every photo would be a style, and style is
        /// the user's to choose (R23, doc 10 §5). This is the geometry telling the reader which frame is
        /// on top, and it exists only in the templates that opted into overlap.
        /// </para>
        /// </summary>
        private void DrawOverlapLift(ImageSlot slot, SKRect rect)
        {
            if (slot.Layer <= 0 || rect.Width <= 0 || rect.Height <= 0) return;

            var feather = Math.Max(_geo.Points(OverlapLiftFeatherPt), 1f);
            var offset = _geo.Points(OverlapLiftOffsetPt);
            var core = new SKRect(rect.Left, rect.Top + offset, rect.Right, rect.Bottom + offset);
            FeatheredBlock(core, feather, SKColors.Black.WithOpacity(OverlapLiftOpacity));
        }

        /// <summary>
        /// A solid rect that fades to nothing over <paramref name="feather"/> on every side — the shared
        /// primitive behind the panel scrim and the overlap lift. Flat fills plus plain linear and radial
        /// gradients only, so <c>SKDocument.CreatePdf</c> writes it natively instead of rasterizing a
        /// blur and breaking the one-draw-path guarantee of ADR-0003.
        /// </summary>
        private void FeatheredBlock(SKRect core, float feather, SKColor color)
        {
            if (core.Width <= 0 || core.Height <= 0) return;

            using (var fill = new SKPaint { Color = color, Style = SKPaintStyle.Fill, IsAntialias = false })
            {
                _canvas.DrawRect(core, fill);
            }

            FeatherEdge(new SKRect(core.Left, core.Top - feather, core.Right, core.Top), color, vertical: true, towardEnd: true);
            FeatherEdge(new SKRect(core.Left, core.Bottom, core.Right, core.Bottom + feather), color, vertical: true, towardEnd: false);
            FeatherEdge(new SKRect(core.Left - feather, core.Top, core.Left, core.Bottom), color, vertical: false, towardEnd: true);
            FeatherEdge(new SKRect(core.Right, core.Top, core.Right + feather, core.Bottom), color, vertical: false, towardEnd: false);

            FeatherCorner(new SKPoint(core.Left, core.Top), -feather, -feather, color);
            FeatherCorner(new SKPoint(core.Right, core.Top), feather, -feather, color);
            FeatherCorner(new SKPoint(core.Left, core.Bottom), -feather, feather, color);
            FeatherCorner(new SKPoint(core.Right, core.Bottom), feather, feather, color);
        }

        /// <summary>One edge of a feathered block: opaque against the core, transparent at the outer edge.</summary>
        private void FeatherEdge(SKRect band, SKColor color, bool vertical, bool towardEnd)
        {
            if (band.Width <= 0 || band.Height <= 0) return;
            var (from, to) = vertical
                ? (new SKPoint(band.Left, band.Top), new SKPoint(band.Left, band.Bottom))
                : (new SKPoint(band.Left, band.Top), new SKPoint(band.Right, band.Top));
            var colors = towardEnd
                ? new[] { color.WithAlpha(0), color }
                : new[] { color, color.WithAlpha(0) };

            using var shader = SKShader.CreateLinearGradient(from, to, colors, [0f, 1f], SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader, IsAntialias = false };
            _canvas.DrawRect(band, paint);
        }

        /// <summary>One corner of a feathered block, so the feather turns instead of notching.</summary>
        private void FeatherCorner(SKPoint pivot, float dx, float dy, SKColor color)
        {
            var radius = Math.Max(Math.Abs(dx), Math.Abs(dy));
            if (radius <= 0) return;

            using var shader = SKShader.CreateRadialGradient(
                pivot, radius, [color, color.WithAlpha(0)], [0f, 1f], SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader, IsAntialias = false };
            var rect = new SKRect(Math.Min(pivot.X, pivot.X + dx), Math.Min(pivot.Y, pivot.Y + dy),
                                  Math.Max(pivot.X, pivot.X + dx), Math.Max(pivot.Y, pivot.Y + dy));
            _canvas.DrawRect(rect, paint);
        }

        /// <summary>
        /// The photos this text sits on, as a clip path. The linter guarantees a scrimmed text slot lies
        /// wholly inside one image slot (L3), but a Detached page snapshot the user hand-edited carries
        /// no such promise — so the clip is the union of every placed slot the text actually touches,
        /// and null when it touches none (nothing to darken).
        /// </summary>
        private SKPath? ScrimClip(TextSlot slot)
        {
            SKPathBuilder? builder = null;
            foreach (var image in _request.Template.Slots)
            {
                if (!image.Rect.Intersects(slot.Rect)) continue;
                if (_request.Page.PlacementFor(image.Id) is not { } placement || string.IsNullOrEmpty(placement.PhotoId)) continue;

                var mapped = _geo.MapPageRect(image.Rect, _request.Half);
                var rect = _geo.ApplyBleedExtension(image.Rect, mapped, _request.Half);
                (builder ??= new SKPathBuilder()).AddRect(rect);
            }

            return builder?.Detach();
        }

        /// <summary>The doc 10 §4 gradient: transparent at the top edge, <c>maxOpacity</c> black at the bottom.</summary>
        private void DrawScrim(SKRect rect)
        {
            if (rect.Height <= 0 || rect.Width <= 0) return;
            var maxOpacity = Math.Clamp(_style.OverlayScrim?.MaxOpacity ?? 0.6, 0, 1);
            if (maxOpacity <= 0) return;

            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(rect.Left, rect.Top),
                new SKPoint(rect.Left, rect.Bottom),
                [SKColors.Black.WithAlpha(0), SKColors.Black.WithOpacity(maxOpacity)],
                [0f, 1f],
                SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader, IsAntialias = false };
            _canvas.DrawRect(rect, paint);
        }

        // ---- text slots -------------------------------------------------------------------------

        private void DrawTextSlot(TextSlot slot)
        {
            var rect = _geo.MapPageRect(slot.Rect, _request.Half);
            _textRects[slot.Id] = rect;
            if (rect.Width <= 0 || rect.Height <= 0) return;

            switch (slot.Role)
            {
                case TextRole.Journal:
                    DrawJournal(slot, rect);
                    break;
                case TextRole.Caption:
                    DrawAttachedCaption(slot, rect);
                    break;
                case TextRole.MonthTitle:
                    DrawMonthTitle(slot, rect);
                    break;
            }
        }

        /// <summary>
        /// Journal text for the day groups assigned to this slot. A slot with no entry renders empty —
        /// deliberate negative space, never an error (doc 07, R20). Overflow is reported, never
        /// shrunk away (kernel §9).
        /// </summary>
        private void DrawJournal(TextSlot slot, SKRect rect)
        {
            var paragraphs = JournalParagraphs(slot.Id);
            if (paragraphs.Count == 0) return;

            var font = JournalFont();
            var lineHeight = _style.JournalText?.LineHeight ?? 1.35;
            var layout = TextLayout.Layout(paragraphs, font, lineHeight, rect.Width, rect.Height);
            if (layout.IsEmpty) return;

            if (layout.Overflowed)
            {
                Report(RenderDiagnosticKind.TextOverflow, RenderSeverity.Error,
                    string.Create(CultureInfo.InvariantCulture,
                        $"Journal text overflows text slot '{slot.Id}' by about {layout.OverflowCharacters} characters. Text is never auto-shrunk (kernel §9): choose a roomier template, trim the entry, or lower the journal size in Style."),
                    slot.Id, value: layout.OverflowCharacters);
            }

            // The scrim hugs the text that is actually there, not the slot: a two-line entry in a tall
            // journal slot would otherwise get a panel four times the size of the words on it.
            if (OverlapsPlacedPhoto(slot))
            {
                DrawPanelScrim(InkedBounds(layout, rect, Align(slot, SKTextAlign.Left)), slot);
            }

            var save = _canvas.Save();
            try
            {
                _canvas.ClipRect(rect, SKClipOperation.Intersect, antialias: false);
                using var paint = TextPaint(_style.JournalText?.Color);
                TextLayout.Draw(_canvas, layout, font, paint, new SKPoint(rect.Left, rect.Top), rect.Width, Align(slot, SKTextAlign.Left));
            }
            finally
            {
                _canvas.RestoreToCount(save);
            }
        }

        private void DrawAttachedCaption(TextSlot slot, SKRect rect)
        {
            var caption = CaptionForAttachedSlot(slot);
            if (string.IsNullOrWhiteSpace(caption)) return;

            var font = CaptionFont();
            var lineHeight = _style.CaptionText?.LineHeight ?? 1.25;
            var layout = TextLayout.Layout([caption], font, lineHeight, rect.Width, rect.Height,
                ellipsize: true, maxLines: MaxCaptionLines);
            if (layout.IsEmpty) return;

            if (layout.Truncated || layout.Overflowed)
            {
                Report(RenderDiagnosticKind.CaptionTruncated, RenderSeverity.Warning,
                    "This caption is longer than its text slot and was shortened.", slot.Id);
            }

            if (OverlapsPlacedPhoto(slot))
            {
                DrawPanelScrim(InkedBounds(layout, rect, Align(slot, SKTextAlign.Left)), slot);
            }

            var save = _canvas.Save();
            try
            {
                _canvas.ClipRect(rect, SKClipOperation.Intersect, antialias: false);
                using var paint = TextPaint(_style.CaptionText?.Color);
                TextLayout.Draw(_canvas, layout, font, paint, new SKPoint(rect.Left, rect.Top), rect.Width, Align(slot, SKTextAlign.Left));
            }
            finally
            {
                _canvas.RestoreToCount(save);
            }
        }

        /// <summary>
        /// The chapter's month title (R24, doc 10 §7) — Playfair Display 64 pt by default, an order of
        /// magnitude above journal text. When the title overlaps a photo it gets the §4 scrim treatment
        /// automatically so it stays readable on whatever image the engine chose.
        /// </summary>
        private void DrawMonthTitle(TextSlot slot, SKRect rect)
        {
            var title = MonthTitleText();
            if (string.IsNullOrWhiteSpace(title)) return;

            var font = MonthTitleFont();
            var lineHeight = _style.MonthTitle?.LineHeight ?? 1.0;
            var align = Align(slot, SKTextAlign.Center);
            var layout = TextLayout.Layout([title], font, lineHeight, rect.Width, rect.Height);
            if (layout.IsEmpty) return;

            if (layout.Overflowed)
            {
                Report(RenderDiagnosticKind.TextOverflow, RenderSeverity.Error,
                    $"The month title does not fit text slot '{slot.Id}' at {font.Size / _geo.PointScale:0.#} pt.", slot.Id);
            }

            SKFont? subtitleFont = null;
            var subtitle = _request.ShowYearSubtitle && _request.Book.Year > 0
                ? _request.Book.Year.ToString(CultureInfo.InvariantCulture)
                : null;
            var subtitleGap = _geo.Points(MonthTitleSubtitleGapPt);
            var subtitleHeight = 0f;
            if (subtitle is not null)
            {
                subtitleFont = SubtitleFont();
                subtitleHeight = subtitleGap + subtitleFont.Size;
            }

            var blockHeight = layout.Height + subtitleHeight;
            var top = rect.Top + Math.Max(0, (rect.Height - blockHeight) / 2f);

            if (OverlapsPlacedPhoto(slot))
            {
                // The subtitle sits under the title and can be wider than nothing but never wider than
                // it, so the title's own inked span is the panel width.
                var span = InkedBounds(layout, rect, align);
                DrawPanelScrim(new SKRect(span.Left, top, span.Right, top + blockHeight), slot);
            }

            using var paint = TextPaint(_style.MonthTitle?.Color);
            TextLayout.Draw(_canvas, layout, font, paint, new SKPoint(rect.Left, top), rect.Width, align);

            if (subtitle is not null && subtitleFont is not null)
            {
                var baseline = top + layout.Height + subtitleGap - subtitleFont.Metrics.Ascent;
                using var subtitlePaint = TextPaint(_style.CaptionText?.Color);
                TextLayout.DrawTracked(_canvas, subtitle, subtitleFont, subtitlePaint,
                    new SKPoint(rect.Left, baseline), MonthTitleSubtitleTrackingEm, rect.Width, align);
            }
        }

        private bool OverlapsPlacedPhoto(TextSlot slot) =>
            _request.Template.Slots.Any(s =>
                s.Rect.Intersects(slot.Rect) && _request.Page.PlacementFor(s.Id) is { } p && !string.IsNullOrEmpty(p.PhotoId));

        // ---- editor-only overlays ---------------------------------------------------------------

        /// <summary>
        /// The amber empty-slot flag of doc 09 §3.6 (R14): a 2 px dashed <c>#FFB300</c> inset border, a
        /// 12% amber fill and a centered photo-plus glyph. <b>Screen only</b> — it must never print.
        /// </summary>
        private void DrawEmptySlotFlag(SKRect rect)
        {
            if (!_request.EmptySlotFlagsVisible || rect.Width <= 0 || rect.Height <= 0) return;

            using var fill = new SKPaint
            {
                Color = SkiaColors.Amber.WithOpacity(EmptySlotFillOpacity),
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
            };
            _canvas.DrawRect(rect, fill);

            var stroke = Math.Max(1f, _geo.Points(EmptySlotStrokePt));
            using var dash = SKPathEffect.CreateDash([stroke * 3f, stroke * 2f], 0);
            using var border = new SKPaint
            {
                Color = SkiaColors.Amber,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = stroke,
                PathEffect = dash,
                IsAntialias = true,
            };
            var inset = stroke / 2f;
            _canvas.DrawRect(new SKRect(rect.Left + inset, rect.Top + inset, rect.Right - inset, rect.Bottom - inset), border);

            // The photo-plus glyph is drawn as geometry rather than typed, so it cannot depend on a font.
            var size = Math.Min(rect.Width, rect.Height) * 0.18f;
            if (size < stroke * 3) return;
            using var glyph = new SKPaint
            {
                Color = SkiaColors.Amber,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = stroke,
                StrokeCap = SKStrokeCap.Round,
                IsAntialias = true,
            };
            var cx = rect.MidX;
            var cy = rect.MidY;
            var half = size / 2f;
            _canvas.DrawRect(new SKRect(cx - size, cy - half * 1.2f, cx + size, cy + half * 1.2f), glyph);
            _canvas.DrawLine(cx - half * 0.5f, cy, cx + half * 0.5f, cy, glyph);
            _canvas.DrawLine(cx, cy - half * 0.5f, cx, cy + half * 0.5f, glyph);
        }

        /// <summary>The doc 12 draft watermark: diagonal DRAFT in the caption face at 20% white.</summary>
        private void DrawWatermark(SKRect region)
        {
            const string text = "DRAFT";
            var typeface = _request.Fonts.Resolve(_style.CaptionText?.Family, TextRole.Caption, _style.CaptionText?.Weight);
            using var font = new SKFont(typeface, region.Height * 0.22f);
            using var paint = new SKPaint
            {
                Color = SKColors.White.WithOpacity(0.2),
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
            };

            var save = _canvas.Save();
            try
            {
                _canvas.Translate(region.MidX, region.MidY);
                _canvas.RotateDegrees(-30);
                _canvas.DrawText(text, 0, font.Size * 0.35f, SKTextAlign.Center, font, paint);
            }
            finally
            {
                _canvas.RestoreToCount(save);
            }
        }

        private void DrawGuides()
        {
            using var dash = SKPathEffect.CreateDash([4f, 4f], 0);
            using var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f,
                PathEffect = dash,
                IsAntialias = false,
                Color = SKColors.Cyan.WithOpacity(0.5),
            };
            _canvas.DrawRect(_geo.TrimRect, paint);
            paint.Color = SKColors.Lime.WithOpacity(0.5);
            _canvas.DrawRect(_geo.SafeRect, paint);
            if (_geo.Surface == PageSurface.Spread)
            {
                paint.Color = SKColors.Magenta.WithOpacity(0.5);
                _canvas.DrawRect(_geo.GutterCautionRect, paint);
            }
        }

        // ---- content lookups --------------------------------------------------------------------

        private IReadOnlyList<string> JournalParagraphs(string textSlotId)
        {
            var assignment = _request.Page.JournalAssignments
                .FirstOrDefault(a => string.Equals(a.TextSlotId, textSlotId, StringComparison.Ordinal));
            if (assignment is null || assignment.EntryIds.Count == 0) return [];

            var paragraphs = new List<string>();
            foreach (var entryId in assignment.EntryIds)
            {
                var entry = _request.Journal.Entries
                    .FirstOrDefault(e => string.Equals(e.Id, entryId, StringComparison.Ordinal));
                if (entry is null || entry.Excluded) continue;
                foreach (var paragraph in entry.Paragraphs)
                {
                    if (paragraph is not null) paragraphs.Add(paragraph);
                }
            }

            return paragraphs;
        }

        private string? CaptionForAttachedSlot(TextSlot slot)
        {
            if (string.IsNullOrEmpty(slot.AttachedTo)) return null;
            var placement = _request.Page.PlacementFor(slot.AttachedTo);
            return placement is null ? null : _request.Photos.Find(placement.PhotoId)?.Caption;
        }

        private string MonthTitleText()
        {
            if (!string.IsNullOrWhiteSpace(_request.Chapter?.Title)) return _request.Chapter!.Title!;
            var month = _request.Chapter?.Month ?? 0;
            if (month is < 1 or > 12) return string.Empty;
            var name = CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(month);
            return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name);
        }

        private static SKTextAlign Align(TextSlot slot, SKTextAlign fallback) => slot.Align switch
        {
            TextAlign.Center => SKTextAlign.Center,
            TextAlign.Right => SKTextAlign.Right,
            TextAlign.Left => SKTextAlign.Left,
            _ => fallback,
        };

        // ---- fonts and paints -------------------------------------------------------------------

        private SKFont JournalFont() => Track(_request.Fonts.CreateFont(_style.JournalText, TextRole.Journal, _geo.PointScale, 10.5));

        private SKFont CaptionFont() => Track(_request.Fonts.CreateFont(_style.CaptionText, TextRole.Caption, _geo.PointScale, 8.5));

        private SKFont MonthTitleFont() => Track(_request.Fonts.CreateFont(_style.MonthTitle, TextRole.MonthTitle, _geo.PointScale, 64));

        private SKFont SubtitleFont()
        {
            var style = _style.CaptionText is null
                ? new TextStyle { SizePt = MonthTitleSubtitleSizePt }
                : _style.CaptionText with { SizePt = MonthTitleSubtitleSizePt };
            return Track(_request.Fonts.CreateFont(style, TextRole.Caption, _geo.PointScale, MonthTitleSubtitleSizePt));
        }

        private SKFont Track(SKFont font)
        {
            _fonts.Add(font);
            return font;
        }

        private SKPaint TextPaint(string? color) => new()
        {
            Color = SkiaColors.Parse(color, SkiaColors.Text),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };

        private void ReportFontSubstitutions()
        {
            ReportFamily(_style.JournalText?.Family ?? BuiltInStyles.JournalFamily, TextRole.Journal, _style.JournalText?.Weight);
            ReportFamily(_style.CaptionText?.Family ?? BuiltInStyles.CaptionFamily, TextRole.Caption, _style.CaptionText?.Weight);
            if (_request.Template.TextSlots.Any(t => t.Role == TextRole.MonthTitle))
                ReportFamily(_style.MonthTitle?.Family ?? BuiltInStyles.MonthTitleFamily, TextRole.MonthTitle, _style.MonthTitle?.Weight);
        }

        private void ReportFamily(string family, TextRole role, string? weight)
        {
            _request.Fonts.Resolve(family, role, weight);
            var resolution = _request.Fonts.ResolutionFor(family);
            if (resolution is null || resolution.IsExactMatch) return;
            Report(RenderDiagnosticKind.FontFallback, RenderSeverity.Info, resolution.Describe());
        }

        // ---- diagnostics ------------------------------------------------------------------------

        private void Report(
            RenderDiagnosticKind kind, RenderSeverity severity, string message,
            string? slotId = null, string? photoId = null, double? value = null) =>
            _diagnostics.Add(new RenderDiagnostic(
                kind, severity, message, _request.BookPageNumber, _request.Page.Id, slotId, photoId, value));
    }
}
