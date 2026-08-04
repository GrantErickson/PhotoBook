using PhotoBook.Core.Model;
using PhotoBook.Core.Persistence;

namespace PhotoBook.Rendering;

/// <summary>
/// One page of the book, carrying the book-wide page number that every scope keeps stable (doc 12
/// "Export scope": a chapter or range export shows the same numbers it would have in the full book).
/// </summary>
/// <param name="PageNumber">The book-wide 1-based page number.</param>
/// <param name="Chapter">The chapter the page belongs to.</param>
/// <param name="Page">The page itself.</param>
/// <param name="IndexInChapter">The page's 0-based index within its chapter.</param>
public sealed record BookPage(int PageNumber, Chapter Chapter, Page Page, int IndexInChapter);

/// <summary>
/// One sheet of the PDF: a single page, or the two facing pages of a spread when the profile's
/// output mode is <see cref="PdfOutputMode.Spreads"/>.
/// </summary>
/// <param name="Left">The left page, or the only page on a single-page sheet.</param>
/// <param name="Right">The right page of a spread sheet; null for a single-page sheet.</param>
/// <param name="Surface">Whether the sheet is a page or a panorama.</param>
public sealed record ExportSheet(BookPage Left, BookPage? Right, PageSurface Surface)
{
    /// <summary>The pages on this sheet, in reading order.</summary>
    public IEnumerable<BookPage> Pages
    {
        get
        {
            yield return Left;
            if (Right is not null) yield return Right;
        }
    }
}

/// <summary>
/// Turns a project into the ordered page sequence an export walks, and slices that sequence by
/// <see cref="ExportScope"/> and by the profile's output mode.
/// <para>
/// Order is deterministic and total — chapters ascending by month, pages in stored order — which is
/// one of the pillars of doc 12's byte-stable output guarantee.
/// </para>
/// </summary>
public static class BookPagination
{
    /// <summary>Every page of the book, numbered from 1.</summary>
    public static IReadOnlyList<BookPage> Paginate(ProjectSnapshot project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Paginate(project.Chapters);
    }

    /// <inheritdoc cref="Paginate(ProjectSnapshot)"/>
    public static IReadOnlyList<BookPage> Paginate(IEnumerable<Chapter> chapters)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        var pages = new List<BookPage>();
        var number = 1;
        foreach (var chapter in chapters.OrderBy(c => c.Year).ThenBy(c => c.Month))
        {
            for (var i = 0; i < chapter.Pages.Count; i++)
            {
                pages.Add(new BookPage(number++, chapter, chapter.Pages[i], i));
            }
        }

        return pages;
    }

    /// <summary>The pages a scope selects, in book order.</summary>
    public static IReadOnlyList<BookPage> InScope(IReadOnlyList<BookPage> allPages, ExportScope scope)
    {
        ArgumentNullException.ThrowIfNull(allPages);
        ArgumentNullException.ThrowIfNull(scope);

        return scope.Kind switch
        {
            ExportScopeKind.Chapter => [.. allPages.Where(p => p.Chapter.Month == scope.ChapterMonth)],
            ExportScopeKind.PageRange => [.. allPages.Where(p => p.PageNumber >= scope.FirstPage && p.PageNumber <= scope.LastPage)],
            _ => allPages,
        };
    }

    /// <summary>
    /// Groups in-scope pages into the sheets the PDF emits.
    /// <para>
    /// In <see cref="PdfOutputMode.SinglePages"/> every page is its own sheet, recto/verso in reading
    /// order, and the print service imposes them. In <see cref="PdfOutputMode.Spreads"/> pages pair
    /// (2,3), (4,5), … into panoramas; page 1 — the book's opening recto — and a trailing lone page
    /// export as single pages (doc 12 "Spread output mode"). A page range is snapped outward to
    /// spread boundaries, which is why this method takes the full page list as well as the scope.
    /// </para>
    /// </summary>
    /// <param name="allPages">Every page of the book, from <see cref="Paginate(ProjectSnapshot)"/>.</param>
    /// <param name="scope">The requested scope.</param>
    /// <param name="output">The profile's output mode.</param>
    public static IReadOnlyList<ExportSheet> Sheets(
        IReadOnlyList<BookPage> allPages, ExportScope scope, PdfOutputMode output)
    {
        ArgumentNullException.ThrowIfNull(allPages);
        ArgumentNullException.ThrowIfNull(scope);

        var selected = InScope(allPages, scope);
        if (selected.Count == 0) return [];

        if (output == PdfOutputMode.SinglePages)
            return [.. selected.Select(p => new ExportSheet(p, null, PageSurface.SinglePage))];

        // Snap outward to spread boundaries: a spread is (even, even+1) in book page numbers.
        var first = selected[0].PageNumber;
        var last = selected[^1].PageNumber;
        if (first > 1 && first % 2 != 0) first--;
        if (last > 1 && last % 2 == 0) last++;

        var byNumber = allPages.ToDictionary(p => p.PageNumber);
        var sheets = new List<ExportSheet>();
        var n = first;
        while (n <= last)
        {
            if (!byNumber.TryGetValue(n, out var page))
            {
                n++;
                continue;
            }

            if (n == 1)
            {
                // The opening recto has no facing page.
                sheets.Add(new ExportSheet(page, null, PageSurface.SinglePage));
                n++;
                continue;
            }

            if (byNumber.TryGetValue(n + 1, out var facing) && n + 1 <= last)
            {
                sheets.Add(new ExportSheet(page, facing, PageSurface.Spread));
                n += 2;
            }
            else
            {
                // A trailing lone page exports single.
                sheets.Add(new ExportSheet(page, null, PageSurface.SinglePage));
                n++;
            }
        }

        return sheets;
    }
}
