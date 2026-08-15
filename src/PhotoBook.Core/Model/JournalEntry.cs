namespace PhotoBook.Core.Model;

/// <summary>
/// One dated entry parsed from the Word journal (R2, doc 11), serialized in <c>journal.json</c>.
/// A day's journal text is <b>atomic</b>: it renders together, may span the two pages of one Spread,
/// and never crosses Spreads (kernel §9).
/// <para>
/// Identity is content-derived so it survives reordering and unrelated edits: <see cref="Id"/> is
/// <c>"je-"</c> plus the first 12 hex characters of the SHA-256 of
/// <c>sourceKey + "#" + occurrence</c>. The document owns the text; the user owns the dates.
/// </para>
/// </summary>
public sealed record JournalEntry
{
    /// <summary><c>"je-" + sha256(sourceKey#occurrence)[..12]</c>; stable across re-imports.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Normalized heading plus first-paragraph prefix — the content key behind <see cref="Id"/>.</summary>
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>Disambiguates identical source keys within one document, 0-based.</summary>
    public int Occurrence { get; set; }

    /// <summary>First day covered by the entry.</summary>
    public DateOnly DateStart { get; set; }

    /// <summary>Last day covered; equals <see cref="DateStart"/> for a single-day entry.</summary>
    public DateOnly DateEnd { get; set; }

    /// <summary>Confidence bucket of the date match.</summary>
    public JournalEntryStatus Status { get; set; } = JournalEntryStatus.Unmatched;

    /// <summary>Name of the matcher that fired, camelCase (e.g. <c>explicitHeading</c>); null when unmatched.</summary>
    public string? MatchedBy { get; set; }

    /// <summary>Final match confidence, <c>0..1</c>.</summary>
    public double Confidence { get; set; }

    /// <summary>Other defensible date resolutions, offered as one-click chips in the Import Report.</summary>
    public IList<DateOnly> Alternates { get; set; } = new List<DateOnly>();

    /// <summary>The heading the date came from; null for inline matches.</summary>
    public string? HeadingText { get; set; }

    /// <summary>The entry body as plain-text paragraphs; formatting is normalized on import.</summary>
    public IList<string> Paragraphs { get; set; } = new List<string>();

    /// <summary>The user's manual date assignment; when set it wins over <see cref="DateStart"/>.</summary>
    public DateOnly? UserDate { get; set; }

    /// <summary>True when the user removed this entry from the book; treated exactly like "no entry".</summary>
    public bool Excluded { get; set; }

    /// <summary>The date used everywhere downstream: <see cref="UserDate"/> ?? <see cref="DateStart"/>.</summary>
    public DateOnly EffectiveDate => UserDate ?? DateStart;

    /// <summary>True when the entry spans more than one day.</summary>
    public bool IsRange => DateEnd > DateStart;

    /// <summary>Total characters of body text — the text-demand input of the layout engine (doc 08 §4).</summary>
    public int CharacterCount => Paragraphs.Sum(p => p?.Length ?? 0);
}
