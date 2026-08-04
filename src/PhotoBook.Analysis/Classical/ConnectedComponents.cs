namespace PhotoBook.Analysis.Classical;

/// <summary>
/// 8-connected component labelling over a boolean mask, with the per-component statistics focus
/// regions need: bounding box, pixel count and mean map value (doc 06's saliency weight is
/// <c>meanMaskValue × min(1, area/0.15)</c>).
/// </summary>
internal static class ConnectedComponents
{
    /// <summary>One connected blob of the thresholded saliency map.</summary>
    /// <param name="MinX">Left-most column, inclusive.</param>
    /// <param name="MinY">Top-most row, inclusive.</param>
    /// <param name="MaxX">Right-most column, inclusive.</param>
    /// <param name="MaxY">Bottom-most row, inclusive.</param>
    /// <param name="PixelCount">Number of pixels in the blob.</param>
    /// <param name="MeanValue">Mean value of the source map over the blob's pixels.</param>
    /// <param name="PeakValue">Highest source-map value inside the blob.</param>
    internal sealed record Component(int MinX, int MinY, int MaxX, int MaxY, int PixelCount, double MeanValue, double PeakValue)
    {
        /// <summary>Bounding-box width in cells.</summary>
        public int BoxWidth => MaxX - MinX + 1;

        /// <summary>Bounding-box height in cells.</summary>
        public int BoxHeight => MaxY - MinY + 1;
    }

    /// <summary>Labels every 8-connected blob of <paramref name="mask"/>, largest (by pixel count) first.</summary>
    /// <param name="mask">Foreground flags, row-major.</param>
    /// <param name="values">The source map the statistics are taken from; same length as the mask.</param>
    /// <param name="width">Grid width.</param>
    /// <param name="height">Grid height.</param>
    public static List<Component> Find(bool[] mask, float[] values, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(values);

        var visited = new bool[mask.Length];
        var components = new List<Component>();
        var queue = new Queue<int>();

        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || visited[start]) continue;

            visited[start] = true;
            queue.Clear();
            queue.Enqueue(start);

            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue, count = 0;
            double sum = 0, peak = 0;

            while (queue.Count > 0)
            {
                var index = queue.Dequeue();
                var x = index % width;
                var y = index / width;

                count++;
                sum += values[index];
                if (values[index] > peak) peak = values[index];
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;

                for (var dy = -1; dy <= 1; dy++)
                {
                    var ny = y + dy;
                    if (ny < 0 || ny >= height) continue;
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        var nx = x + dx;
                        if (nx < 0 || nx >= width) continue;
                        var neighbor = ny * width + nx;
                        if (!mask[neighbor] || visited[neighbor]) continue;
                        visited[neighbor] = true;
                        queue.Enqueue(neighbor);
                    }
                }
            }

            components.Add(new Component(minX, minY, maxX, maxY, count, count > 0 ? sum / count : 0, peak));
        }

        components.Sort((a, b) => b.PixelCount.CompareTo(a.PixelCount));
        return components;
    }
}
