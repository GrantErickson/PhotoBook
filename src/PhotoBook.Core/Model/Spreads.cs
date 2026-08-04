namespace PhotoBook.Core.Model;

/// <summary>
/// Pairs pages into <see cref="SpreadView"/>s. A spread is a view, not a stored entity: pages
/// <c>2k</c> and <c>2k+1</c> face each other (doc 03 §9), so this is the only place page order is
/// turned into facing pairs.
/// </summary>
public static class Spreads
{
    /// <summary>The side a page of the given 0-based index falls on: even = left, odd = right (doc 07).</summary>
    public static PageSide SideOf(int pageIndex) => pageIndex % 2 == 0 ? PageSide.Left : PageSide.Right;

    /// <summary>True when a page of the given 0-based index is a left-hand page, where mirrorable templates mirror.</summary>
    public static bool IsLeftPage(int pageIndex) => SideOf(pageIndex) == PageSide.Left;

    /// <summary>The 0-based spread index a page of the given 0-based page index belongs to.</summary>
    public static int SpreadIndexOf(int pageIndex) => pageIndex / 2;

    /// <summary>
    /// Pairs the pages by index into facing spreads. The final spread has a null
    /// <see cref="SpreadView.Right"/> when the page count is odd.
    /// </summary>
    public static IReadOnlyList<SpreadView> From(IReadOnlyList<Page> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var spreads = new List<SpreadView>((pages.Count + 1) / 2);
        for (var i = 0; i < pages.Count; i += 2)
        {
            spreads.Add(new SpreadView(i / 2, pages[i], i + 1 < pages.Count ? pages[i + 1] : null));
        }

        return spreads;
    }

    /// <summary>Pairs a chapter's pages into facing spreads.</summary>
    public static IReadOnlyList<SpreadView> From(Chapter chapter)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        return From(chapter.Pages as IReadOnlyList<Page> ?? chapter.Pages.ToList());
    }

    /// <summary>The spread containing the page at <paramref name="pageIndex"/>, or null when out of range.</summary>
    public static SpreadView? ContainingPage(IReadOnlyList<Page> pages, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pageIndex < 0 || pageIndex >= pages.Count) return null;
        var start = pageIndex - pageIndex % 2;
        return new SpreadView(start / 2, pages[start], start + 1 < pages.Count ? pages[start + 1] : null);
    }
}
