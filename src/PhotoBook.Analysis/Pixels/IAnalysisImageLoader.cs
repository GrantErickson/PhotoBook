namespace PhotoBook.Analysis.Pixels;

/// <summary>
/// How an analyzer gets pixels for the analysis copy (<c>cache/thumbs/1024a/{hash}.jpg</c>, doc 06).
/// It is a seam rather than a hard dependency so the host can hand over a bitmap the job queue
/// already decoded instead of paying for a second decode (doc 02 §2: "when Analysis needs pixels it
/// receives them as input").
/// </summary>
public interface IAnalysisImageLoader
{
    /// <summary>Decodes an image file into 8-bit sRGB pixels.</summary>
    /// <param name="path">Absolute path of the analysis copy.</param>
    /// <param name="maxLongEdge">Cap on the long edge; the analysis copy is already 1024 px.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="OperationCanceledException">The load was cancelled.</exception>
    Task<AnalysisImage> LoadAsync(string path, int maxLongEdge, CancellationToken ct = default);
}
