namespace PhotoBook.Core.Model;

/// <summary>
/// A <b>view</b> over two facing pages — never a stored entity (kernel §4, doc 03 §9 Decision).
/// Everything a spread "knows" (the 22 × 8.5 in panorama box, the gutter caution zone) derives from
/// geometry constants plus page order, so pages stay independently editable (R8, R22).
/// </summary>
/// <param name="Index">0-based spread index within the sequence of pages.</param>
/// <param name="Left">The left-hand page, or null when the sequence starts on a right-hand page.</param>
/// <param name="Right">The right-hand page, or null when the sequence ends on a left-hand page.</param>
public readonly record struct SpreadView(int Index, Page? Left, Page? Right)
{
    /// <summary>True when both facing pages exist — the only case where spread-pair templates apply (R22).</summary>
    public bool IsComplete => Left is not null && Right is not null;

    /// <summary>The pages of this spread in reading order, skipping a missing side.</summary>
    public IEnumerable<Page> Pages
    {
        get
        {
            if (Left is not null) yield return Left;
            if (Right is not null) yield return Right;
        }
    }
}
