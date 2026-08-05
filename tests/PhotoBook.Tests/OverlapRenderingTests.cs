using PhotoBook.Core.Model;
using PhotoBook.Core.Templates;
using PhotoBook.Imaging;
using PhotoBook.Rendering;
using SkiaSharp;

namespace PhotoBook.Tests;

/// <summary>
/// The renderer's half of doc 07's "Deliberate overlap". The template library declares which slots
/// sit on top of which (<see cref="ImageSlot.Layer"/>) and which text blocks sit on a photo
/// (<see cref="TextSlot.Scrim"/>); those declarations mean nothing until the draw path honors them,
/// and nothing in the geometry tests can tell the difference — a template linted clean, laid out
/// clean, and still rendered its journal as white-on-white.
/// <para>
/// So these tests read pixels. Every photo here is a flat, known color, which makes "which frame is
/// on top" and "is this area darkened" both decidable to the byte.
/// </para>
/// </summary>
public sealed class OverlapRenderingTests
{
    private const int WidthPx = 880;
    private const int HeightPx = 680;

    private static readonly SKColor Under = new(255, 0, 0);
    private static readonly SKColor Over = new(0, 0, 255);
    private static readonly SKColor PageWhite = new(255, 255, 255);

    [Fact]
    public void TheSlotOnTheHigherLayerIsPaintedOverTheOneBeneathIt()
    {
        // Authored the wrong way round on purpose: in reading order the background covers the inset,
        // so if the renderer drew Slots rather than SlotsInPaintOrder the inset would vanish.
        var template = new Template
        {
            Id = "t-test-overlap",
            Name = "Inset over background",
            Kind = TemplateKind.Standard,
            PhotoCount = 2,
            Overlaps = true,
            Slots =
            [
                new ImageSlot { Id = "s2", Rect = new Rect(0.55, 0.55, 0.35, 0.35), Aspect = 1.0, Layer = 1 },
                new ImageSlot { Id = "s1", Rect = new Rect(0.05, 0.05, 0.90, 0.90), Aspect = 1.294, Layer = 0 },
            ],
        };

        var page = Page(template, ("s1", "p-under"), ("s2", "p-over"));
        var preview = Render(template, page);

        // The inset's own middle shows the inset; the background's far corner still shows the background.
        var inset = preview.Result.SlotRects["s2"];
        var background = preview.Result.SlotRects["s1"];
        Assert.Equal(Over, PixelAt(preview, inset.MidX, inset.MidY));
        Assert.Equal(Under, PixelAt(preview, background.Left + 8, background.Top + 8));

        // And the overlap reads as depth: the band just outside the inset's lower edge is darkened by
        // the lift, so two photos of similar tone cannot merge into one shape at the seam.
        var lift = PixelAt(preview, inset.MidX, inset.Bottom + 3);
        Assert.True(lift.Red < Under.Red - 30,
            $"no lift shadow under the overlapping slot (found #{lift.Red:X2}{lift.Green:X2}{lift.Blue:X2})");

        // Well clear of it, the photo underneath is its own colour again — the lift is a seam, not a vignette.
        Assert.Equal(Under, PixelAt(preview, inset.Left - 40, inset.MidY));
    }

    [Fact]
    public void ALayerZeroPhotoCastsNoShadow()
    {
        // The lift belongs to the templates that opted into overlap. An ordinary grid must render
        // exactly as it always did — a shadow around every photo would be a Style the user chose (R23).
        var template = new Template
        {
            Id = "t-test-flat",
            Name = "Two up",
            Kind = TemplateKind.Standard,
            PhotoCount = 2,
            Slots =
            [
                new ImageSlot { Id = "s1", Rect = new Rect(0.05, 0.05, 0.40, 0.40), Aspect = 1.294 },
                new ImageSlot { Id = "s2", Rect = new Rect(0.55, 0.05, 0.40, 0.40), Aspect = 1.294 },
            ],
        };

        var preview = Render(template, Page(template, ("s1", "p-under"), ("s2", "p-over")));
        var first = preview.Result.SlotRects["s1"];
        Assert.Equal(PageWhite, PixelAt(preview, first.MidX, first.Bottom + 3));
        Assert.Equal(PageWhite, PixelAt(preview, first.Right + 3, first.MidY));
    }

