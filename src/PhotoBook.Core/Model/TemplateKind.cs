namespace PhotoBook.Core.Model;

/// <summary>What a template is for; drives engine eligibility (kernel §6, doc 07).</summary>
public enum TemplateKind
{
    /// <summary>An ordinary photo page.</summary>
    Standard,

    /// <summary>The page that opens a Chapter with the month's name (R24).</summary>
    MonthTitle,

    /// <summary>A single photo running to the bleed box, usually with an overlay caption (R18, R5).</summary>
    FullBleed,

    /// <summary>Several sparse days sharing one page, each day kept together in a section (R28).</summary>
    MultiDay,

    /// <summary>One side of a matched left/right pair (R22).</summary>
    SpreadPair,
}
