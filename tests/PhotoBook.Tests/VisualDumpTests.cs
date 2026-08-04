using PhotoBook.Core.Model;
using PhotoBook.Core.Templates;
using PhotoBook.Engine;
using PhotoBook.Rendering;
using PhotoBook.Tests.Fixtures;
using SkiaSharp;

namespace PhotoBook.Tests;

/// <summary>
/// Writes real rendered pages to PHOTOBOOK_VISUAL_DUMP so a human (or the agent building this) can
/// look at what the engine actually produces. Skipped unless that variable is set, so it never runs
/// in a normal test pass.
/// </summary>
[Collection(SyntheticMonthCollection.Name)]
public sealed class VisualDumpTests
{
    private readonly SyntheticMonth _month;

    public VisualDumpTests(SyntheticMonth month) => _month = month;

    [Fact]
    public void DumpChapterPages()
    {
        var outDir = Environment.GetEnvironmentVariable("PHOTOBOOK_VISUAL_DUMP");
        if (string.IsNullOrWhiteSpace(outDir))
        {
            return;
        }

        Directory.CreateDirectory(outDir);

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
        using var images = new ThumbnailRenderImageSource(_month.Paths, _month.Catalog, _month.Thumbnails);

        var summary = new List<string>
        {
            $"pages={book.Pages.Count} photos={_month.Catalog.Photos.Count} unplaced={book.UnplacedPhotoIds.Count}",
        };

        for (var i = 0; i < book.Pages.Count; i++)
        {
            var page = book.Pages[i];
            var template = page.ResolveTemplate(id => TemplateLibrary.Default.Find(id))
                           ?? throw new InvalidOperationException($"unresolved template '{page.TemplateRef}'");

            var preview = PagePreviewRenderer.Render(
                new PageRenderRequest
                {
                    Book = project.Book,
                    Chapter = project.Chapters[0],
                    Page = page,
                    Template = template,
                    Geometry = PageGeometryMapper.Create(
                        BuiltInPrintProfiles.Generic, project.Book.PageSize, PageGeometry.PointsPerInch),
                    Images = images,
                    Photos = project.Photos,
                    Journal = project.Journal,
                    Target = RenderTarget.Screen,
                    ShowEmptySlotFlags = true,
                },
                pixelWidth: 1100,
                pixelHeight: 850,
                SKColors.Black);

            var pixels = preview.Image;
            using var bitmap = new SKBitmap(new SKImageInfo(pixels.Width, pixels.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            System.Runtime.InteropServices.Marshal.Copy(pixels.Pixels, 0, bitmap.GetPixels(), pixels.Pixels.Length);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 92);
            using var fs = File.Create(Path.Combine(outDir, $"page-{i + 1:00}.png"));
            data.SaveTo(fs);

            summary.Add(
                $"page {i + 1,2}: template={page.TemplateRef,-16} photos={page.Placements.Count} " +
                $"empty={preview.Result.EmptySlotCount} text={(template.TextSlots.Count > 0 ? "y" : "n")}");
        }

        File.WriteAllLines(Path.Combine(outDir, "summary.txt"), summary);
    }
}
