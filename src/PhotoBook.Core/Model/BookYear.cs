using System.Globalization;

namespace PhotoBook.Core.Model;

/// <summary>
/// Everything a book's calendar year owns, captured so a year change is exactly reversible: the year
/// itself, the title (which <see cref="BookYear.MoveTo"/> may rewrite), and the year stamped on each
/// chapter, in chapter order.
/// </summary>
public sealed record BookYearState
{
    /// <summary>The year the book covered.</summary>
    public required int Year { get; init; }

    /// <summary>The title it had, before any year substitution.</summary>
    public required string Title { get; init; }

    /// <summary>Each chapter's year, positionally matched to the chapter list it was captured from.</summary>
    public required IReadOnlyList<int> ChapterYears { get; init; }
}

/// <summary>
/// Moving a book from one calendar year to another (R3, R6).
/// <para>
/// A book covers exactly one year, so the year is not a label: chapters carry it, chapter membership
/// is computed from it (<see cref="PhotoCatalog.InChapter"/>), and every non-excluded photo dated
/// outside it lands in the Outside-book tray rather than on a page. Retargeting a book therefore has
/// to move the chapters with it and say what it stranded — and it has to behave identically whether
/// the move came from an import that discovered the real year or from a user typing one, which is
/// why both paths go through here.
/// </para>
/// </summary>
public static class BookYear
{
    /// <summary>Captures the state a later <see cref="Restore"/> would put back.</summary>
    public static BookYearState Capture(Book book, IReadOnlyList<Chapter> chapters)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(chapters);

        return new BookYearState
        {
            Year = book.Year,
            Title = book.Title,
            ChapterYears = [.. chapters.Select(c => c.Year)],
        };
    }

    /// <summary>
    /// Puts a captured year state back — the undo half of <see cref="MoveTo"/>. Chapters are matched
    /// positionally against the list the state was captured from; extra chapters keep what they have.
    /// </summary>
    public static void Restore(Book book, IReadOnlyList<Chapter> chapters, BookYearState state)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(chapters);
        ArgumentNullException.ThrowIfNull(state);

        book.Year = state.Year;
        book.Title = state.Title;

        var count = Math.Min(chapters.Count, state.ChapterYears.Count);
        for (var i = 0; i < count; i++)
        {
            chapters[i].Year = state.ChapterYears[i];
        }
    }

    /// <summary>
    /// Retargets a book at another year, moving its chapters with it and rewriting a default title
    /// like "Family 2026" so nothing keeps showing the year the book no longer covers. Photos are not
    /// touched: a photo dated outside the new year simply falls into the Outside-book tray, which is
    /// reversible by re-dating it or by moving the book back (R6, R17 — nothing is ever deleted).
    /// </summary>
    public static void MoveTo(Book book, IEnumerable<Chapter> chapters, int year)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(chapters);

        var previous = book.Year.ToString(CultureInfo.InvariantCulture);
        book.Year = year;

        foreach (var chapter in chapters)
        {
            chapter.Year = year;
        }

        if (book.Title.Contains(previous, StringComparison.Ordinal))
        {
            book.Title = book.Title.Replace(
                previous, year.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }
    }

    /// <summary>How many non-excluded photos would sit in the Outside-book tray if the book covered this year.</summary>
    public static int PhotosOutside(PhotoCatalog catalog, int year)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.OutsideBookTray(year).Count();
    }

    /// <summary>
    /// The sentence worth showing about photos a given year strands, or null when it strands none.
    /// One wording, used both after an import and before a deliberate year change, so the two can
    /// never disagree about what the Outside-book tray means.
    /// </summary>
    public static string? OutsideNote(PhotoCatalog catalog, int year)
    {
        var strays = PhotosOutside(catalog, year);
        return strays == 0
            ? null
            : $"{strays} photo{(strays == 1 ? " is" : "s are")} outside {year} and will not appear " +
              $"in any month. Change the photo's date, or make a book for that year.";
    }
}
