using Microsoft.ML.OnnxRuntime;
using PhotoBook.Analysis.Classical;
using PhotoBook.Analysis.Pixels;
using PhotoBook.Core.Model;

namespace PhotoBook.Analysis.Onnx;

/// <summary>
/// U2-Netp saliency (doc 06): the safety net for photos with no faces — landscapes, food, the kid's
/// drawing. Input 320×320 with ImageNet normalization, output a single-channel mask that is
/// min-max normalized, thresholded at 0.5 and turned into regions by
/// <see cref="Classical.SaliencyRegions"/>, so U2-Netp and the model-free detector produce regions
/// on identical terms.
/// </summary>
internal sealed class U2NetSaliencyDetector : IDisposable
{
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _inputSize;
    private readonly bool _channelsFirst;
    private readonly OnnxAnalyzerOptions _options;

    private U2NetSaliencyDetector(InferenceSession session, string inputName, int inputSize, bool channelsFirst, OnnxAnalyzerOptions options)
    {
        _session = session;
        _inputName = inputName;
        _inputSize = inputSize;
        _channelsFirst = channelsFirst;
        _options = options;
    }

    /// <summary>Loads the model, or returns null (having logged) when it cannot be used.</summary>
    public static U2NetSaliencyDetector? TryLoad(string modelPath, OnnxAnalyzerOptions options)
    {
        try
        {
            var session = new InferenceSession(modelPath, OnnxTensors.CreateSessionOptions(options.IntraOpThreads));
            var inputName = OnnxTensors.SingleInputName(session);
            var size = OnnxTensors.InputSize(session, options.SaliencyInputSize, out var channelsFirst);
            return new U2NetSaliencyDetector(session, inputName, size, channelsFirst, options);
        }
        catch (Exception ex)
        {
            options.Log?.Invoke($"Saliency model '{modelPath}' failed to load: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Runs the model and extracts focus regions; returns null when inference failed, so the caller
    /// can fall back to the model-free detector instead of losing saliency altogether.
    /// </summary>
    public List<FocusRegion>? Detect(AnalysisImage image, SaliencyRegionShape shape, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            var resized = image.ResizeTo(_inputSize, _inputSize);
            var tensor = OnnxTensors.Build(resized, _inputSize, _inputSize, _channelsFirst, blueFirst: false, 1f / 255f, Mean, Std);

            using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);
            var first = results.FirstOrDefault();
            if (first is null) return null;

            var mask = first.AsEnumerable<float>().ToArray();
            var cells = _inputSize * _inputSize;
            if (mask.Length < cells)
            {
                _options.Log?.Invoke($"Saliency model returned {mask.Length} values, expected at least {cells}; ignoring.");
                return null;
            }

            var map = ImageOps.Normalize01(mask.AsSpan(0, cells).ToArray());
            return SaliencyRegions.Extract(map, _inputSize, _inputSize, _options.SaliencyThreshold, shape);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _options.Log?.Invoke($"Saliency inference failed: {ex.Message}");
            return null;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _session.Dispose();
}
