namespace PhotoBook.Core.Model;

/// <summary>
/// The shipped style defaults — kernel §9 and doc 10 §1/§3: journal text Source Serif 4 at 10.5 pt,
/// captions Source Sans 3 at 8.5 pt, month titles Playfair Display at 64 pt, white text
/// (<c>#FFFFFF</c>) on a solid black page (<c>#000000</c>), image borders off, and the overlay scrim
/// on at 60% maximum opacity.
/// <para>
/// Every property returns a <b>fresh</b> object graph, so callers may mutate the result without
/// disturbing anyone else's defaults.
/// </para>
/// </summary>
public static class BuiltInStyles
{
    /// <summary>The default text color on the v1 black page (kernel §3).</summary>
    public const string DefaultTextColor = "#FFFFFF";

    /// <summary>The v1 page background color (kernel §3, R21).</summary>
    public const string DefaultBackgroundColor = "#000000";

    /// <summary>Bundled OFL family for journal body text.</summary>
    public const string JournalFamily = "Source Serif 4";

    /// <summary>Bundled OFL family for captions.</summary>
    public const string CaptionFamily = "Source Sans 3";

    /// <summary>Bundled OFL display family for month titles.</summary>
    public const string MonthTitleFamily = "Playfair Display";

    /// <summary>Journal text: Source Serif 4, 10.5 pt, white, line height 1.35.</summary>
    public static TextStyle DefaultJournalText => new()
    {
        Family = JournalFamily,
        SizePt = 10.5,
        Color = DefaultTextColor,
        Weight = "regular",
        LineHeight = 1.35,
    };

    /// <summary>Caption text: Source Sans 3, 8.5 pt, white, line height 1.25.</summary>
    public static TextStyle DefaultCaptionText => new()
    {
        Family = CaptionFamily,
        SizePt = 8.5,
        Color = DefaultTextColor,
        Weight = "regular",
        LineHeight = 1.25,
    };

    /// <summary>Month titles: Playfair Display, 64 pt, white, line height 1.0 (R24).</summary>
    public static TextStyle DefaultMonthTitle => new()
    {
        Family = MonthTitleFamily,
        SizePt = 64,
        Color = DefaultTextColor,
        Weight = "regular",
        LineHeight = 1.0,
    };

    /// <summary>Image borders: off, 2 pt white, square corners (R23).</summary>
    public static ImageBorderStyle DefaultImageBorder => new()
    {
        Enabled = false,
        WidthPt = 2.0,
        Color = DefaultTextColor,
        CornerRadiusPt = 0,
    };

    /// <summary>Overlay scrim: on, 60% maximum opacity, 12 pt padding (doc 10 §4).</summary>
    public static OverlayScrimStyle DefaultOverlayScrim => new()
    {
        Enabled = true,
        MaxOpacity = 0.6,
        PaddingPt = 12,
    };

    /// <summary>Background: solid black (R21).</summary>
    public static BackgroundStyle DefaultBackground => new()
    {
        Kind = BackgroundKind.Solid,
        Color = DefaultBackgroundColor,
    };

    /// <summary>
    /// The complete default <see cref="Style"/> — every leaf populated. This is what a new book's
    /// global style starts as, and the base every cascade resolution merges onto.
    /// </summary>
    public static Style Default => new()
    {
        JournalText = DefaultJournalText,
        CaptionText = DefaultCaptionText,
        MonthTitle = DefaultMonthTitle,
        ImageBorder = DefaultImageBorder,
        OverlayScrim = DefaultOverlayScrim,
        Background = DefaultBackground,
    };
}
