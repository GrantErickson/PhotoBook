using PhotoBook.Core.Model;
using PhotoBook.Core.Templates;
using PhotoBook.Engine;
using PhotoBook.Imaging;
using PhotoBook.Rendering;
using PhotoBook.Tests.Fixtures;
using SkiaSharp;

namespace PhotoBook.Tests;

/// <summary>
/// The page canvas draws through <see cref="ThumbnailRenderImageSource"/>. Its in-memory cache was
/// keyed by photo and tier alone, so once a photo had been drawn, editing it returned the pixels it
/// had before the edit — the correction appeared in the inspector's own preview and never on the
/// page. The edit hash belongs in the key.
/// </summary>
[Collection(SyntheticMonthCollection.Name)]
public sealed class RenderImageCacheTests
{
    private readonly SyntheticMonth _month;

    public RenderImageCacheTests(SyntheticMonth month) => _month = month;

    /// <summary>
    /// Average brightness of what the renderer would draw. Goes through an <see cref="SKBitmap"/>
    /// rather than <c>PeekPixels</c> because an image decoded from an encoded file is not obliged to
    /// expose its pixels directly; drawing it once always is.
    /// </summary>
    private static double MeanLuma(RenderImage image)
    {
        var info = new SKImageInfo(image.PixelWidth, image.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Black);
            canvas.DrawImage(image.Image, 0, 0, SKSamplingOptions.Default, null);
        }

        var span = bitmap.GetPixelSpan();

        double total = 0;
        var samples = 0;
        for (var i = 0; i + 3 < span.Length; i += 64 * 4)
        {
            total += (0.114 * span[i]) + (0.587 * span[i + 1]) + (0.299 * span[i + 2]);
            samples++;
        }

        return samples == 0 ? 0 : total / samples;
    }

    [Fact]
    public void EditingAPhotoChangesWhatThePageDrawsForIt()
    {
        var photo = _month.MonthPhotos[0];

        using var images = new ThumbnailRenderImageSource(_month.Paths, _month.Catalog, _month.Thumbnails);
        var request = new RenderImageRequest(photo.Id, MinLongEdgePx: 512, RenderTarget.Screen);

        var before = images.GetImage(request);
        Assert.NotNull(before);
        var lumaBefore = MeanLuma(before!);

        // Exactly what the in-place panel does: write a parameter onto the catalog entry.
        var original = photo.Adjustments.ExposureEv;
        try
        {
            photo.Adjustments.ExposureEv = -1.5;

            var after = images.GetImage(request);
            Assert.NotNull(after);

            Assert.True(
                MeanLuma(after!) < lumaBefore - 2.0,
                $"the page still draws the pre-edit pixels: luma {MeanLuma(after!):F1} vs {lumaBefore:F1} " +
                "before a −1.5 EV exposure edit — the render cache ignored the adjustment.");
        }
        finally
        {
            photo.Adjustments.ExposureEv = original;
        }
    }

    [Fact]
    public void ReturningToAPreviousEditStateIsStillCorrect()
    {
        var photo = _month.MonthPhotos[1];

        using var images = new ThumbnailRenderImageSource(_month.Paths, _month.Catalog, _month.Thumbnails);
        var request = new RenderImageRequest(photo.Id, MinLongEdgePx: 512, RenderTarget.Screen);

        var original = photo.Adjustments.ExposureEv;
        try
        {
            var neutral = MeanLuma(images.GetImage(request)!);

            photo.Adjustments.ExposureEv = 1.2;
            var brightened = MeanLuma(images.GetImage(request)!);

            // Undo: the same parameters must give the same pixels, whether or not the old entry was
            // dropped to keep the cache from thrashing during a drag.
            photo.Adjustments.ExposureEv = original;
            var restored = MeanLuma(images.GetImage(request)!);

            Assert.True(brightened > neutral + 2.0, "a +1.2 EV edit did not brighten the drawn image");
            Assert.Equal(neutral, restored, 1);
        }
        finally
        {
            photo.Adjustments.ExposureEv = original;
        }
    }

    /// <summary>
    /// The claim the two tests above only support indirectly, made end to end: lay a chapter out, draw
    /// a page through the same <see cref="PagePreviewRenderer"/> the editor's canvas uses, move one
    /// photo's exposure, draw again — and the pixels inside that photo's slot, and only that slot, must
    /// have moved with it.
    /// </summary>
    [Fact]
    public void EditingOnePhotoRedrawsThatPhotosSlotOnTheRenderedPage()
    {
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
        var page = book.Pages.First(p => p.Placements.Count >= 2);
        var edited = page.Placements[0];
        var untouched = page.Placements[1];
        var photo = _month.Catalog.Find(edited.PhotoId)!;

        using var images = new ThumbnailRenderImageSource(_month.Paths, _month.Catalog, _month.Thumbnails);
        var request = new PageRenderRequest
        {
            Book = project.Book,
            Chapter = project.Chapters[0],
            Page = page,
            Template = page.ResolveTemplate(id => TemplateLibrary.Default.Find(id))!,
            Geometry = PageGeometryMapper.Create(
                BuiltInPrintProfiles.Generic, project.Book.PageSize, PageGeometry.PointsPerInch),
            Images = images,
            Photos = project.Photos,
            Journal = project.Journal,
            Target = RenderTarget.Screen,
            ShowEmptySlotFlags = false,
        };

        var before = PagePreviewRenderer.Render(request, 1100, 850, SKColors.Black);
        var editedRect = before.Result.VisibleImageRects[edited.SlotId];
        var untouchedRect = before.Result.VisibleImageRects[untouched.SlotId];

        var original = photo.Adjustments.ExposureEv;
        try
        {
            photo.Adjustments.ExposureEv = -2.0;
            var after = PagePreviewRenderer.Render(request, 1100, 850, SKColors.Black);

            var editedBefore = MeanLuma(before.Image, editedRect);
            var editedAfter = MeanLuma(after.Image, editedRect);
            Assert.True(
                editedAfter < editedBefore - 5.0,
                $"the page redrew at luma {editedAfter:F1} against {editedBefore:F1} before a −2 EV edit — " +
                "the correction never reached the page.");

            // The neighbouring photo is the control: whatever changed, it was this photo and not the
            // whole page being re-decoded differently.
            Assert.Equal(MeanLuma(before.Image, untouchedRect), MeanLuma(after.Image, untouchedRect), 1);
        }
        finally
        {
            photo.Adjustments.ExposureEv = original;
        }
    }

    /// <summary>Average brightness inside one slot of a rendered page.</summary>
    private static double MeanLuma(DecodedImage page, SKRect rect)
    {
        var span = page.AsSpan();
        var left = Math.Max(0, (int)rect.Left);
        var top = Math.Max(0, (int)rect.Top);
        var right = Math.Min(page.Width, (int)rect.Right);
        var bottom = Math.Min(page.Height, (int)rect.Bottom);

        double total = 0;
        var samples = 0;
        for (var y = top; y < bottom; y += 3)
        {
            for (var x = left; x < right; x += 3)
            {
                var i = (y * page.Stride) + (x * 4);
                if (i + 3 >= span.Length) continue;
                total += (0.114 * span[i]) + (0.587 * span[i + 1]) + (0.299 * span[i + 2]);
                samples++;
            }
        }

        return samples == 0 ? 0 : total / samples;
    }
}
