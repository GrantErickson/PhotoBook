using System.Globalization;
using PhotoBook.Core.Model;
using PhotoBook.Core.Templates;
using PhotoBook.Engine;
using PhotoBook.Core.Persistence;
using PhotoBook.Imaging;
using PhotoBook.Rendering;
using PhotoBook.Tests.Fixtures;
using SkiaSharp;

namespace PhotoBook.Tests;

/// <summary>
/// Writes real rendered pages to PHOTOBOOK_VISUAL_DUMP so a human (or the agent building this) can
/// look at what the engine actually produces. Skipped unless that variable is set, so it never runs
/// in a normal test pass.
/// <para>
/// Two dumps, because they answer different questions. <see cref="DumpChapterPages"/> shows what the
/// <em>engine</em> chose for a month — page count, template mix, how full the pages it picked are.
/// <see cref="DumpTemplateLibrary"/> shows every template in the library filled to capacity, which is
/// the only way to see the ones the engine happened not to pick, and the only way to eyeball a
/// deliberate overlap or a scrimmed text block that this month's photos never triggered.
/// </para>
/// <para>
/// Both draw through <see cref="PhotographicImageSource"/> rather than the analysis fixtures. The
/// analysis fixtures are smooth gradients with a checkerboard patch on purpose — that is what makes
/// the sharpness and saliency assertions mean something — but a flat gradient cannot answer "is white
/// text legible on this photo", which is exactly what the scrim exists for.
/// </para>
/// </summary>
[Collection(SyntheticMonthCollection.Name)]
public sealed class VisualDumpTests
{
    private const int PageWidthPx = 1100;
    private const int PageHeightPx = 850;

    private readonly SyntheticMonth _month;

    public VisualDumpTests(SyntheticMonth month) => _month = month;

    [Fact]
    public void DumpChapterPages()
    {
        var outDir = OutputFolder("pages");
        if (outDir is null) return;

        var book = LayoutEngine.LayoutChapter(new LayoutRequest
        {
            Chapter = new ChapterInput
            {
                Year = SyntheticMonth.Year,
                Month = SyntheticMonth.Month,
                Photos = [.. _month.Catalog.Photos],
                JournalEntries = [.. _month.Journal.EntriesIn(SyntheticMonth.Year, SyntheticMonth.Month)],
            },
            Style = StyleResolver.Resolve(_month.Book, null, null),
            Seed = SyntheticMonth.Seed,
        });

        var project = _month.SnapshotWith(book.Pages);
        using var images = new PhotographicImageSource(_month.Catalog);

        var summary = new List<string>
        {
            $"pages={book.Pages.Count} photos={_month.Catalog.Photos.Count} unplaced={book.UnplacedPhotoIds.Count}",
            "",
            $"{"page",4} {"template",-16} {"photos",6} {"cov",6} {"ov",3} {"scrim",5}  diagnostics",
        };

        var coverages = new List<double>();

        for (var i = 0; i < book.Pages.Count; i++)
        {
            var page = book.Pages[i];
            var template = page.ResolveTemplate(id => TemplateLibrary.Default.Find(id))
                           ?? throw new InvalidOperationException($"unresolved template '{page.TemplateRef}'");

            var preview = RenderPage(project.Book, project.Chapters[0], page, template, images, project);
            Save(preview.Image, Path.Combine(outDir, $"page-{i + 1:00}.png"));

            var coverage = TemplateLinter.PageCoverage(template);
            coverages.Add(coverage);
            summary.Add(
                $"{i + 1,4} {page.TemplateRef,-16} {page.Placements.Count,6} {coverage,6:0.000} " +
                $"{(template.Overlaps ? "yes" : "-"),3} {(template.TextSlots.Any(t => t.Scrim) ? "yes" : "-"),5}  " +
                Describe(preview.Result.Diagnostics));
        }

        summary.Add("");
        summary.Add(string.Create(CultureInfo.InvariantCulture,
            $"mean page coverage = {coverages.Average():0.000}  min = {coverages.Min():0.000}  max = {coverages.Max():0.000}"));
        File.WriteAllLines(Path.Combine(outDir, "summary.txt"), summary);
    }

