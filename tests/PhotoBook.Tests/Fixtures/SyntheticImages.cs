using System.Globalization;
using ImageMagick;
using PhotoBook.Core.Model;

namespace PhotoBook.Tests.Fixtures;

/// <summary>
/// Synthesizes the real image files the Phase 2 integration tests run against. Nothing here depends
/// on a photo existing on the machine: every fixture is generated deterministically from a seed,
/// written through Magick.NET, and thrown away with the <see cref="TempWorkspace"/>.
///
/// <para>The scene is deliberately built so the analysis assertions mean something:</para>
/// <list type="bullet">
/// <item><description>A smooth, low-frequency background — so global sharpness comes almost entirely
/// from the subject, and blurring the file craters it.</description></item>
/// <item><description>An optional high-contrast checkerboard <em>subject</em> at a caller-chosen,
/// deliberately off-center rect — the thing a saliency detector must find.</description></item>
/// <item><description>An exposure scale applied last, so "over-exposed" really does pile pixels above
/// the 247/255 clipping threshold doc 06 measures.</description></item>
/// </list>
/// </summary>
public static class SyntheticImages
{
    /// <summary>The default off-center subject: upper-left third, well away from the center prior.</summary>
    public static Rect OffCenterSubject { get; } = new(0.10, 0.14, 0.26, 0.30);

    /// <summary>How one synthesized image should look.</summary>
    public sealed record SceneSpec
    {
        /// <summary>Stored pixel width (before any EXIF orientation is declared).</summary>
        public int Width { get; init; } = 1200;

        /// <summary>Stored pixel height (before any EXIF orientation is declared).</summary>
        public int Height { get; init; } = 900;

        /// <summary>The high-contrast subject's normalized rect, or null for a subject-free frame.</summary>
        public Rect? Subject { get; init; } = OffCenterSubject;

        /// <summary>Gaussian blur sigma applied after rendering; 0 leaves the frame crisp.</summary>
        public double BlurSigma { get; init; }

        /// <summary>Linear multiplier applied to every channel last: &gt;1 blows highlights, &lt;1 crushes shadows.</summary>
        public double ExposureScale { get; init; } = 1.0;

        /// <summary>EXIF <c>DateTimeOriginal</c> to embed, or null for a file with no capture date.</summary>
        public DateTime? ExifTaken { get; init; }

        /// <summary>EXIF orientation tag to embed (1..8), or null to embed none.</summary>
        public ushort? Orientation { get; init; }

        /// <summary>Camera make/model to embed alongside the date.</summary>
        public string? CameraMake { get; init; }

        /// <summary>Camera model to embed alongside the date.</summary>
        public string? CameraModel { get; init; }

        /// <summary>Seed for the deterministic texture; two writes of the same seed are identical.</summary>
        public int Seed { get; init; } = 20240115;

        /// <summary>Hue rotation of the background palette, 0..1 — gives visibly different photos.</summary>
        public double Hue { get; init; } = 0.55;

        /// <summary>Encoder quality for lossy formats.</summary>
        public uint Quality { get; init; } = 92;
    }

    /// <summary>Renders a scene and writes it to <paramref name="path"/>; the extension picks the format.</summary>
    /// <param name="path">Absolute destination path.</param>
    /// <param name="spec">The scene to render; null uses the defaults.</param>
    /// <returns>The path written, for fluent use in tests.</returns>
    public static string Write(string path, SceneSpec? spec = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        spec ??= new SceneSpec();

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var image = Render(spec);
        image.Quality = spec.Quality;
        image.Write(path);
        return path;
    }

    /// <summary>A crisp, well-exposed frame with the off-center subject — the "good photo" fixture.</summary>
    public static string WriteSharp(string path, SceneSpec? spec = null) =>
        Write(path, (spec ?? new SceneSpec()) with { BlurSigma = 0 });

    /// <summary>The same scene, heavily blurred — the "missed focus" fixture.</summary>
    public static string WriteBlurred(string path, SceneSpec? spec = null) =>
        Write(path, (spec ?? new SceneSpec()) with { BlurSigma = 8.0 });

    /// <summary>The same scene pushed until the highlights blow out.</summary>
    public static string WriteOverexposed(string path, SceneSpec? spec = null) =>
        Write(path, (spec ?? new SceneSpec()) with { ExposureScale = 3.2 });

    /// <summary>The same scene crushed into the shadows.</summary>
    public static string WriteUnderexposed(string path, SceneSpec? spec = null) =>
        Write(path, (spec ?? new SceneSpec()) with { ExposureScale = 0.045 });