    [Fact]
    public void JournalTextOnAPhotoIsDarkenedBehindTheWordsAndNowhereElse()
    {
        var template = TextOnPhoto(scrim: true);
        var page = Page(template, ("s1", "p-under"));
        page.JournalAssignments.Add(new JournalAssignment { TextSlotId = "t1", EntryIds = ["e1"] });

        var preview = Render(template, page, Journal());
        var photo = preview.Result.SlotRects["s1"];
        var text = preview.Result.TextSlotRects["t1"];

        // Behind the words the photo is materially darker. Measured as a mean over the block rather
        // than at a point, because a point lands either on a glyph or between two of them.
        var behind = MeanRed(preview, WrittenBand(text));
        Assert.True(behind < Under.Red - 60,
            $"the scrim did not darken the photo behind the journal (mean red {behind:0})");

        // The same photo, well away from the text: untouched. A scrim sized to the slot rather than to
        // the words would be a dark plate across a photo the user chose to show.
        Assert.Equal(Under, PixelAt(preview, photo.Right - 6, photo.Top + 6));
    }

    [Fact]
    public void TheScrimStopsAtTheEdgeOfThePhotoItBelongsTo()
    {
        // A hand-edited Detached page can hang a text block half off its photo — the linter cannot stop
        // that, since it only lints library templates — so the renderer clips the darkening to the
        // photos the text actually touches. The page around them keeps its own background colour.
        var template = TextOnPhoto(scrim: true);
        template.Slots[0].Rect = new Rect(0.05, 0.05, 0.42, 0.90);
        template.TextSlots[0].Rect = new Rect(0.20, 0.55, 0.60, 0.30);

        var page = Page(template, ("s1", "p-under"));
        page.JournalAssignments.Add(new JournalAssignment { TextSlotId = "t1", EntryIds = ["e1"] });

        var preview = Render(template, page, Journal());
        var photo = preview.Result.SlotRects["s1"];
        var text = preview.Result.TextSlotRects["t1"];

        Assert.True(MeanRed(preview, SKRect.Intersect(photo, WrittenBand(text))) < Under.Red - 60, "the scrim missed the photo");
        Assert.Equal(PageWhite, PixelAt(preview, photo.Right + 14, text.MidY));
    }

    [Fact]
    public void AJournalSlotThatTouchesNoPhotoGetsNoScrim()
    {
        var template = TextOnPhoto(scrim: true);
        template.Slots[0].Rect = new Rect(0.05, 0.05, 0.90, 0.42);
        template.TextSlots[0].Rect = new Rect(0.10, 0.60, 0.50, 0.30);

        var page = Page(template, ("s1", "p-under"));
        page.JournalAssignments.Add(new JournalAssignment { TextSlotId = "t1", EntryIds = ["e1"] });

        var preview = Render(template, page, Journal());
        var text = preview.Result.TextSlotRects["t1"];

        // No photo under these words, so no plate behind them: the page is exactly its own colour
        // wherever a glyph is not.
        Assert.Equal(PageWhite, PixelAt(preview, text.Left + 2, text.Top - 6));
        Assert.Equal(PageWhite, PixelAt(preview, text.Right - 2, text.Bottom + 6));
    }

    [Fact]
    public void EverySlotTheShippedLibraryStacksHasSomewhereToStack()
    {
        // The renderer's contract with the library: a slot above layer zero is only meaningful if
        // something it overlaps sits below it. This is the pairing the linter's L2 enforces, restated
        // from the draw path's point of view so a future re-authoring cannot quietly break paint order.
        foreach (var template in TemplateLibrary.Default.Templates)
        {
            var order = template.SlotsInPaintOrder.ToList();
            Assert.Equal(template.Slots.Count, order.Count);
            Assert.True(
                order.Select(s => s.Layer).SequenceEqual(order.Select(s => s.Layer).Order()),
                $"{template.Id} does not paint in ascending layer order");

            foreach (var slot in template.Slots.Where(s => s.Layer > 0))
            {
                Assert.True(
                    template.Slots.Any(other => other.Layer < slot.Layer && other.Rect.Intersects(slot.Rect)),
                    $"{template.Id}/{slot.Id} is lifted onto layer {slot.Layer} but overlaps nothing beneath it");
            }
        }
    }

    // ---- fixtures --------------------------------------------------------------------------------

    private static Template TextOnPhoto(bool scrim) => new()
    {
        Id = "t-test-scrim",
        Name = "Hero with journal",
        Kind = TemplateKind.Standard,
        PhotoCount = 1,
        Overlaps = true,
        Slots = [new ImageSlot { Id = "s1", Rect = new Rect(0.05, 0.05, 0.90, 0.90), Aspect = 1.294 }],
        TextSlots =
        [
            new TextSlot { Id = "t1", Rect = new Rect(0.10, 0.55, 0.35, 0.30), Role = TextRole.Journal, Scrim = scrim },
        ],
    };

    private static Page Page(Template template, params (string Slot, string Photo)[] placements)
    {
        var page = new Page { Id = "pg-test", TemplateRef = template.Id };
        foreach (var (slot, photo) in placements)
        {
            page.Placements.Add(new Placement { SlotId = slot, PhotoId = photo, Crop = CropState.Default });
        }

        return page;
    }

