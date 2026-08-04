using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PhotoBook.Analysis.Pixels;

namespace PhotoBook.Analysis.Onnx;

/// <summary>
/// Pixel-to-tensor plumbing shared by the three model wrappers, plus the small amount of session
/// metadata inspection that lets them adapt to a model whose input layout is not what was expected
/// instead of crashing on it.
/// </summary>
internal static class OnnxTensors
{
    /// <summary>Session options for every model: CPU EP, capped intra-op threads, deterministic.</summary>
    public static SessionOptions CreateSessionOptions(int intraOpThreads) => new()
    {
        IntraOpNumThreads = Math.Max(1, intraOpThreads),
        InterOpNumThreads = 1,
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
    };

    /// <summary>
    /// Builds an input tensor from an image already resized to <paramref name="width"/> ×
    /// <paramref name="height"/>.
    /// </summary>
    /// <param name="image">Source pixels, exactly the target size.</param>
    /// <param name="width">Tensor width.</param>
    /// <param name="height">Tensor height.</param>
    /// <param name="channelsFirst">True for NCHW, false for NHWC.</param>
    /// <param name="blueFirst">True when the model expects BGR channel order.</param>
    /// <param name="scale">Multiplier applied to the raw 0..255 byte before mean/std.</param>
    /// <param name="mean">Per-channel mean subtracted after scaling, in the model's channel order.</param>
    /// <param name="std">Per-channel divisor applied last, in the model's channel order.</param>
    public static DenseTensor<float> Build(
        AnalysisImage image,
        int width,
        int height,
        bool channelsFirst,
        bool blueFirst,
        float scale,
        float[] mean,
        float[] std)
    {
        ArgumentNullException.ThrowIfNull(image);

        var dimensions = channelsFirst ? new[] { 1, 3, height, width } : new[] { 1, height, width, 3 };
        var tensor = new DenseTensor<float>(dimensions);
        var buffer = tensor.Buffer.Span;
        var rgb = image.Rgb;
        var plane = width * height;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixel = y * width + x;
                var source = pixel * 3;
                for (var c = 0; c < 3; c++)
                {
                    var channelByte = blueFirst ? rgb[source + (2 - c)] : rgb[source + c];
                    var value = (channelByte * scale - mean[c]) / std[c];
                    buffer[channelsFirst ? c * plane + pixel : pixel * 3 + c] = value;
                }
            }
        }

        return tensor;
    }

    /// <summary>
    /// Letterboxes an image into a square canvas: aspect-preserving resize into the top-left corner
    /// of a black square of <paramref name="size"/> pixels. Returns the canvas and the scale that
    /// maps canvas pixels back to the source.
    /// </summary>
    /// <param name="image">Source pixels.</param>
    /// <param name="size">Edge of the square canvas.</param>
    public static (AnalysisImage Canvas, double Scale, int ContentWidth, int ContentHeight) Letterbox(AnalysisImage image, int size)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);

        var scale = (double)size / Math.Max(image.Width, image.Height);
        var contentWidth = Math.Max(1, Math.Min(size, (int)Math.Round(image.Width * scale)));
        var contentHeight = Math.Max(1, Math.Min(size, (int)Math.Round(image.Height * scale)));

        var resized = image.ResizeTo(contentWidth, contentHeight);
        var canvas = new byte[size * size * 3];
        for (var y = 0; y < contentHeight; y++)
        {
            Array.Copy(resized.Rgb, y * contentWidth * 3, canvas, y * size * 3, contentWidth * 3);
        }

        return (new AnalysisImage(size, size, canvas), scale, contentWidth, contentHeight);
    }

    /// <summary>The single input name of a session.</summary>
    public static string SingleInputName(InferenceSession session) => session.InputMetadata.Keys.First();

    /// <summary>
    /// Reads a square input size out of the session metadata, falling back to
    /// <paramref name="fallback"/> when the model has dynamic dimensions.
    /// </summary>
    /// <param name="session">The session to inspect.</param>
    /// <param name="fallback">Size to use when the metadata does not pin one.</param>
    /// <param name="channelsFirst">Set to true when the input is NCHW, false when NHWC.</param>
    public static int InputSize(InferenceSession session, int fallback, out bool channelsFirst)
    {
        channelsFirst = true;
        var metadata = session.InputMetadata.Values.FirstOrDefault();
        var dimensions = metadata?.Dimensions;
        if (dimensions is not { Length: 4 }) return fallback;

        if (dimensions[3] == 3 && dimensions[1] != 3)
        {
            channelsFirst = false;
            var nhwc = dimensions[1] > 0 ? dimensions[1] : fallback;
            return nhwc;
        }

        var nchw = dimensions[2] > 0 ? dimensions[2] : fallback;
        return nchw;
    }

    /// <summary>Flattens a named output tensor to an array, or null when the output is absent.</summary>
    public static float[]? TryFloats(IReadOnlyCollection<DisposableNamedOnnxValue> outputs, string name)
    {
        foreach (var output in outputs)
        {
            if (!string.Equals(output.Name, name, StringComparison.Ordinal)) continue;
            return output.AsEnumerable<float>().ToArray();
        }

        return null;
    }
}
