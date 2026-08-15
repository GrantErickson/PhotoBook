using PhotoBook.Core.Model;

namespace PhotoBook.Core.Persistence;

/// <summary>An entire project as loaded from disk.</summary>
/// <param name="Book">The book document.</param>
/// <param name="Photos">The photo catalog.</param>
/// <param name="Journal">The journal document; empty when no journal was imported.</param>
/// <param name="Chapters">The chapter files present on disk, ordered by month.</param>
public sealed record ProjectSnapshot(
    Book Book,
    PhotoCatalog Photos,
    JournalDocument Journal,
    IReadOnlyList<Chapter> Chapters)
{
    /// <summary>The chapter for a month of the book's year, or null when the month has no pages yet.</summary>
    public Chapter? Chapter(int month) => Chapters.FirstOrDefault(c => c.Month == month);
}
