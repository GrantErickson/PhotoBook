namespace PhotoBook.Core.Model;

/// <summary>
/// Resolves the style cascade <c>global → chapter → page</c> (doc 10 §2, R23).
/// <para>
/// Resolution is a real <b>field-level deep merge</b>: the most specific level that sets a leaf wins
/// and unset leaves fall through, so <c>{"captionText": {"sizePt": 9.5}}</c> at chapter level
/// changes caption size for that chapter only — family, color and everything else still come from
/// the book. Removing an override restores inheritance; there is no copied-down snapshot to drift.
/// </para>
/// </summary>
public static class StyleResolver
{
    /// <summary>
    /// The effective style for a page: <see cref="BuiltInStyles.Default"/> ← book global ←
    /// chapter override ← page override. Every leaf of the result is non-null, because the shipped
    /// defaults are the base of the chain.
    /// </summary>
    /// <param name="book">The book, whose <see cref="Book.Style"/> is the global level.</param>
    /// <param name="chapter">The chapter, or null to resolve the book level only.</param>
    /// <param name="page">The page, or null to resolve down to the chapter level.</param>
    public static Style Resolve(Book book, Chapter? chapter = null, Page? page = null)
    {
        ArgumentNullException.ThrowIfNull(book);
        return Merge(BuiltInStyles.Default, book.Style, chapter?.StyleOverride, page?.StyleOverride);
    }

    /// <summary>
    /// The effective style for a chapter (no page level) — what a chapter-scoped UI preview shows.
    /// </summary>
    public static Style Resolve(Book book, Chapter? chapter) => Resolve(book, chapter, null);

    /// <summary>
    /// Merges sparse styles left to right, later levels overriding earlier ones leaf by leaf. Nulls
    /// are skipped. The inputs are never mutated; the result is a fresh object graph.
    /// </summary>
    public static Style Merge(params Style?[] levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        var result = new Style();
        foreach (var level in levels)
        {
            if (level is null) continue;
            result.JournalText = MergeText(result.JournalText, level.JournalText);
            result.CaptionText = MergeText(result.CaptionText, level.CaptionText);
            result.MonthTitle = MergeText(result.MonthTitle, level.MonthTitle);
            result.ImageBorder = MergeBorder(result.ImageBorder, level.ImageBorder);
            result.OverlayScrim = MergeScrim(result.OverlayScrim, level.OverlayScrim);
            result.Background = MergeBackground(result.Background, level.Background);
        }

        return result;
    }

    /// <summary>Merges one text block, leaf by leaf.</summary>
    public static TextStyle? MergeText(TextStyle? baseStyle, TextStyle? overrideStyle)
    {
        if (overrideStyle is null) return baseStyle is null ? null : baseStyle with { };
        if (baseStyle is null) return overrideStyle with { };
        return new TextStyle
        {
            Family = overrideStyle.Family ?? baseStyle.Family,
            SizePt = overrideStyle.SizePt ?? baseStyle.SizePt,
            Color = overrideStyle.Color ?? baseStyle.Color,
            Weight = overrideStyle.Weight ?? baseStyle.Weight,
            LineHeight = overrideStyle.LineHeight ?? baseStyle.LineHeight,
        };
    }

    /// <summary>Merges the image-border block, leaf by leaf.</summary>
    public static ImageBorderStyle? MergeBorder(ImageBorderStyle? baseStyle, ImageBorderStyle? overrideStyle)
    {
        if (overrideStyle is null) return baseStyle is null ? null : baseStyle with { };
        if (baseStyle is null) return overrideStyle with { };
        return new ImageBorderStyle
        {
            Enabled = overrideStyle.Enabled ?? baseStyle.Enabled,
            WidthPt = overrideStyle.WidthPt ?? baseStyle.WidthPt,
            Color = overrideStyle.Color ?? baseStyle.Color,
            CornerRadiusPt = overrideStyle.CornerRadiusPt ?? baseStyle.CornerRadiusPt,
        };
    }

    /// <summary>Merges the overlay-scrim block, leaf by leaf.</summary>
    public static OverlayScrimStyle? MergeScrim(OverlayScrimStyle? baseStyle, OverlayScrimStyle? overrideStyle)
    {
        if (overrideStyle is null) return baseStyle is null ? null : baseStyle with { };
        if (baseStyle is null) return overrideStyle with { };
        return new OverlayScrimStyle
        {
            Enabled = overrideStyle.Enabled ?? baseStyle.Enabled,
            MaxOpacity = overrideStyle.MaxOpacity ?? baseStyle.MaxOpacity,
            PaddingPt = overrideStyle.PaddingPt ?? baseStyle.PaddingPt,
        };
    }

    /// <summary>Merges the background block, leaf by leaf.</summary>
    public static BackgroundStyle? MergeBackground(BackgroundStyle? baseStyle, BackgroundStyle? overrideStyle)
    {
        if (overrideStyle is null) return baseStyle is null ? null : baseStyle with { };
        if (baseStyle is null) return overrideStyle with { };
        return new BackgroundStyle
        {
            Kind = overrideStyle.Kind ?? baseStyle.Kind,
            Color = overrideStyle.Color ?? baseStyle.Color,
        };
    }

    /// <summary>
    /// Which cascade level supplies a given leaf — the source of the UI's inheritance chip
    /// (<em>Book</em>, <em>Chapter</em> or <em>Page</em>, doc 10 §2).
    /// </summary>
    /// <param name="selector">Picks the leaf's owning block out of a style, e.g. <c>s =&gt; s.CaptionText?.SizePt</c>.</param>
    /// <param name="book">The book.</param>
    /// <param name="chapter">The chapter, if any.</param>
    /// <param name="page">The page, if any.</param>
    public static StyleLevel LevelOf(Func<Style, object?> selector, Book book, Chapter? chapter = null, Page? page = null)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(book);
        if (page?.StyleOverride is { } p && selector(p) is not null) return StyleLevel.Page;
        if (chapter?.StyleOverride is { } c && selector(c) is not null) return StyleLevel.Chapter;
        if (book.Style is { } b && selector(b) is not null) return StyleLevel.Book;
        return StyleLevel.Default;
    }
}