    [Fact]
    public void DumpTemplateLibrary()
    {
        var outDir = OutputFolder("templates");
        if (outDir is null) return;

        using var images = new PhotographicImageSource(_month.Catalog);
        var photos = _month.MonthPhotos;
        // The month's longest entry, deliberately: a text-led template's whole justification is the
        // journal it can hold, and filling it with a one-liner would show it as a page of black.
        var entry = _month.Journal.EntriesIn(SyntheticMonth.Year, SyntheticMonth.Month)
            .MaxBy(e => e.Paragraphs.Sum(p => p?.Length ?? 0))!;
        var chapter = new Chapter { Year = SyntheticMonth.Year, Month = SyntheticMonth.Month };

        var summary = new List<string>
        {
            $"{"template",-16} {"kind",-11} {"n",2} {"cov",6} {"ov",3} {"scrim",5}  diagnostics",
        };

        var index = 0;
        foreach (var template in TemplateLibrary.Default.Templates.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            var page = new Page { Id = $"pg-{template.Id}", TemplateRef = template.Id };
            foreach (var slot in template.Slots)
            {
                page.Placements.Add(new Placement
                {
                    SlotId = slot.Id,
                    PhotoId = photos[index++ % photos.Count].Id,
                    Crop = CropState.Default,
                });
            }

            foreach (var text in template.JournalSlots)
            {
                page.JournalAssignments.Add(new JournalAssignment { TextSlotId = text.Id, EntryIds = [entry.Id] });
            }

            var project = new ProjectSnapshot(_month.Book, _month.Catalog, _month.Journal, [chapter]);
            var preview = RenderPage(_month.Book, chapter, page, template, images, project);
            Save(preview.Image, Path.Combine(outDir, $"{template.Id}.png"));

            summary.Add(
                $"{template.Id,-16} {template.Kind,-11} {template.PhotoCount,2} " +
                $"{TemplateLinter.PageCoverage(template),6:0.000} {(template.Overlaps ? "yes" : "-"),3} " +
                $"{(template.TextSlots.Any(t => t.Scrim) ? "yes" : "-"),5}  " +
                Describe(preview.Result.Diagnostics));
        }

        File.WriteAllLines(Path.Combine(outDir, "summary.txt"), summary);
    }

    // ---- plumbing --------------------------------------------------------------------------------

    private static PagePreview RenderPage(
        Book book, Chapter chapter, Page page, Template template,
        IRenderImageSource images, ProjectSnapshot project) =>
        PagePreviewRenderer.Render(
            new PageRenderRequest
            {
                Book = book,
                Chapter = chapter,
                Page = page,
                Template = template,
                Geometry = PageGeometryMapper.Create(
                    BuiltInPrintProfiles.Generic, book.PageSize, PageGeometry.PointsPerInch),
                Images = images,
                Photos = project.Photos,
                Journal = project.Journal,
                Target = RenderTarget.Screen,
                ShowEmptySlotFlags = true,
            },
            PageWidthPx,
            PageHeightPx,
            SKColors.Black);

    private static string? OutputFolder(string leaf)
    {
        var root = Environment.GetEnvironmentVariable("PHOTOBOOK_VISUAL_DUMP");
        if (string.IsNullOrWhiteSpace(root)) return null;
        var folder = Path.Combine(root, leaf);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Describe(IReadOnlyList<RenderDiagnostic> diagnostics)
    {
        var interesting = diagnostics
            .Where(d => d.Severity != RenderSeverity.Info && d.Kind != RenderDiagnosticKind.LowEffectiveDpi)
            .Select(d => $"{d.Kind}{(d.SlotId is null ? "" : $"[{d.SlotId}]")}")
            .ToList();
        return interesting.Count == 0 ? "clean" : string.Join(" ", interesting);
    }

    private static void Save(DecodedImage pixels, string path)
    {
        using var bitmap = new SKBitmap(
            new SKImageInfo(pixels.Width, pixels.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        System.Runtime.InteropServices.Marshal.Copy(pixels.Pixels, 0, bitmap.GetPixels(), pixels.Pixels.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 92);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    /// <summary>
    /// Photo-like pixels for a photo id, synthesized deterministically from that id. Not a photograph,
    /// but it has the properties a page design has to survive: a bright sky and a dark foreground in the
    /// same frame, high-frequency texture, a warm off-center subject, and a hue that changes from frame
    /// to frame so a grid of them reads as a grid of different pictures. The reported source dimensions
    /// are the catalog's, so crop math and effective-DPI reporting behave exactly as they do in the app.
    /// </summary>
    private sealed class PhotographicImageSource : IRenderImageSource, IDisposable
    {
        private const int LongEdgePx = 1024;

        private readonly PhotoCatalog _catalog;
        private readonly Dictionary<string, RenderImage> _cache = new(StringComparer.Ordinal);

        public PhotographicImageSource(PhotoCatalog catalog) => _catalog = catalog;

        public RenderImage? GetImage(RenderImageRequest request)
        {
            if (_cache.TryGetValue(request.PhotoId, out var cached)) return cached;

            var photo = _catalog.Find(request.PhotoId);
            if (photo is null || photo.Width <= 0 || photo.Height <= 0) return null;

            var scale = (double)LongEdgePx / Math.Max(photo.Width, photo.Height);
            var w = Math.Max(8, (int)Math.Round(photo.Width * scale));
            var h = Math.Max(8, (int)Math.Round(photo.Height * scale));

            var image = Synthesize(w, h, StableSeed(request.PhotoId));
            var render = new RenderImage(image, photo.Width, photo.Height);
            _cache[request.PhotoId] = render;
            return render;
        }

        public void Dispose()
        {
            foreach (var entry in _cache.Values) entry.Image.Dispose();
            _cache.Clear();
        }

        private static int StableSeed(string photoId)
        {
            var hash = 17;
            foreach (var c in photoId) hash = (hash * 31) + c;
            return hash & 0x7FFFFFFF;
        }

        private static SKImage Synthesize(int width, int height, int seed)
        {
            var random = new Random(seed);
            var hue = (float)random.NextDouble() * 360f;
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;

            var horizon = height * (0.42f + (float)random.NextDouble() * 0.24f);

            // Sky: a bright, low-contrast wash — the hard case for white text.
            using (var sky = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, 0), new SKPoint(0, horizon),
                    [SKColor.FromHsl(hue, 42, 74), SKColor.FromHsl((hue + 18) % 360, 38, 90)],
                    [0f, 1f], SKShaderTileMode.Clamp),
            })
            {
                canvas.DrawRect(new SKRect(0, 0, width, horizon), sky);
            }

            // A blown highlight, so at least one frame per page has something the scrim must beat.
            using (var sun = new SKPaint
            {
                Shader = SKShader.CreateRadialGradient(
                    new SKPoint(width * (0.2f + (float)random.NextDouble() * 0.6f), horizon * 0.35f),
                    Math.Min(width, height) * 0.30f,
                    [SKColors.White, SKColors.White.WithAlpha(0)], [0f, 1f], SKShaderTileMode.Clamp),
            })
            {
                canvas.DrawRect(new SKRect(0, 0, width, horizon), sun);
            }

            // Ground: darker, and the other half of the contrast range.
            using (var ground = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, horizon), new SKPoint(0, height),
                    [SKColor.FromHsl((hue + 150) % 360, 34, 34), SKColor.FromHsl((hue + 140) % 360, 30, 16)],
                    [0f, 1f], SKShaderTileMode.Clamp),
            })
            {
                canvas.DrawRect(new SKRect(0, horizon, width, height), ground);
            }

