namespace PhotoBook.Core.Model;

/// <summary>The counts and leftovers the Import Report shows after a journal import (doc 11).</summary>
public sealed record JournalImportReport
{
    /// <summary>Entries whose date matched with confidence 0.70 or better.</summary>
    public int Matched { get; set; }

    /// <summary>Entries provisionally dated at confidence 0.40–0.69.</summary>
    public int Ambiguous { get; set; }

    /// <summary>Entries with no usable date, including the preamble bucket and unmatched headings.</summary>
    public int Unmatched { get; set; }

    /// <summary>Entries whose resolved date fell outside the book year (R3).</summary>
    public int OutOfYear { get; set; }

    /// <summary>User dates and exclusions that no longer match any entry.</summary>
    public IList<OrphanedUserAssignment> OrphanedUserAssignments { get; set; } = new List<OrphanedUserAssignment>();

    /// <summary>True when the report has anything worth showing the user.</summary>
    public bool HasFindings =>
        Ambiguous > 0 || Unmatched > 0 || OutOfYear > 0 || OrphanedUserAssignments.Count > 0;
}
