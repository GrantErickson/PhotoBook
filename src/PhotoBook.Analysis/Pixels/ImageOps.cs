namespace PhotoBook.Analysis.Pixels;

/// <summary>
/// The handful of pure pixel kernels the classical pipeline needs: area-average resampling, box
/// blur and normalization. Deliberately tiny and dependency-free — Analysis must not call Imaging
/// (doc 02 §2), and these are the only operations the maths below relies on.
/// </summary>
internal static class ImageOps
{
    /// <summary>Area-averaged (box) resample of a single-channel plane. Exact for both up- and downscale.</summary>
    public static float[] ResampleArea(float[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetHeight, 1);

        var result = new float[targetWidth * targetHeight];
        var sx = (double)sourceWidth / targetWidth;
        var sy = (double)sourceHeight / targetHeight;

        for (var ty = 0; ty < targetHeight; ty++)
        {
            var y0 = ty * sy;
            var y1 = (ty + 1) * sy;
            var iy0 = (int)Math.Floor(y0);
            var iy1 = Math.Min(sourceHeight, (int)Math.Ceiling(y1));
            if (iy1 <= iy0) iy1 = Math.Min(sourceHeight, iy0 + 1);

            for (var tx = 0; tx < targetWidth; tx++)
            {
                var x0 = tx * sx;
                var x1 = (tx + 1) * sx;
                var ix0 = (int)Math.Floor(x0);
                var ix1 = Math.Min(sourceWidth, (int)Math.Ceiling(x1));
                if (ix1 <= ix0) ix1 = Math.Min(sourceWidth, ix0 + 1);

                double sum = 0;
                double weight = 0;
                for (var y = iy0; y < iy1; y++)
                {
                    var wy = Math.Min(y + 1.0, y1) - Math.Max(y, y0);
                    if (wy <= 0) continue;
                    var row = y * sourceWidth;
                    for (var x = ix0; x < ix1; x++)
                    {
                        var wx = Math.Min(x + 1.0, x1) - Math.Max(x, x0);
                        if (wx <= 0) continue;
                        var w = wx * wy;
                        sum += source[row + x] * w;
                        weight += w;
                    }
                }

                result[ty * targetWidth + tx] = weight > 0 ? (float)(sum / weight) : 0f;
            }
        }

        return result;
    }

    /// <summary>Area-averaged resample of an interleaved RGB buffer.</summary>
    public static byte[] ResampleAreaRgb(byte[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = new byte[targetWidth * targetHeight * 3];
        var sx = (double)sourceWidth / targetWidth;
        var sy = (double)sourceHeight / targetHeight;

        for (var ty = 0; ty < targetHeight; ty++)
        {
            var y0 = ty * sy;
            var y1 = (ty + 1) * sy;
            var iy0 = (int)Math.Floor(y0);
            var iy1 = Math.Min(sourceHeight, (int)Math.Ceiling(y1));
            if (iy1 <= iy0) iy1 = Math.Min(sourceHeight, iy0 + 1);

            for (var tx = 0; tx < targetWidth; tx++)
            {
                var x0 = tx * sx;
                var x1 = (tx + 1) * sx;
                var ix0 = (int)Math.Floor(x0);
                var ix1 = Math.Min(sourceWidth, (int)Math.Ceiling(x1));
                if (ix1 <= ix0) ix1 = Math.Min(sourceWidth, ix0 + 1);

                double r = 0, g = 0, b = 0, weight = 0;
                for (var y = iy0; y < iy1; y++)
                {
                    var wy = Math.Min(y + 1.0, y1) - Math.Max(y, y0);
                    if (wy <= 0) continue;
                    var row = y * sourceWidth;
                    for (var x = ix0; x < ix1; x++)
                    {
                        var wx = Math.Min(x + 1.0, x1) - Math.Max(x, x0);
                        if (wx <= 0) continue;
                        var w = wx * wy;
                        var s = (row + x) * 3;
                        r += source[s] * w;
                        g += source[s + 1] * w;
                        b += source[s + 2] * w;
                        weight += w;
                    }
                }

                var d = (ty * targetWidth + tx) * 3;
                if (weight > 0)
                {
                    result[d] = ClampByte(r / weight);
                    result[d + 1] = ClampByte(g / weight);
                    result[d + 2] = ClampByte(b / weight);
                }
            }
        }

        return result;
    }

    /// <summary>Separable box blur with edge clamping; <paramref name="radius"/> 0 returns a copy.</summary>
    public static float[] BoxBlur(float[] source, int width, int height, int radius)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (radius <= 0) return (float[])source.Clone();

        var temp = new float[source.Length];
        var result = new float[source.Length];

        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                double sum = 0;
                var count = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    var xx = Math.Clamp(x + k, 0, width - 1);
                    sum += source[row + xx];
                    count++;
                }

                temp[row + x] = (float)(sum / count);
            }
        }

        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                double sum = 0;
                var count = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    var yy = Math.Clamp(y + k, 0, height - 1);
                    sum += temp[yy * width + x];
                    count++;
                }

                result[y * width + x] = (float)(sum / count);
            }
        }

        return result;
    }

    /// <summary>
    /// Three successive box blurs — the standard cheap approximation of a Gaussian (the central
    /// limit theorem does the work) used to smooth the saliency map before thresholding.
    /// </summary>
    public static float[] ApproximateGaussian(float[] source, int width, int height, int radius)
    {
        var a = BoxBlur(source, width, height, radius);
        var b = BoxBlur(a, width, height, radius);
        return BoxBlur(b, width, height, radius);
    }

    /// <summary>Rescales a plane to <c>0..1</c> by its own min and max; a flat plane becomes all zeros.</summary>
    public static float[] Normalize01(float[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length == 0) return source;

        var min = float.MaxValue;
        var max = float.MinValue;
        foreach (var v in source)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }

        var result = new float[source.Length];
        var range = max - min;
        if (range <= 1e-9f) return result;
        for (var i = 0; i < source.Length; i++) result[i] = (source[i] - min) / range;
        return result;
    }

    /// <summary>Bilinear upsample of a coarse plane onto a finer grid (tile maps onto the saliency grid).</summary>
    public static float[] UpsampleBilinear(float[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = new float[targetWidth * targetHeight];
        for (var ty = 0; ty < targetHeight; ty++)
        {
            var fy = sourceHeight == 1 ? 0 : (ty + 0.5) * sourceHeight / targetHeight - 0.5;
            var y0 = Math.Clamp((int)Math.Floor(fy), 0, sourceHeight - 1);
            var y1 = Math.Clamp(y0 + 1, 0, sourceHeight - 1);
            var wy = Math.Clamp(fy - y0, 0, 1);

            for (var tx = 0; tx < targetWidth; tx++)
            {
                var fx = sourceWidth == 1 ? 0 : (tx + 0.5) * sourceWidth / targetWidth - 0.5;
                var x0 = Math.Clamp((int)Math.Floor(fx), 0, sourceWidth - 1);
                var x1 = Math.Clamp(x0 + 1, 0, sourceWidth - 1);
                var wx = Math.Clamp(fx - x0, 0, 1);

                var top = source[y0 * sourceWidth + x0] * (1 - wx) + source[y0 * sourceWidth + x1] * wx;
                var bottom = source[y1 * sourceWidth + x0] * (1 - wx) + source[y1 * sourceWidth + x1] * wx;
                result[ty * targetWidth + tx] = (float)(top * (1 - wy) + bottom * wy);
            }
        }

        return result;
    }

    /// <summary>Clamps a double to <c>[0,1]</c>; NaN becomes 0.</summary>
    public static double Clamp01(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);

    private static byte ClampByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}
