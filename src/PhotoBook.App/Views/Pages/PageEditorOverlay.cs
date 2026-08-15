using System.Windows;
using System.Windows.Media;
using PhotoBook.App.Controls;
using PageSide = PhotoBook.Core.Model.PageSide;

namespace PhotoBook.App.Views.Pages;

/// <summary>
/// The non-interactive layer drawn over a <c>PageCanvas</c>: crop-mode guides (doc 09 §3.3 — the
/// rule-of-thirds grid, the photo's Focus Regions ghosted, and the letterbox outline that makes
/// <c>zoom &lt; 1</c> visible, R9) and the Spread view's gutter caution band (doc 09 §3.1).
/// <para>
/// It owns no geometry of its own: the view feeds it rects in control pixels that the canvas
/// computed from the renderer's own <c>SlotRects</c>, so the guides can never disagree with the
/// pixels underneath them.
/// </para>
/// </summary>
public sealed class PageEditorOverlay : FrameworkElement
{
    private Rect _slotRect = Rect.Empty;
    private Rect _imageRect = Rect.Empty;
    private IReadOnlyList<Rect> _focusRects = [];
    private bool _showCropGuides;
    private bool _letterboxed;

    private Rect _pageRect = Rect.Empty;
    private PageSide? _gutterSide;
    private double _gutterWidth;

    /// <summary>Creates the overlay. It never takes the pointer — the canvas below always does.</summary>
    public PageEditorOverlay()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    /// <summary>
    /// Sets the crop-mode guides for the slot being edited, in control pixels.
    /// </summary>
    /// <param name="slotRect">The slot's rect, from <c>PageCanvas.SlotRectInControl</c>.</param>
    /// <param name="imageRect">Where the whole image lands (<c>CropMath.ImageRect</c>).</param>
    /// <param name="focusRects">The photo's focus regions mapped into control pixels (R25).</param>
    /// <param name="letterboxed">True when zoom &lt; 1 and the page background shows through (R9).</param>
    public void ShowCrop(Rect slotRect, Rect imageRect, IReadOnlyList<Rect> focusRects, bool letterboxed)
    {
        _slotRect = slotRect;
        _imageRect = imageRect;
        _focusRects = focusRects ?? [];
        _letterboxed = letterboxed;
        _showCropGuides = !slotRect.IsEmpty && slotRect.Width > 8 && slotRect.Height > 8;
        InvalidateVisual();
    }

    /// <summary>Clears the crop guides.</summary>
    public void ClearCrop()
    {
        if (!_showCropGuides)
        {
            return;
        }

        _showCropGuides = false;
        _focusRects = [];
        InvalidateVisual();
    }

    /// <summary>
    /// Sets the gutter caution band: the half inch of page next to the spine, hatched, on the side
    /// this page faces (doc 09 §3.1). Pass a null side to hide it.
    /// </summary>
    /// <param name="pageRect">The sheet's rect in control pixels.</param>
    /// <param name="side">Which side of the spread this page is, or null to hide the band.</param>
    /// <param name="width">The band's width in control pixels.</param>
    public void SetGutter(Rect pageRect, PageSide? side, double width)
    {
        _pageRect = pageRect;
        _gutterSide = side;
        _gutterWidth = width;
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);
        base.OnRender(dc);

        DrawGutter(dc);

        if (_showCropGuides)
        {
            DrawCropGuides(dc);
        }
    }

    private void DrawGutter(DrawingContext dc)
    {
        if (_gutterSide is not { } side || _gutterWidth <= 1 || _pageRect.IsEmpty || _pageRect.Height <= 0)
        {
            return;
        }

        var width = Math.Min(_gutterWidth, _pageRect.Width);
        var band = side == PageSide.Left
            ? new Rect(_pageRect.Right - width, _pageRect.Y, width, _pageRect.Height)
            : new Rect(_pageRect.X, _pageRect.Y, width, _pageRect.Height);

        var hatch = Palette("WarningBrush", "#FFE0A33C");
        dc.DrawRectangle(Palette("WarningWashBrush", "#1FE0A33C"), null, band);

        dc.PushClip(new RectangleGeometry(band));
        dc.PushOpacity(0.22);
        var pen = new Pen(hatch, 1);
        pen.Freeze();

        // 45° hatching: one pass of parallel lines is enough to read as "keep clear".
        for (var x = band.X - band.Height; x < band.Right; x += 9)
        {
            dc.DrawLine(pen, new Point(x, band.Bottom), new Point(x + band.Height, band.Y));
        }

        dc.Pop();
        dc.Pop();

        // The spine itself is the sheet's own trim edge, drawn by PageCanvas: one owner for the page
        // boundary, so a dull line here can never sit on top of the crisp one there.
    }

    private void DrawCropGuides(DrawingContext dc)
    {
        var slot = _slotRect;
        dc.PushClip(new RectangleGeometry(slot));

        // The letterbox: when zoom < 1 the image sits inside the slot and the page background shows
        // through (R9). Outlining the image makes that unmistakable rather than looking like a bug.
        if (_letterboxed && !_imageRect.IsEmpty && _imageRect.Width > 2 && _imageRect.Height > 2)
        {
            var amber = Palette("AmberFlagBrush", "#FFFFB300");
            var pen = new Pen(amber, 1)
            {
                DashStyle = new DashStyle([3, 3], 0),
            };

            dc.DrawRectangle(null, pen, _imageRect);
        }

        // Thirds are a guide, not a graphic: the palette's text colour at a third of its weight.
        var thirds = new Pen(Palette("TextPrimaryBrush", "#FFECEFF3"), 1);
        thirds.Freeze();

        dc.PushOpacity(0.35);
        for (var i = 1; i <= 2; i++)
        {
            var x = slot.X + (slot.Width * i / 3.0);
            var y = slot.Y + (slot.Height * i / 3.0);
            dc.DrawLine(thirds, new Point(x, slot.Y), new Point(x, slot.Bottom));
            dc.DrawLine(thirds, new Point(slot.X, y), new Point(slot.Right, y));
        }

        dc.Pop();

        // Focus regions, ghosted: what the engine was protecting when it chose this crop (R25).
        if (_focusRects.Count > 0)
        {
            var focus = new Pen(Palette("FocusRingBrush", "#FF7FB0FF"), 1.2)
            {
                DashStyle = new DashStyle([2, 3], 0),
            };

            foreach (var rect in _focusRects)
            {
                if (rect.Width > 3 && rect.Height > 3)
                {
                    dc.DrawRectangle(null, focus, rect);
                }
            }
        }

        dc.Pop();

        // The crop frame lands on exactly the rect PageCanvas already outlined for the selection, in
        // exactly the same language (SelectionChrome) — one frame on screen, drawn brighter because
        // crop mode is the stronger state. The square corner grips underneath show through it.
        SelectionChrome.DrawFrame(
            dc, slot, Palette("AccentBrush", "#FF4C8DF5"), Palette("ScrimBrush", "#99000000"), 2);
    }

    /// <summary>
    /// Resolves a palette brush. The literal only fires outside the app's resource scope (the XAML
    /// designer); inside the app <c>Themes/Palette.xaml</c> always wins, so colour still lives there.
    /// </summary>
    private Brush Palette(string key, string fallback)
    {
        if (TryFindResource(key) is Brush brush)
        {
            return brush;
        }

        var solid = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
        solid.Freeze();
        return solid;
    }
}
