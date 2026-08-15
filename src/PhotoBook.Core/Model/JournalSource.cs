namespace PhotoBook.Core.Model;

/// <summary>The Word document a journal was parsed from (doc 11).</summary>
public sealed record JournalSource
{
    /// <summary>The <c>.docx</c> file name as imported.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>SHA-256 of the document bytes at import time.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>When the document was imported, UTC.</summary>
    public DateTime ImportedAtUtc { get; set; }
}