    private static JournalDocument Journal() => new()
    {
        Entries =
        [
            new JournalEntry
            {
                Id = "e1",
                DateStart = new DateOnly(2024, 4, 3),
                DateEnd = new DateOnly(2024, 4, 3),
                Paragraphs = [string.Concat(Enumerable.Repeat("The creek was cold and the heron did not move. ", 6))],
            },
        ],
    };

    private static PagePreview Render(Template template, Page page, JournalDocument? journal = null)
    {
        var catalog = new PhotoCatalog
        {
            Photos =
            [
                new Photo { Id = "p-under", OriginalFileName = "under.jpg", Width = 1600, Height = 1200 },
                new Photo { Id = "p-over", OriginalFileName = "over.jpg", Width = 1600, Height = 1200 },
            ],
        };

        using var images = new FlatColorImageSource(
            new Dictionary<string, SKColor>(StringComparer.Ordinal) { ["p-under"] = Under, ["p-over"] = Over });

        // A white page, not the shipped black one (R21). On black, "the scrim did not spill here" is
        // unfalsifiable — black over black is black — so these tests colour the sheet and read it back.
        var style = BuiltInStyles.Default;
        style.Background = new BackgroundStyle { Kind = BackgroundKind.Solid, Color = "#FFFFFF" };

        return PagePreviewRenderer.Render(
            new PageRenderRequest
            {
                Book = new Book { Id = "bk-test", Year = 2024, PageSize = PageGeometry.DefaultPageSizeId },
                Page = page,
                Template = template,
                Geometry = PageGeometryMapper.Create(
                    BuiltInPrintProfiles.Generic, PageGeometry.DefaultPageSizeId, PageGeometry.PointsPerInch),
                Images = images,
                Photos = catalog,
                Journal = journal ?? JournalDocument.Empty,
                ResolvedStyle = style,
                Target = RenderTarget.Screen,
                ShowEmptySlotFlags = false,
            },
            WidthPx,
            HeightPx,
            PageWhite);
    }

    /// <summary>
    /// The top of a journal slot, where the words of a short entry actually land. Averaging over the
    /// whole slot would dilute the scrim with the empty page below the last line — which is the point
    /// of sizing the scrim to the text rather than to the box.
    /// </summary>
    private static SKRect WrittenBand(SKRect textSlot) =>
        new(textSlot.Left, textSlot.Top, textSlot.Right, textSlot.Top + (textSlot.Height * 0.30f));

    /// <summary>The colour of one device pixel of the rendered buffer.</summary>
    private static SKColor PixelAt(PagePreview preview, float deviceX, float deviceY)
    {
        var image = preview.Image;
        var x = Math.Clamp((int)Math.Round(deviceX), 0, image.Width - 1);
        var y = Math.Clamp((int)Math.Round(deviceY), 0, image.Height - 1);
        var i = ((y * image.Width) + x) * DecodedImage.BytesPerPixel;
        var span = image.AsSpan();
        return new SKColor(span[i + 2], span[i + 1], span[i]);
    }

    /// <summary>Mean red channel over a device rect — robust to landing on or between glyphs.</summary>
    private static double MeanRed(PagePreview preview, SKRect rect)
    {
        var image = preview.Image;
        var span = image.AsSpan();
        double sum = 0;
        var count = 0;
        for (var y = (int)rect.Top; y < (int)rect.Bottom; y++)
        {
            if (y < 0 || y >= image.Height) continue;
            for (var x = (int)rect.Left; x < (int)rect.Right; x++)
            {
                if (x < 0 || x >= image.Width) continue;
                sum += span[(((y * image.Width) + x) * DecodedImage.BytesPerPixel) + 2];
                count++;
            }
        }

        return count == 0 ? 0 : sum / count;
    }

    /// <summary>An image source that hands back one flat color per photo id, so pixels are decidable.</summary>
    private sealed class FlatColorImageSource : IRenderImageSource, IDisposable
    {
        private readonly Dictionary<string, SKColor> _colors;
        private readonly Dictionary<string, RenderImage> _images = new(StringComparer.Ordinal);

        public FlatColorImageSource(Dictionary<string, SKColor> colors) => _colors = colors;

        public RenderImage? GetImage(RenderImageRequest request)
        {
            if (_images.TryGetValue(request.PhotoId, out var cached)) return cached;
            if (!_colors.TryGetValue(request.PhotoId, out var color)) return null;

            var info = new SKImageInfo(800, 600, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var surface = SKSurface.Create(info);
            surface.Canvas.Clear(color);
            var render = new RenderImage(surface.Snapshot(), 1600, 1200);
            _images[request.PhotoId] = render;
            return render;
        }

        public void Dispose()
        {
            foreach (var image in _images.Values) image.Image.Dispose();
            _images.Clear();
        }
    }
}
