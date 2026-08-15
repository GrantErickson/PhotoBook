namespace PhotoBook.Analysis.Pixels;

/// <summary>
/// Decoded pixels for one analysis pass: interleaved 8-bit sRGB plus a cached Rec.601 luma plane.
/// <para>
/// This is the only pixel type <c>PhotoBook.Analysis</c> knows. Per the dependency rule (doc 02 §2)
/// Analysis never calls <c>PhotoBook.Imaging</c>: it either receives pixels the job queue already
/// decoded (<see cref="FromRgb"/> / <see cref="FromRgba"/>) or decodes the analysis copy itself
/// through <see cref="IAnalysisImageLoader"/>.
/// </para>
/// <para>
/// Luma is stored on the <c>0..255</c> scale because doc 06's classical formulas
/// (<c>log10(1 + varLap)/3</c>, the <c>8/255</c> and <c>247/255</c> clipping thresholds) are stated
/// against 8-bit grayscale.
/// </para>
/// </summary>
public sealed class AnalysisImage
{
    private float[]? _luma;

    /// <summary>Creates an image over an interleaved RGB buffer. The buffer is taken by reference, not copied.</summary>
    /// <param name="width">Width in pixels; must be positive.</param>
    /// <param name="height">Height in pixels; must be positive.</param>
    /// <param name="rgb">Interleaved RGB bytes, exactly <c>width × height × 3</c> long.</param>
    public AnalysisImage(int width, int height, byte[] rgb)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentNullException.ThrowIfNull(rgb);
        if (rgb.Length < (long)width * height * 3)
            throw new ArgumentException($"Expected at least {(long)width * height * 3} bytes for {width}×{height} RGB, got {rgb.Length}.", nameof(rgb));

        Width = width;
        Height = height;
        Rgb = rgb;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Interleaved RGB bytes, 3 per pixel, row-major from the top-left.</summary>
    public byte[] Rgb { get; }

    /// <summary>Number of pixels.</summary>
    public int PixelCount => Width * Height;

    /// <summary>Width divided by height.</summary>
    public double Aspect => (double)Width / Height;

    /// <summary>
    /// Rec.601 luma (<c>0.299R + 0.587G + 0.114B</c>) on the <c>0..255</c> scale, one entry per
    /// pixel. Computed once on first use.
    /// </summary>
    public float[] Luma => _luma ??= ComputeLuma();

    /// <summary>Wraps an interleaved RGB buffer.</summary>
    public static AnalysisImage FromRgb(byte[] rgb, int width, int height) => new(width, height, rgb);

    /// <summary>Copies an interleaved RGBA/BGRA buffer down to RGB, ignoring alpha.</summary>
    /// <param name="rgba">Interleaved 4-byte pixels.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="blueFirst">True when the buffer is BGRA rather than RGBA.</param>
    public static AnalysisImage FromRgba(byte[] rgba, int width, int height, bool blueFirst = false)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        var count = width * height;
        if (rgba.Length < (long)count * 4)
            throw new ArgumentException($"Expected at least {(long)count * 4} bytes for {width}×{height} RGBA.", nameof(rgba));

        var rgb = new byte[count * 3];
        for (var i = 0; i < count; i++)
        {
            var s = i * 4;
            var d = i * 3;
            if (blueFirst)
            {
                rgb[d] = rgba[s + 2];
                rgb[d + 1] = rgba[s + 1];
                rgb[d + 2] = rgba[s];
            }
            else
            {
                rgb[d] = rgba[s];
                rgb[d + 1] = rgba[s + 1];
                rgb[d + 2] = rgba[s + 2];
            }
        }

        return new AnalysisImage(width, height, rgb);
    }

    /// <summary>
    /// Area-averaged resample of the luma plane to an arbitrary grid — the input of the saliency
    /// stage. Area averaging (rather than nearest) keeps the spectral residual free of the aliasing
    /// a point-sampled downscale would inject.
    /// </summary>
    public float[] ResampleLuma(int targetWidth, int targetHeight) =>
        ImageOps.ResampleArea(Luma, Width, Height, targetWidth, targetHeight);

    /// <summary>Area-averaged resample of the RGB pixels — the input of the ONNX models.</summary>
    public AnalysisImage ResizeTo(int targetWidth, int targetHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(targetWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetHeight, 1);
        if (targetWidth == Width && targetHeight == Height) return this;
        return new AnalysisImage(targetWidth, targetHeight,
            ImageOps.ResampleAreaRgb(Rgb, Width, Height, targetWidth, targetHeight));
    }

    /// <summary>
    /// Area-averaged resample so the long edge is at most <paramref name="maxLongEdge"/>; returns
    /// this instance when it is already small enough.
    /// </summary>
    public AnalysisImage LimitLongEdge(int maxLongEdge)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLongEdge, 1);
        var longEdge = Math.Max(Width, Height);
        if (longEdge <= maxLongEdge) return this;
        var scale = (double)maxLongEdge / longEdge;
        var w = Math.Max(1, (int)Math.Round(Width * scale));
        var h = Math.Max(1, (int)Math.Round(Height * scale));
        return ResizeTo(w, h);
    }

    private float[] ComputeLuma()
    {
        var luma = new float[PixelCount];
        var rgb = Rgb;
        for (var i = 0; i < luma.Length; i++)
        {
            var s = i * 3;
            luma[i] = 0.299f * rgb[s] + 0.587f * rgb[s + 1] + 0.114f * rgb[s + 2];
        }

        return luma;
    }
}
