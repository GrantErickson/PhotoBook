using System.Security.Cryptography;
using System.Text;
using PhotoBook.Core.Model;
using PhotoBook.Core.Persistence;
using PhotoBook.Core.Templates;
using PhotoBook.Engine;
using PhotoBook.Rendering;
using PhotoBook.Tests.Fixtures;
using SkiaSharp;

namespace PhotoBook.Tests;

/// <summary>
/// The Phase 3 seam test for rendering and export: the book the engine just laid out is rasterized
/// through the one <see cref="PageRenderer"/> draw path, written to a real PDF on disk, and run
/// through the kernel §11 preflight gate.
/// <para>
/// This is the test that would break if the engine and the renderer had been written against each
/// other's documentation rather than against each other: it decodes the actual archived originals,
/// resolves the actual templates, and applies the actual <see cref="CropState"/> values phase 6
/// emitted.
/// </para>
/// </summary>
[Collection(SyntheticMonthCollection.Name)]
public sealed class Phase3ExportTests
{
    private readonly SyntheticMonth _month;

    /// <summary>Receives the shared month.</summary>
    public Phase3ExportTests(SyntheticMonth month) => _month = month;

    // ---- rendering ----------------------------------------------------------------------------------

    [Fact]
    public void ALaidOutPageRastersToRealPixelsOnARealBlackPage()
    {
        var book = Layout();
        var project = _month.SnapshotWith(book.Pages);
        var page = book.Pages.First(p => p.Placements.Count >= 3);

        using var images = ImageSource();

        // The buffer is cleared to magenta, so "the page is black" cannot be an artifact of the
        // canvas: every black pixel below was painted by the renderer's R21 background pass.
        var preview = PagePreviewRenderer.Render(
            Request(project, page, images), pixelWidth: 1100, pixelHeight: 850, SKColors.Magenta);

        var pixels = preview.Image;
        Assert.Equal(1100, pixels.Width);
        Assert.Equal(850, pixels.Height);

        // 1. The page is not blank: photographs actually decoded and drew.
        var contrast = SyntheticImages.LumaStdDev(pixels.AsSpan());
        Assert.True(contrast > 0.05, $"the rendered page has almost no variation (luma σ = {contrast:F4}) — nothing drew");

        var lit = CountPixels(pixels, (r, g, b) => r + g + b > 90);
        Assert.True(
            lit > pixels.Width * pixels.Height / 5,
            $"only {lit} of {pixels.Width * pixels.Height} pixels carry image data");

        // 2. The page background is genuinely black (R21), everywhere no slot covers it.
        var samples = BackgroundSamples(preview);
        Assert.True(samples.Count >= 50, $"only {samples.Count} sample points fell outside every slot");
        foreach (var (x, y) in samples)
        {
            var (r, g, b) = PixelAt(pixels, x, y);
            Assert.True(r == 0 && g == 0 && b == 0, $"page background at ({x}, {y}) is #{r:X2}{g:X2}{b:X2}, not black");
        }

        // 3. The renderer agrees with the engine about what is on the page.
        Assert.Equal(page.Placements.Count, preview.Result.VisibleImageRects.Count);
        Assert.Equal(0, preview.Result.EmptySlotCount);
        Assert.DoesNotContain(preview.Result.Diagnostics, d => d.Severity == RenderSeverity.Error);
    }

    [Fact]
    public void EveryPageOfTheChapterRendersWithoutAnError()
    {
        var book = Layout();
        var project = _month.SnapshotWith(book.Pages);
        using var images = ImageSource();

        var number = 0;
        foreach (var page in book.Pages)
        {
            number++;
            var preview = PagePreviewRenderer.Render(
                Request(project, page, images), 560, 440, SKColors.Black);

            Assert.DoesNotContain(
                preview.Result.Diagnostics,
                d => d.Severity == RenderSeverity.Error);
            Assert.Equal(0, preview.Result.EmptySlotCount);
            Assert.True(
                SyntheticImages.LumaStdDev(preview.Image.AsSpan()) > 0.02,
                $"page {number} ({page.TemplateRef}) rendered essentially blank");
        }
    }

    // ---- PDF export ---------------------------------------------------------------------------------

