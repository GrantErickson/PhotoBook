namespace PhotoBook.Analysis;

/// <summary>
/// The stable analyzer ids. They are part of every analysis cache key
/// (<c>cache/analysis/{contentHash}.{analyzerId}.json</c>, doc 06) and the value of
/// <c>book.json</c>'s <c>analysis.analyzerId</c> (kernel §10), so they are constants, never
/// literals sprinkled around.
/// </summary>
public static class AnalyzerIds
{
    /// <summary>The default local ONNX pipeline: YuNet + U2-Netp + NIMA over classical metrics.</summary>
    public const string LocalOnnx = "local-onnx";

    /// <summary>
    /// The model-free pipeline. Not named in doc 06 because doc 06 assumes the models are present;
    /// it exists because they legally cannot be bundled, so this is what an untouched install runs.
    /// </summary>
    public const string Classical = "classical";

    /// <summary>The optional, opt-in Azure AI Vision adapter (doc 06; not implemented in v1).</summary>
    public const string AzureVision = "azure-vision";
}
