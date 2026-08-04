namespace PhotoBook.Core.Abstractions;

/// <summary>The three fixed thumbnail tiers of kernel §10 / doc 05.</summary>
public enum ThumbnailTier
{
    /// <summary>256 px long edge, JPEG q80 — the Photos grid and the bins. Adjustments applied.</summary>
    Grid256,

    /// <summary>1024 px long edge, JPEG q85 — the Pages tab render. Adjustments applied.</summary>
    Preview1024,

    /// <summary>
    /// 1024 px long edge, <b>pre-adjustment</b> — the analysis copy (<c>cache/thumbs/1024a/</c>).
    /// Analysis reads this, so fixing a photo's exposure never silently re-tiers it (doc 05).
    /// </summary>
    Analysis1024,
}
