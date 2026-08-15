namespace PhotoBook.Core.Model;

/// <summary>Per-book analysis opt-ins (kernel §10, doc 06).</summary>
public sealed record AnalysisSettings
{
    /// <summary>The analyzer to use: <c>local-onnx</c> by default, or the opt-in <c>azure-vision</c>.</summary>
    public string AnalyzerId { get; set; } = "local-onnx";
}
