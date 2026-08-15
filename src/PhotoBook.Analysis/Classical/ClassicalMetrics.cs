using PhotoBook.Analysis.Pixels;

namespace PhotoBook.Analysis.Classical;

/// <summary>
/// The classical, model-free measurements of doc 06: variance of the Laplacian for sharpness (global
/// and per tile), luma-histogram statistics for exposure, and the Hasler–Süsstrunk colorfulness
/// metric. All of them are cheap, robust and deterministic — identical bytes give identical numbers,
/// which is what lets analysis outputs be snapshot as goldens (kernel §7).
/// </summary>
internal static class ClassicalMetrics
{
    /// <summary>Sharpness measured from the Laplacian, globally and over a tile grid.</summary>
    /// <param name="LaplacianVariance">Raw variance of the Laplacian on the <c>0..255</c> luma plane.</param>
    /// <param name="Normalized">Doc 06's <c>clamp01(log10(1 + varLap) / 3)</c>.</param>
    /// <param name="TileNormalized">Per-tile normalized sharpness, row-major over the tile grid.</param>
    /// <param name="TileColumns">Tile-grid width.</param>
    /// <param name="TileRows">Tile-grid height.</param>
    internal sealed record SharpnessResult(
        double LaplacianVariance,
        double Normalized,
        float[] TileNormalized,
        int TileColumns,
        int TileRows);

    /// <summary>Exposure measured from the luma histogram.</summary>
    /// <param name="Score">Doc 06's <c>clamp01(1 − 2·clipLo − 2·clipHi − |mid − 0.5|)</c>.</param>
    /// <param name="ClipLow">Fraction of pixels below <c>8/255</c>.</param>
    /// <param name="ClipHigh">Fraction of pixels above <c>247/255</c>.</param>
    /// <param name="MeanLuma">Mean luma, <c>0..1</c>.</param>
    /// <param name="DynamicRange">The 1st-to-99th percentile luma spread, <c>0..1</c>.</param>
    internal sealed record ExposureResult(double Score, double ClipLow, double ClipHigh, double MeanLuma, double DynamicRange);

    /// <summary>Hasler–Süsstrunk colorfulness.</summary>
    /// <param name="Metric">The raw metric <c>σ_rgyb + 0.3·μ_rgyb</c>; roughly 0 (grayscale) to ~150 (vivid).</param>
    /// <param name="Normalized">The metric divided by 100 and clamped — "not colorful" to "very colorful".</param>
    internal sealed record ColorfulnessResult(double Metric, double Normalized);

    /// <summary>The luma value below which a pixel counts as crushed (doc 06: <c>8/255</c>).</summary>
    public const double ShadowClipThreshold = 8.0;

    /// <summary>The luma value above which a pixel counts as blown (doc 06: <c>247/255</c>).</summary>
    public const double HighlightClipThreshold = 247.0;

    /// <summary>
    /// Variance of the 4-neighbour Laplacian, globally and per tile. Motion blur and missed focus —
    /// the two biggest "why is this photo big?" complaints — crater this signal; the tile map also
    /// tells the saliency stage <em>where</em> the in-focus subject is, which is how a shallow
    /// depth-of-field portrait gets cropped around the face rather than the bokeh.
    /// </summary>
    /// <param name="image">The decoded analysis image.</param>
    /// <param name="tilesOnLongEdge">Tile-grid resolution along the long edge.</param>
    public static SharpnessResult Sharpness(AnalysisImage image, int tilesOnLongEdge)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfLessThan(tilesOnLongEdge, 1);

        var width = image.Width;
        var height = image.Height;
        var luma = image.Luma;

        var laplacian = new float[width * height];
        for (var y = 1; y < height - 1; y++)
        {
            var row = y * width;
            for (var x = 1; x < width - 1; x++)
            {
                var i = row + x;
                laplacian[i] = 4f * luma[i] - luma[i - 1] - luma[i + 1] - luma[i - width] - luma[i + width];
            }
        }

        double sum = 0, sumSquares = 0;
        long count = 0;
        for (var y = 1; y < height - 1; y++)
        {
            var row = y * width;
            for (var x = 1; x < width - 1; x++)
            {
                var v = laplacian[row + x];
                sum += v;
                sumSquares += (double)v * v;
                count++;
            }
        }

        var variance = count > 0 ? Math.Max(0, sumSquares / count - (sum / count) * (sum / count)) : 0;

        int columns, rows;
        if (width >= height)
        {
            columns = tilesOnLongEdge;
            rows = Math.Max(1, (int)Math.Round(tilesOnLongEdge * (double)height / width));
        }
        else
        {
            rows = tilesOnLongEdge;
            columns = Math.Max(1, (int)Math.Round(tilesOnLongEdge * (double)width / height));
        }

