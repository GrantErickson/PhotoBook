using Microsoft.ML.OnnxRuntime;
using PhotoBook.Analysis.Pixels;
using PhotoBook.Core.Model;

namespace PhotoBook.Analysis.Onnx;

/// <summary>
/// YuNet face detection (doc 06). This is a family memory book: faces are the subject, and both
/// smart-crop and the tier score lean on them, so a face box is worth far more than a salient blob.
/// <para>
/// The model is anchor-free with three strides (8, 16, 32); each stride emits a classification
/// score, an objectness score, a box offset and five landmarks per cell. Decoding is the standard
/// YuNet post-process: <c>score = sqrt(cls · obj)</c>, <c>cx = (col + dx)·stride</c>,
/// <c>w = exp(dw)·stride</c>, then non-maximum suppression.
/// </para>
/// </summary>
internal sealed class YuNetFaceDetector : IDisposable
{
    private static readonly int[] Strides = [8, 16, 32];

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly OnnxAnalyzerOptions _options;

    private YuNetFaceDetector(InferenceSession session, string inputName, OnnxAnalyzerOptions options)
    {
        _session = session;
        _inputName = inputName;
        _options = options;
    }

    /// <summary>One detected face in normalized image coordinates.</summary>
    /// <param name="Rect">The box, normalized to the source image.</param>
    /// <param name="Confidence">Detection score, <c>0..1</c>.</param>
    internal sealed record Face(Rect Rect, double Confidence);

    /// <summary>Loads the model, or returns null (having logged) when it cannot be used.</summary>
    public static YuNetFaceDetector? TryLoad(string modelPath, OnnxAnalyzerOptions options)
    {
        try
        {
            var session = new InferenceSession(modelPath, OnnxTensors.CreateSessionOptions(options.IntraOpThreads));
            var inputName = OnnxTensors.SingleInputName(session);

            var outputs = session.OutputMetadata.Keys;
            if (!Strides.All(s => outputs.Contains($"cls_{s}") && outputs.Contains($"obj_{s}") && outputs.Contains($"bbox_{s}")))
            {
                options.Log?.Invoke(
                    $"Face model '{Path.GetFileName(modelPath)}' does not expose the expected cls_/obj_/bbox_ outputs; face detection disabled.");
                session.Dispose();
                return null;
            }

            return new YuNetFaceDetector(session, inputName, options);
        }
        catch (Exception ex)
        {
            options.Log?.Invoke($"Face model '{modelPath}' failed to load: {ex.Message}");
            return null;
        }
    }

    /// <summary>Detects faces on the analysis image; returns an empty list rather than throwing.</summary>
    public IReadOnlyList<Face> Detect(AnalysisImage image, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            var size = RoundUpTo32(_options.FaceInputSize);
            var (canvas, scale, _, _) = OnnxTensors.Letterbox(image, size);

            // YuNet consumes raw BGR bytes with no normalization.
            var tensor = OnnxTensors.Build(canvas, size, size, channelsFirst: true, blueFirst: true, 1f, [0, 0, 0], [1, 1, 1]);
            using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);
            var outputs = results.ToList();

            var candidates = new List<Face>();
            foreach (var stride in Strides)
            {
                var cls = OnnxTensors.TryFloats(outputs, $"cls_{stride}");
                var obj = OnnxTensors.TryFloats(outputs, $"obj_{stride}");
                var box = OnnxTensors.TryFloats(outputs, $"bbox_{stride}");
                if (cls is null || obj is null || box is null) continue;

                var columns = size / stride;
                var rows = size / stride;
                var cells = Math.Min(cls.Length, Math.Min(obj.Length, box.Length / 4));

                for (var i = 0; i < cells && i < columns * rows; i++)
                {
                    var score = Math.Sqrt(Math.Clamp(cls[i], 0, 1) * Math.Clamp(obj[i], 0, 1));
                    if (score < _options.FaceScoreThreshold) continue;

                    var column = i % columns;
                    var row = i / columns;
                    var centerX = (column + box[i * 4]) * stride;
                    var centerY = (row + box[i * 4 + 1]) * stride;
                    var width = Math.Exp(box[i * 4 + 2]) * stride;
                    var height = Math.Exp(box[i * 4 + 3]) * stride;

                    // Canvas pixels → source pixels → normalized image coordinates. The letterbox
                    // content sits at the top-left, so only the scale has to be undone.
                    var left = (centerX - width / 2) / scale / image.Width;
                    var top = (centerY - height / 2) / scale / image.Height;
                    var normalizedWidth = width / scale / image.Width;
                    var normalizedHeight = height / scale / image.Height;

                    var rect = Rect.FromEdges(
                        Math.Clamp(left, 0, 1),
                        Math.Clamp(top, 0, 1),
                        Math.Clamp(left + normalizedWidth, 0, 1),
                        Math.Clamp(top + normalizedHeight, 0, 1));

                    if (!rect.IsWellFormed) continue;
                    candidates.Add(new Face(rect, score));
                }

                ct.ThrowIfCancellationRequested();
            }

            return NonMaximumSuppression(candidates, _options.FaceNmsIoU);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _options.Log?.Invoke($"Face detection failed: {ex.Message}");
            return [];
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _session.Dispose();

    private static int RoundUpTo32(int size) => Math.Max(32, (size + 31) / 32 * 32);

    private static List<Face> NonMaximumSuppression(List<Face> candidates, double iouThreshold)
    {
        var kept = new List<Face>();
        foreach (var face in candidates.OrderByDescending(f => f.Confidence).ThenBy(f => f.Rect.X).ThenBy(f => f.Rect.Y))
        {
            if (kept.Any(k => k.Rect.IoU(face.Rect) > iouThreshold)) continue;
            kept.Add(face);
        }

        return kept;
    }
}
