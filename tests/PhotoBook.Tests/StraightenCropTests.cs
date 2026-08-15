using ImageMagick;
using PhotoBook.Imaging;
using PhotoBook.Tests.Fixtures;

namespace PhotoBook.Tests;

/// <summary>
/// Straightening rotates the frame, which leaves triangular wedges at the corners. Those wedges must
/// be cropped away, not filled: a photo with angled black corners on a black page reads as a
/// rendering fault, and on a page with an image background it would read as damage.
/// </summary>
public sealed class StraightenCropTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    /// <summary>A uniformly coloured photo, so any pixel that is not that colour came from a wedge.</summary>
    private string WriteFlat(string name, MagickColor colour, uint width = 1200, uint height = 800)
    {
        var path = _workspace.At("images", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new MagickImage(colour, width, height);
        image.Write(path);
        return path;
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(-5.0)]
    [InlineData(9.5)]
    [InlineData(-12.0)]
    public async Task AStraightenedPhotoHasNoWedgeLeftInAnyCorner(double degrees)
    {
        var path = WriteFlat($"flat{degrees}.png", MagickColors.White);
        var decoder = new ImageDecoder();

        var adjusted = await decoder.DecodeAdjustedAsync(
            path, new ImageAdjustments { Straighten = degrees }, maxLongEdge: 0);

        // Sample well inside each corner: anti-aliasing along the crop edge is expected, a wedge is not.
        var inset = 4;
        foreach (var (x, y, corner) in new[]
        {
            (inset, inset, "top-left"),
            (adjusted.Width - 1 - inset, inset, "top-right"),
            (inset, adjusted.Height - 1 - inset, "bottom-left"),
            (adjusted.Width - 1 - inset, adjusted.Height - 1 - inset, "bottom-right"),
        })
        {
            var offset = (y * adjusted.Stride) + (x * DecodedImage.BytesPerPixel);
            var b = adjusted.Pixels[offset];
            var g = adjusted.Pixels[offset + 1];
            var r = adjusted.Pixels[offset + 2];

            Assert.True(
                r > 200 && g > 200 && b > 200,
                $"{corner} corner of a {degrees}° straighten is #{r:X2}{g:X2}{b:X2}, not the photo — the " +
                "rotation wedge was left in the frame instead of being cropped away.");
        }
    }

    [Fact]
    public async Task StraighteningKeepsTheFrameRectangularAndTheAspectItStartedWith()
    {
        var path = WriteFlat("aspect.png", MagickColors.White, 1600, 900);
        var decoder = new ImageDecoder();

        var straight = await decoder.DecodeAsync(path);
        var tilted = await decoder.DecodeAdjustedAsync(
            path, new ImageAdjustments { Straighten = 6.5 }, maxLongEdge: 0);

        Assert.True(tilted.Width > 0 && tilted.Height > 0);

        // The crop shrinks the frame, and keeps its shape: a straighten must not turn a landscape
        // photo into a subtly different aspect, or every slot's crop would shift under it.
        Assert.True(tilted.Width <= straight.Width && tilted.Height <= straight.Height);
        Assert.Equal(straight.Aspect, tilted.Aspect, 2);
    }
}