        var tiles = new float[columns * rows];
        for (var ty = 0; ty < rows; ty++)
        {
            var y0 = Math.Max(1, ty * height / rows);
            var y1 = Math.Min(height - 1, (ty + 1) * height / rows);
            for (var tx = 0; tx < columns; tx++)
            {
                var x0 = Math.Max(1, tx * width / columns);
                var x1 = Math.Min(width - 1, (tx + 1) * width / columns);

                double tileSum = 0, tileSquares = 0;
                long tileCount = 0;
                for (var y = y0; y < y1; y++)
                {
                    var row = y * width;
                    for (var x = x0; x < x1; x++)
                    {
                        var v = laplacian[row + x];
                        tileSum += v;
                        tileSquares += (double)v * v;
                        tileCount++;
                    }
                }

                var tileVariance = tileCount > 0
                    ? Math.Max(0, tileSquares / tileCount - (tileSum / tileCount) * (tileSum / tileCount))
                    : 0;
                tiles[ty * columns + tx] = (float)NormalizeLaplacianVariance(tileVariance);
            }
        }

        return new SharpnessResult(variance, NormalizeLaplacianVariance(variance), tiles, columns, rows);
    }

    /// <summary>Doc 06's sharpness normalization: <c>clamp01(log10(1 + varLap) / 3)</c>.</summary>
    public static double NormalizeLaplacianVariance(double variance) =>
        ImageOps.Clamp01(Math.Log10(1 + Math.Max(0, variance)) / 3.0);

    /// <summary>
    /// Exposure from the luma histogram: clipping at both ends, distance of the mean from mid-gray,
    /// and the dynamic range. Blown skies and black mush both read as defects, while a deliberately
    /// dark-but-unclipped photo is only gently penalized — the <c>|mid − 0.5|</c> term is soft by
    /// design (doc 06).
    /// </summary>
    public static ExposureResult Exposure(AnalysisImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var histogram = new int[256];
        var luma = image.Luma;
        double sum = 0;
        for (var i = 0; i < luma.Length; i++)
        {
            var v = (int)Math.Clamp(MathF.Round(luma[i]), 0, 255);
            histogram[v]++;
            sum += v;
        }

        var total = (double)luma.Length;
        double clipLow = 0, clipHigh = 0;
        for (var v = 0; v < ShadowClipThreshold; v++) clipLow += histogram[v];
        for (var v = (int)HighlightClipThreshold + 1; v < 256; v++) clipHigh += histogram[v];
        clipLow /= total;
        clipHigh /= total;

        var mean = sum / total / 255.0;
        var score = ImageOps.Clamp01(1 - 2 * clipLow - 2 * clipHigh - Math.Abs(mean - 0.5));

        var low = Percentile(histogram, total, 0.01);
        var high = Percentile(histogram, total, 0.99);
        var dynamicRange = ImageOps.Clamp01((high - low) / 255.0);

        return new ExposureResult(score, clipLow, clipHigh, mean, dynamicRange);
    }

    /// <summary>
    /// Hasler–Süsstrunk colorfulness: <c>σ(rg,yb) + 0.3·μ(rg,yb)</c> over the opponent channels
    /// <c>rg = R − G</c> and <c>yb = ½(R + G) − B</c>. It correlates well with how colorful people
    /// say a photo is, which is the only aesthetic cue available with no model files (doc 06's NIMA
    /// slot).
    /// </summary>
    public static ColorfulnessResult Colorfulness(AnalysisImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var rgb = image.Rgb;
        var count = image.PixelCount;
        double sumRg = 0, sumYb = 0, sumRg2 = 0, sumYb2 = 0;

        for (var i = 0; i < count; i++)
        {
            var s = i * 3;
            double r = rgb[s], g = rgb[s + 1], b = rgb[s + 2];
            var rg = r - g;
            var yb = 0.5 * (r + g) - b;
            sumRg += rg;
            sumYb += yb;
            sumRg2 += rg * rg;
            sumYb2 += yb * yb;
        }

        var meanRg = sumRg / count;
        var meanYb = sumYb / count;
        var varianceRg = Math.Max(0, sumRg2 / count - meanRg * meanRg);
        var varianceYb = Math.Max(0, sumYb2 / count - meanYb * meanYb);

        var sigma = Math.Sqrt(varianceRg + varianceYb);
        var mu = Math.Sqrt(meanRg * meanRg + meanYb * meanYb);
        var metric = sigma + 0.3 * mu;

        return new ColorfulnessResult(metric, ImageOps.Clamp01(metric / 100.0));
    }

    private static double Percentile(int[] histogram, double total, double fraction)
    {
        var target = total * fraction;
        double running = 0;
        for (var v = 0; v < histogram.Length; v++)
        {
            running += histogram[v];
            if (running >= target) return v;
        }

        return 255;
    }
}
