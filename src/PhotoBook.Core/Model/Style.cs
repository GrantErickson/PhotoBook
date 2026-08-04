namespace PhotoBook.Core.Model;

/// <summary>
/// How things look (doc 10). One record type is used at all three cascade levels — global
/// (<see cref="Book.Style"/>), chapter (<see cref="Chapter.StyleOverride"/>) and page
/// (<see cref="Page.StyleOverride"/>) — and overrides are <b>sparse</b>: only non-null leaves
/// override the level above (R23). Resolve a level with
/// <see cref="StyleResolver.Resolve(Book, Chapter?, Page?)"/>.
/// </summary>
public sealed record Style
{
    /// <summary>Journal body text — Source Serif 4, 10.5 pt by default.</summary>
    public TextStyle? JournalText { get; set; }

    /// <summary>Caption text, below-image and overlay — Source Sans 3, 8.5 pt by default. Independent of <see cref="JournalText"/> (R23).</summary>
    public TextStyle? CaptionText { get; set; }

    /// <summary>Month-title display text — Playfair Display, 64 pt by default (R24).</summary>
    public TextStyle? MonthTitle { get; set; }

    /// <summary>Image borders across every photo in scope (R23).</summary>
    public ImageBorderStyle? ImageBorder { get; set; }

    /// <summary>The overlay-caption scrim.</summary>
    public OverlayScrimStyle? OverlayScrim { get; set; }

    /// <summary>The page background (R21).</summary>
    public BackgroundStyle? Background { get; set; }

    /// <summary>True when this style sets nothing at all — the identity override.</summary>
    public bool IsEmpty =>
        JournalText is null && CaptionText is null && MonthTitle is null &&
        ImageBorder is null && OverlayScrim is null && Background is null;
}