            // Foliage and a treeline: high-frequency edges across the whole frame.
            using (var leaf = new SKPaint { IsAntialias = true })
            {
                for (var i = 0; i < 260; i++)
                {
                    var x = (float)random.NextDouble() * width;
                    var y = horizon + (float)Math.Pow(random.NextDouble(), 0.6) * (height - horizon);
                    var r = (float)(2 + random.NextDouble() * Math.Min(width, height) * 0.03);
                    leaf.Color = SKColor.FromHsl(
                        (hue + 130 + (float)random.NextDouble() * 60) % 360,
                        30 + (float)random.NextDouble() * 40,
                        12 + (float)random.NextDouble() * 46).WithAlpha(210);
                    canvas.DrawCircle(x, y, r, leaf);
                }

                for (var i = 0; i < 7; i++)
                {
                    var x = (float)random.NextDouble() * width;
                    var top = horizon - (float)(random.NextDouble() * height * 0.3);
                    leaf.Color = SKColor.FromHsl((hue + 145) % 360, 26, 18).WithAlpha(235);
                    canvas.DrawOval(new SKRect(x - width * 0.06f, top, x + width * 0.06f, horizon + height * 0.06f), leaf);
                }
            }

            // The subject: a warm figure, off-centre, roughly where a face would be.
            var cx = width * (0.28f + (float)random.NextDouble() * 0.44f);
            var cy = horizon + (height - horizon) * 0.18f;
            var headR = Math.Min(width, height) * 0.075f;
            using (var skin = new SKPaint { IsAntialias = true, Color = SKColor.FromHsl(28, 46, 68) })
            {
                canvas.DrawCircle(cx, cy, headR, skin);
                skin.Color = SKColor.FromHsl((hue + 200) % 360, 58, 52);
                canvas.DrawOval(new SKRect(cx - headR * 1.5f, cy + headR * 0.7f, cx + headR * 1.5f, cy + headR * 4.2f), skin);
            }

            // Grain, so the frame is not resolvable into flat regions at any zoom.
            using (var grain = new SKPaint { IsAntialias = false })
            {
                for (var i = 0; i < width * height / 90; i++)
                {
                    var x = (float)random.NextDouble() * width;
                    var y = (float)random.NextDouble() * height;
                    var v = (byte)random.Next(256);
                    grain.Color = new SKColor(v, v, v, 26);
                    canvas.DrawRect(x, y, 2, 2, grain);
                }
            }

            return surface.Snapshot();
        }
    }
}
