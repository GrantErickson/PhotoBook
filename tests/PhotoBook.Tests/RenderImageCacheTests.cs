using PhotoBook.Core.Model;
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
            canvas.DrawImage(image.Image, 0, 0);
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
}
