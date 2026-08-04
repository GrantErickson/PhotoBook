using ImageMagick;
using PhotoBook.Imaging;
using PhotoBook.Tests.Fixtures;

namespace PhotoBook.Tests;

/// <summary>
/// Decode-side integration tests (doc 05 step 2, ADR-0004): every accepted format round-trips to the
/// dimensions it was written at, EXIF orientation is applied exactly once, and the HEIC family is
/// genuinely wired up rather than merely listed.
/// </summary>
public sealed class ImageDecodeTests
{
    private static readonly ImageDecoder Decoder = new();

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".png")]
    [InlineData(".webp")]
    [InlineData(".tif")]
    [InlineData(".tiff")]
    [InlineData(".bmp")]
    [InlineData(".gif")]
    public async Task EverySynthesizableFormatRoundTripsToItsPixelDimensions(string extension)
    {
        using var workspace = new TempWorkspace("decode");
        var path = SyntheticImages.Write(
            workspace.At("fixtures", "scene" + extension),
            new SyntheticImages.SceneSpec { Width = 640, Height = 480 });

        var metadata = await Decoder.ProbeAsync(path);
        Assert.Equal(640, metadata.Width);
        Assert.Equal(480, metadata.Height);

        var decoded = await Decoder.DecodeAsync(path);
        Assert.Equal(640, decoded.Width);
        Assert.Equal(480, decoded.Height);
        Assert.Equal(640 * 4, decoded.Stride);
        Assert.Equal(640L * 480 * 4, decoded.Pixels.LongLength);
    }

    [Theory]
    [InlineData(1600, 1200)]   // 4:3 landscape
    [InlineData(1200, 1600)]   // 3:4 portrait
    [InlineData(1920, 1080)]   // 16:9
    [InlineData(800, 800)]     // square
    [InlineData(1500, 500)]    // panorama
    public async Task DifferingAspectRatiosSurviveProbeAndDecode(int width, int height)
    {
        using var workspace = new TempWorkspace("aspect");
        var path = SyntheticImages.Write(
            workspace.At("fixtures", $"{width}x{height}.jpg"),
            new SyntheticImages.SceneSpec { Width = width, Height = height });

        var metadata = await Decoder.ProbeAsync(path);
        Assert.Equal(width, metadata.Width);
        Assert.Equal(height, metadata.Height);
        Assert.Equal((double)width / height, metadata.Aspect, 6);

        var decoded = await Decoder.DecodeAsync(path);
        Assert.Equal(width, decoded.Width);
        Assert.Equal(height, decoded.Height);
    }

    [Fact]
    public async Task MaxLongEdgeDownsamplesAndPreservesAspect()
    {
        using var workspace = new TempWorkspace("downsample");
        var path = SyntheticImages.Write(
            workspace.At("fixtures", "big.jpg"),
            new SyntheticImages.SceneSpec { Width = 1600, Height = 1200 });

        var decoded = await Decoder.DecodeAsync(path, maxLongEdge: 256);

        Assert.True(Math.Max(decoded.Width, decoded.Height) <= 256);
        Assert.Equal(4.0 / 3.0, decoded.Aspect, 1);
    }

    [Fact]
    public async Task ExifOrientationIsAppliedExactlyOnce()
    {
        using var workspace = new TempWorkspace("orientation");

        // Orientation 6 = "rotate 90° clockwise on display": stored 800×1200 must read as 1200×800.
        var path = SyntheticImages.Write(
            workspace.At("fixtures", "rotated.jpg"),
            new SyntheticImages.SceneSpec { Width = 800, Height = 1200, Orientation = 6 });

        var metadata = await Decoder.ProbeAsync(path);
        Assert.Equal(6, metadata.Orientation);
        Assert.True(metadata.OrientationSwapsAxes);
        Assert.Equal(800, metadata.StoredWidth);
        Assert.Equal(1200, metadata.StoredHeight);
        Assert.Equal(1200, metadata.Width);
        Assert.Equal(800, metadata.Height);

        // And the pixels agree with the metadata: the decoder orients, nothing downstream rotates again.
        var decoded = await Decoder.DecodeAsync(path);
        Assert.Equal(1200, decoded.Width);
        Assert.Equal(800, decoded.Height);
    }

    [Fact]
    public async Task AnUnorientedFileIsNotRotated()
    {
        using var workspace = new TempWorkspace("orientation-none");
        var path = SyntheticImages.Write(
            workspace.At("fixtures", "upright.jpg"),
            new SyntheticImages.SceneSpec { Width = 800, Height = 1200, Orientation = 1 });

        var metadata = await Decoder.ProbeAsync(path);
        Assert.False(metadata.OrientationSwapsAxes);
        Assert.Equal(800, metadata.Width);
        Assert.Equal(1200, metadata.Height);
    }

    [Fact]
    public async Task ExifCaptureMetadataIsReadBackVerbatim()
    {
        using var workspace = new TempWorkspace("exif");
        var taken = new DateTime(2024, 3, 15, 10, 30, 45, DateTimeKind.Unspecified);
        var path = SyntheticImages.Write(
            workspace.At("fixtures", "camera.jpg"),
            new SyntheticImages.SceneSpec
            {
                Width = 900,
                Height = 600,
                ExifTaken = taken,
                CameraMake = "PhotoBook",
                CameraModel = "Fixture One",
            });

        var metadata = await Decoder.ProbeAsync(path);

        Assert.Equal(taken, metadata.DateTimeOriginal);
        Assert.Equal(DateTimeKind.Unspecified, metadata.DateTimeOriginal!.Value.Kind);
        Assert.Equal("PhotoBook", metadata.CameraMake);
        Assert.Equal("Fixture One", metadata.CameraModel);
    }

    [Fact]
    public void TheHeicFamilyIsAcceptedAndAReadCoderIsActuallyPresent()
    {
        // Magick.NET cannot *write* HEIC, so the fixture route used by every other format is closed.
        // The two things that can be proven are proven: the decoder accepts the extensions, and the
        // bundled libheif read coder that ADR-0004 depends on is genuinely registered in this process.
        foreach (var extension in new[] { ".heic", ".heif", ".avif" })
        {
            Assert.Contains(extension, Decoder.SupportedExtensions);
            Assert.True(Decoder.CanDecode("C:\\photos\\IMG_0001" + extension));
        }

        MagickRuntime.Ensure();
        var formats = MagickNET.SupportedFormats.ToList();
        Assert.Contains(formats, f => f.Format == MagickFormat.Heic && f.SupportsReading);
        Assert.Contains(formats, f => f.Format == MagickFormat.Heif && f.SupportsReading);
    }

    [Fact]
    public void TheSupportedExtensionListIsTheDocumentedOne()
    {
        Assert.Equal(
            [".avif", ".bmp", ".gif", ".heic", ".heif", ".jpe", ".jpeg", ".jpg", ".png", ".tif", ".tiff", ".webp"],
            Decoder.SupportedExtensions.OrderBy(e => e, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ADecodeFailureIsReportedAsAnImageDecodeExceptionCarryingThePath()
    {
        using var workspace = new TempWorkspace("broken");
        var path = workspace.At("fixtures", "truncated.jpg");
        await File.WriteAllBytesAsync(path, [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A]);

        var error = await Assert.ThrowsAsync<ImageDecodeException>(() => Decoder.DecodeAsync(path));
        Assert.Equal(path, error.Path);
    }
}
