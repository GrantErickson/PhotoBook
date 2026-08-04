namespace PhotoBook.Ingestion.Journal;

/// <summary>
/// One paragraph of the flattened Word document (doc 11, "OpenXML parsing: the paragraph stream").
/// Everything downstream — date matching, segmentation, entry identity — operates on this stream and
/// never on raw OpenXML, which is what keeps the parser testable without binary fixtures.
/// </summary>
public sealed record ParagraphRecord
{
    /// <summary>0-based position in the flattened body; empty paragraphs do not take a slot.</summary>
    public int Index { get; init; }

    /// <summary>All runs concatenated, whitespace collapsed to single spaces, trimmed.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary><c>w:pStyle</c> value, e.g. <c>Heading1</c>; null when the paragraph uses the default style.</summary>
    public string? StyleId { get; init; }

    /// <summary>Outline level from the paragraph or its resolved style, if any.</summary>
    public int? OutlineLevel { get; init; }

    /// <summary>True when every non-whitespace run is bold.</summary>
    public bool IsBoldOnly { get; init; }

    /// <summary>True when the text is uppercase, or every run carries <c>w:caps</c>.</summary>
    public bool IsAllCaps { get; init; }

    /// <summary>True when the paragraph has numbering properties (<c>w:numPr</c>).</summary>
    public bool IsListItem { get; init; }

    /// <summary>
    /// How many empty paragraphs preceded this one. Empty paragraphs are dropped from the stream but
    /// counted, so an entry's blank-line structure is not silently lost (doc 11).
    /// </summary>
    public int PrecedingBreaks { get; init; }

    /// <summary>
    /// True when the paragraph is structurally a heading: a <c>Heading1</c>–<c>Heading6</c> style or an
    /// outline level of 2 or less. These get first crack at date matching and, when they yield no date,
    /// are surfaced in the Import Report as unmatched headings rather than swallowed.
    /// </summary>
    public bool IsStrongHeading { get; init; }

    /// <summary>
    /// True when the paragraph is a <em>heading candidate</em> per doc 11: a strong heading, or short
    /// bold/all-caps text that does not end in <c>.</c>, <c>!</c> or <c>?</c>.
    /// </summary>
    public bool IsHeadingCandidate { get; init; }

    /// <summary>True when the paragraph carries no visible text.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);
}