    [Fact]
    public void TheChapterExportsToARealMultiPagePdf()
    {
        var book = Layout();
        var project = _month.SnapshotWith(book.Pages);
        using var images = ImageSource();

        var path = _month.Workspace.At("export", "book.pdf");
        var result = new PdfExporter().Export(new PdfExportRequest
        {
            Project = project,
            Profile = BuiltInPrintProfiles.Generic,
            Templates = id => TemplateLibrary.Default.Find(id),
            Images = images,
            OutputPath = path,
            Scope = ExportScope.WholeBook,
        });

        Assert.Equal(book.Pages.Count, result.PageCount);
        Assert.Equal(book.Pages.Count, result.SheetCount);
        Assert.True(File.Exists(path));

        var bytes = File.ReadAllBytes(path);

        // A real PDF, not a plausible-looking stub.
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes, 0, 8), StringComparison.Ordinal);
        Assert.Contains("%%EOF", Encoding.Latin1.GetString(bytes[^64..]), StringComparison.Ordinal);
        Assert.Equal(book.Pages.Count, CountPdfPages(bytes));

        // Eleven pages of 300 DPI photographs is megabytes, not kilobytes: a PDF that lost its images
        // would still parse.
        Assert.True(bytes.Length > 400_000, $"the exported PDF is only {bytes.Length} bytes — the images did not embed");

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RenderSeverity.Error);
    }

    [Fact]
    public void ExportingTheSameBookTwiceProducesByteIdenticalFiles()
    {
        var book = Layout();
        var project = _month.SnapshotWith(book.Pages);

        var first = ExportOnce(project, "stable-1.pdf");
        var second = ExportOnce(project, "stable-2.pdf");

        Assert.Equal(Convert.ToHexString(SHA256.HashData(first)), Convert.ToHexString(SHA256.HashData(second)));
    }

    [Fact]
    public void AChapterProofCarriesTheBookWidePageNumbers()
    {
        var book = Layout();
        var project = _month.SnapshotWith(book.Pages);
        using var images = ImageSource();

        var path = _month.Workspace.At("export", "chapter.pdf");
        var result = new PdfExporter().Export(new PdfExportRequest
        {
            Project = project,
            Profile = BuiltInPrintProfiles.Generic,
            Templates = id => TemplateLibrary.Default.Find(id),
            Images = images,
            OutputPath = path,
            Scope = ExportScope.Chapter(SyntheticMonth.Month),
        });

        Assert.Equal(book.Pages.Count, result.PageCount);
        Assert.Equal(book.Pages.Count, CountPdfPages(File.ReadAllBytes(path)));
    }

    // ---- preflight (kernel §11) ---------------------------------------------------------------------

    [Fact]
    public void AFullyPopulatedPagePreflightsWithNothingBlocking()
    {
        var book = Layout();
        var project = _month.SnapshotWith(book.Pages);
        var number = PageNumberOfFirstBusyPage(book.Pages);

        var report = Preflight(project, ExportScope.PageRange(number, number));

        Assert.DoesNotContain(report.Findings, f => f.Check == PreflightCheck.EmptySlot);
        Assert.DoesNotContain(report.Findings, f => f.Check == PreflightCheck.TextOverflow);
        Assert.True(report.CanExport, "a fully populated page should not block export: " + report.Summarize());
    }

    [Fact]
    public void APageWithAnEmptySlotBlocksExport()
    {
        var book = Layout();
        var pages = book.Pages.Select(Clone).ToList();
        var number = PageNumberOfFirstBusyPage(pages);

        // Exactly what R14 describes: the user swapped in a layout with more slots than photos.
        var wounded = pages[number - 1];
        var orphaned = wounded.Placements[1];
        wounded.Placements.RemoveAt(1);

        var report = Preflight(_month.SnapshotWith(pages), ExportScope.PageRange(number, number));

        var finding = Assert.Single(report.Findings, f => f.Check == PreflightCheck.EmptySlot);
        Assert.Equal(PreflightSeverity.Error, finding.Severity);
        Assert.Equal(number, finding.BookPageNumber);
        Assert.Equal(orphaned.SlotId, finding.SlotId);
        Assert.False(report.CanExport);
        Assert.NotEmpty(report.Errors);
    }

    [Fact]
    public void AnElevenPageBookFailsThePrintProfilesPageCountRule()
    {
        // The generic profile wants at least 20 pages in multiples of 2 (doc 12). A whole-book export
        // is the only scope where that is enforced — a chapter proof is not an upload.
        var book = Layout();
        var project = _month.SnapshotWith(book.Pages);

        var whole = Preflight(project, ExportScope.WholeBook);
        var proof = Preflight(project, ExportScope.Chapter(SyntheticMonth.Month));

        Assert.Contains(whole.Errors, f => f.Check == PreflightCheck.ProfileConstraint);
        Assert.False(whole.CanExport);
        Assert.DoesNotContain(proof.Errors, f => f.Check == PreflightCheck.ProfileConstraint);
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private LayoutResult Layout() => LayoutEngine.LayoutChapter(new LayoutRequest
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

    private ThumbnailRenderImageSource ImageSource() =>
        new(_month.Paths, _month.Catalog, _month.Thumbnails);

    private PageRenderRequest Request(ProjectSnapshot project, Page page, IRenderImageSource images) => new()
    {
        Book = project.Book,
        Chapter = project.Chapters[0],
        Page = page,
        Template = page.ResolveTemplate(id => TemplateLibrary.Default.Find(id))
                   ?? throw new InvalidOperationException($"unresolved template '{page.TemplateRef}'"),
        Geometry = PageGeometryMapper.Create(
            BuiltInPrintProfiles.Generic, project.Book.PageSize, PageGeometry.PointsPerInch),
        Images = images,
        Photos = project.Photos,
        Journal = project.Journal,
        Target = RenderTarget.Screen,
        ShowEmptySlotFlags = false,
    };

    private byte[] ExportOnce(ProjectSnapshot project, string fileName)
    {
        using var images = ImageSource();
        var path = _month.Workspace.At("export", fileName);
        new PdfExporter().Export(new PdfExportRequest
        {
            Project = project,
            Profile = BuiltInPrintProfiles.Generic,
            Templates = id => TemplateLibrary.Default.Find(id),
            Images = images,
            OutputPath = path,
            Overwrite = true,
        });

        return File.ReadAllBytes(path);
    }

    private static PreflightReport Preflight(ProjectSnapshot project, ExportScope scope) =>
        new PreflightChecker().Check(new PreflightRequest
        {
            Project = project,
            Profile = BuiltInPrintProfiles.Generic,
            Templates = id => TemplateLibrary.Default.Find(id),
            Scope = scope,
        });

    private static int PageNumberOfFirstBusyPage(IReadOnlyList<Page> pages)
    {
        for (var i = 0; i < pages.Count; i++)
        {
            if (pages[i].Placements.Count >= 3) return i + 1;
        }

        throw new InvalidOperationException("the fixture chapter has no page with three photos.");
    }

    private static Page Clone(Page page) => page with
    {
        Placements = [.. page.Placements.Select(p => p with { })],
        JournalAssignments = [.. page.JournalAssignments.Select(a => a with { EntryIds = [.. a.EntryIds] })],
    };

    /// <summary>
    /// Device points inside the sheet that neither an image slot nor a text slot covers — the pixels
    /// where nothing but the R21 page background can be.
    /// </summary>
    private static List<(int X, int Y)> BackgroundSamples(PagePreview preview)
    {
        var media = preview.Geometry.MediaRect;
        var slots = preview.Result.SlotRects.Values
            .Concat(preview.Result.TextSlotRects.Values)
            .Select(r => SKRect.Inflate(r, 8, 8))
            .ToList();

        var samples = new List<(int, int)>();
        for (var y = media.Top + 4; y < media.Bottom - 4; y += 7)
        {
            for (var x = media.Left + 4; x < media.Right - 4; x += 7)
            {
                if (slots.Any(s => s.Contains(x, y))) continue;
                var px = (int)x;
                var py = (int)y;
                if (px < 0 || py < 0 || px >= preview.Image.Width || py >= preview.Image.Height) continue;
                samples.Add((px, py));
            }
        }

        return samples;
    }

    private static (byte R, byte G, byte B) PixelAt(PhotoBook.Imaging.DecodedImage image, int x, int y)
    {
        var offset = y * image.Stride + x * PhotoBook.Imaging.DecodedImage.BytesPerPixel;
        return (image.Pixels[offset + 2], image.Pixels[offset + 1], image.Pixels[offset]);
    }

    private static int CountPixels(PhotoBook.Imaging.DecodedImage image, Func<int, int, int, bool> predicate)
    {
        var count = 0;
        var pixels = image.Pixels;
        for (var i = 0; i < pixels.Length; i += PhotoBook.Imaging.DecodedImage.BytesPerPixel)
        {
            if (predicate(pixels[i + 2], pixels[i + 1], pixels[i])) count++;
        }

        return count;
    }

    /// <summary>
    /// Counts the page objects in a PDF by looking for <c>/Type /Page</c> that is not <c>/Type /Pages</c>.
    /// Skia writes an uncompressed object table, so this reads the file rather than trusting the exporter.
    /// </summary>
    private static int CountPdfPages(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var count = 0;
        var index = 0;
        while (true)
        {
            index = text.IndexOf("/Type /Page", index, StringComparison.Ordinal);
            if (index < 0) break;
            var after = index + "/Type /Page".Length;
            if (after >= text.Length || text[after] != 's') count++;
            index = after;
        }

        return count;
    }
}