    /// <summary>Renders a scene into an in-memory <see cref="MagickImage"/>. The caller disposes it.</summary>
    public static MagickImage Render(SceneSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentOutOfRangeException.ThrowIfLessThan(spec.Width, 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(spec.Height, 8);

        var rgb = RenderPixels(spec);

        var image = new MagickImage();
        var settings = new PixelReadSettings((uint)spec.Width, (uint)spec.Height, StorageType.Char, PixelMapping.RGB);
        image.ReadPixels(rgb, settings);
        image.ColorSpace = ColorSpace.sRGB;

        if (spec.BlurSigma > 0) image.GaussianBlur(spec.BlurSigma, spec.BlurSigma);

        // The coder writes the image's own Orientation into the EXIF block on save, so setting only the
        // profile value would be silently overwritten by "undefined". Set both and they agree.
        if (spec.Orientation is { } orientation) image.Orientation = (OrientationType)orientation;

        var profile = BuildExifProfile(spec);
        if (profile is not null) image.SetProfile(profile);

        return image;
    }

    /// <summary>
    /// The scene as interleaved 8-bit RGB — separated from encoding so a test can assert on the pixels
    /// it asked for without going through a lossy codec.
    /// </summary>
    public static byte[] RenderPixels(SceneSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var width = spec.Width;
        var height = spec.Height;
        var rgb = new byte[(long)width * height * 3];
        var random = new Random(spec.Seed);

        // A handful of low-frequency sinusoids: enough variation that the frame is not flat, but nothing
        // the Laplacian can bite on, so global sharpness is dominated by the subject.
        var phaseX = random.NextDouble() * Math.Tau;
        var phaseY = random.NextDouble() * Math.Tau;

        var subject = spec.Subject;
        var sx0 = subject is { } s0 ? (int)Math.Round(s0.X * width) : 0;
        var sy0 = subject is { } s1 ? (int)Math.Round(s1.Y * height) : 0;
        var sx1 = subject is { } s2 ? (int)Math.Round(s2.Right * width) : -1;
        var sy1 = subject is { } s3 ? (int)Math.Round(s3.Bottom * height) : -1;
        var checkerSize = Math.Max(3, Math.Min(width, height) / 90);

        for (var y = 0; y < height; y++)
        {
            var v = (double)y / height;
            for (var x = 0; x < width; x++)
            {
                var u = (double)x / width;
                var i = ((long)y * width + x) * 3;

                double r, g, b;

                if (subject is not null && x >= sx0 && x < sx1 && y >= sy0 && y < sy1)
                {
                    // The subject: a fine, maximum-contrast checkerboard. High spatial frequency (so it
                    // owns the sharpness map and dies under blur) and extreme local contrast (so it owns
                    // the saliency map).
                    var on = ((x - sx0) / checkerSize + (y - sy0) / checkerSize) % 2 == 0;
                    r = on ? 0.97 : 0.06;
                    g = on ? 0.93 : 0.05;
                    b = on ? 0.86 : 0.09;
                }
                else
                {
                    // The background: a smooth two-axis gradient around mid-gray, tinted by Hue.
                    var wave = 0.5
                               + 0.09 * Math.Sin(u * 2.1 * Math.PI + phaseX)
                               + 0.07 * Math.Sin(v * 1.7 * Math.PI + phaseY);
                    r = wave * (0.78 + 0.30 * spec.Hue);
                    g = wave * 0.94;
                    b = wave * (1.16 - 0.32 * spec.Hue);
                }

                rgb[i] = Encode(r * spec.ExposureScale);
                rgb[i + 1] = Encode(g * spec.ExposureScale);
                rgb[i + 2] = Encode(b * spec.ExposureScale);
            }
        }

        return rgb;
    }

    private static byte Encode(double value) => (byte)Math.Clamp(Math.Round(value * 255.0), 0, 255);

    private static ExifProfile? BuildExifProfile(SceneSpec spec)
    {
        if (spec.ExifTaken is null && spec.Orientation is null &&
            spec.CameraMake is null && spec.CameraModel is null)
        {
            return null;
        }

        var profile = new ExifProfile();

        if (spec.ExifTaken is { } taken)
        {
            var text = taken.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture);
            profile.SetValue(ExifTag.DateTimeOriginal, text);
            profile.SetValue(ExifTag.DateTimeDigitized, text);
        }

        if (spec.Orientation is { } orientation) profile.SetValue(ExifTag.Orientation, orientation);
        if (spec.CameraMake is { } make) profile.SetValue(ExifTag.Make, make);
        if (spec.CameraModel is { } model) profile.SetValue(ExifTag.Model, model);

        return profile;
    }

    // ---------------------------------------------------------------- measurement helpers

    /// <summary>Mean luma (Rec.601, 0..1) of a decoded BGRA buffer.</summary>
    /// <param name="bgra">Tightly packed BGRA8888 bytes.</param>
    public static double MeanLuma(ReadOnlySpan<byte> bgra)
    {
        double sum = 0;
        var pixels = bgra.Length / 4;
        for (var i = 0; i < pixels; i++)
        {
            var s = i * 4;
            sum += 0.299 * bgra[s + 2] + 0.587 * bgra[s + 1] + 0.114 * bgra[s];
        }

        return pixels == 0 ? 0 : sum / pixels / 255.0;
    }

    /// <summary>Standard deviation of luma (0..1) — the measurable stand-in for "contrast".</summary>
    /// <param name="bgra">Tightly packed BGRA8888 bytes.</param>
    public static double LumaStdDev(ReadOnlySpan<byte> bgra)
    {
        double sum = 0, squares = 0;
        var pixels = bgra.Length / 4;
        for (var i = 0; i < pixels; i++)
        {
            var s = i * 4;
            var luma = (0.299 * bgra[s + 2] + 0.587 * bgra[s + 1] + 0.114 * bgra[s]) / 255.0;
            sum += luma;
            squares += luma * luma;
        }

        if (pixels == 0) return 0;
        var mean = sum / pixels;
        return Math.Sqrt(Math.Max(0, squares / pixels - mean * mean));
    }

    /// <summary>Mean HSV saturation (0..1) of a decoded BGRA buffer.</summary>
    /// <param name="bgra">Tightly packed BGRA8888 bytes.</param>
    public static double MeanSaturation(ReadOnlySpan<byte> bgra)
    {
        double sum = 0;
        var pixels = bgra.Length / 4;
        for (var i = 0; i < pixels; i++)
        {
            var s = i * 4;
            int b = bgra[s], g = bgra[s + 1], r = bgra[s + 2];
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            sum += max == 0 ? 0 : (double)(max - min) / max;
        }

        return pixels == 0 ? 0 : sum / pixels;
    }
}
