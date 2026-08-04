using Microsoft.ML.OnnxRuntime;
using PhotoBook.Analysis.Pixels;

namespace PhotoBook.Analysis.Onnx;

/// <summary>
/// NIMA on a MobileNet backbone (doc 06): a 10-bin score distribution over the AVA aesthetic scale.
/// Only the distribution mean is used — <c>aesthetic = (mean − 1) / 9</c> — because v1 keeps every
/// quality signal scalar and explainable.
/// </summary>
internal sealed class NimaAestheticScorer : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _inputSize;
    private readonly bool _channelsFirst;
    private readonly OnnxAnalyzerOptions _options;

    private NimaAestheticScorer(InferenceSession session, string inputName, int inputSize, bool channelsFirst, OnnxAnalyzerOptions options)
    {
        _session = session;
        _inputName = inputName;
        _inputSize = inputSize;
        _channelsFirst = channelsFirst;
        _options = options;
    }

    /// <summary>Loads the model, or returns null (having logged) when it cannot be used.</summary>
    public static NimaAestheticScorer? TryLoad(string modelPath, OnnxAnalyzerOptions options)
    {
        try
        {
            var session = new InferenceSession(modelPath, OnnxTensors.CreateSessionOptions(options.IntraOpThreads));
            var inputName = OnnxTensors.SingleInputName(session);
            var size = OnnxTensors.InputSize(session, options.AestheticInputSize, out var channelsFirst);
            return new NimaAestheticScorer(session, inputName, size, channelsFirst, options);
        }
        catch (Exception ex)
        {
            options.Log?.Invoke($"Aesthetic model '{modelPath}' failed to load: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Scores the image <c>0..1</c>, or returns null when inference failed or the output was not a
    /// 10-bin distribution — the caller then keeps the classical aesthetic proxy.
    /// </summary>
    public double? Score(AnalysisImage image, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            var resized = image.ResizeTo(_inputSize, _inputSize);

            // MobileNet preprocessing: x/127.5 − 1.
            var tensor = OnnxTensors.Build(
                resized, _inputSize, _inputSize, _channelsFirst, blueFirst: false, 1f / 127.5f, [1f, 1f, 1f], [1f, 1f, 1f]);

            using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);
            var first = results.FirstOrDefault();
            if (first is null) return null;

            var distribution = first.AsEnumerable<float>().ToArray();
            if (distribution.Length != 10)
            {
                _options.Log?.Invoke($"Aesthetic model returned {distribution.Length} values, expected a 10-bin distribution; ignoring.");
                return null;
            }

            double sum = 0;
            foreach (var value in distribution) sum += value;
            if (sum <= 1e-6) return null;

            double mean = 0;
            for (var i = 0; i < distribution.Length; i++) mean += (i + 1) * (distribution[i] / sum);

            return ImageOps.Clamp01((mean - 1) / 9.0);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _options.Log?.Invoke($"Aesthetic inference failed: {ex.Message}");
            return null;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _session.Dispose();
}
