using System.Globalization;
using PhotoBook.Core.Model;
using PhotoBook.Core.Persistence;

namespace PhotoBook.Rendering;

/// <summary>Which pages an export emits (doc 12 "Export scope").</summary>
public enum ExportScopeKind
{
    /// <summary>Every chapter in order — one book is one year (R3).</summary>
    Book,

    /// <summary>One month, including its month-title page — chapters stand alone (R4).</summary>
    Chapter,

    /// <summary>An inclusive range of book page numbers, for proofing a section.</summary>
    PageRange,
}

/// <summary>
/// The pages an export covers. Geometry, page numbering and rendering are identical in all three
/// scopes — scope only filters which pages are emitted, so a chapter proof shows the same page
/// numbers it would have in the full book (doc 12).
/// </summary>
/// <param name="Kind">Which scope this is.</param>
/// <param name="ChapterMonth">The month, for <see cref="ExportScopeKind.Chapter"/>.</param>
/// <param name="FirstPage">First book page number, inclusive, for <see cref="ExportScopeKind.PageRange"/>.</param>
/// <param name="LastPage">Last book page number, inclusive.</param>
public sealed record ExportScope(
    ExportScopeKind Kind,
    int? ChapterMonth = null,
    int? FirstPage = null,
    int? LastPage = null)
{
    /// <summary>The default scope: the whole book.</summary>
    public static ExportScope WholeBook { get; } = new(ExportScopeKind.Book);

    /// <summary>One chapter, by month number.</summary>
    public static ExportScope Chapter(int month)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(month, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(month, 12);
        return new ExportScope(ExportScopeKind.Chapter, ChapterMonth: month);
    }

    /// <summary>An inclusive range of book page numbers.</summary>
    public static ExportScope PageRange(int firstPage, int lastPage)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(firstPage, 1);
        if (lastPage < firstPage)
            throw new ArgumentOutOfRangeException(nameof(lastPage), lastPage, "The last page must not precede the first.");
        return new ExportScope(ExportScopeKind.PageRange, FirstPage: firstPage, LastPage: lastPage);
    }

    /// <summary>
    /// True when the profile's page-count rules apply. Partial exports are proofs, not uploads, so
    /// their page counts are never gated (doc 12).
    /// </summary>
    public bool EnforcesPageCount => Kind == ExportScopeKind.Book;

    /// <summary>
    /// The file name doc 12 prescribes: <c>MyBook-2024.pdf</c>, <c>MyBook-2024_2024-06.pdf</c>,
    /// <c>MyBook-2024_p12-p27.pdf</c>.
    /// </summary>
    public string SuggestedFileName(Book book, bool draft = false)
    {
        ArgumentNullException.ThrowIfNull(book);
        var stem = string.IsNullOrWhiteSpace(book.Title)
            ? string.Create(CultureInfo.InvariantCulture, $"PhotoBook-{book.Year}")
            : ProjectPaths.SanitizeFileName(book.Title);

        var suffix = Kind switch
        {
            ExportScopeKind.Chapter => string.Create(CultureInfo.InvariantCulture, $"_{book.Year:0000}-{ChapterMonth ?? 0:00}"),
            ExportScopeKind.PageRange => string.Create(CultureInfo.InvariantCulture, $"_p{FirstPage}-p{LastPage}"),
            _ => string.Empty,
        };

        return $"{stem}{suffix}{(draft ? "_draft" : string.Empty)}.pdf";
    }

    /// <inheritdoc/>
    public override string ToString() => Kind switch
    {
        ExportScopeKind.Chapter => $"Chapter {ChapterMonth:00}",
        ExportScopeKind.PageRange => $"Pages {FirstPage}–{LastPage}",
        _ => "Whole book",
    };
}
