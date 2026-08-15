using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;

namespace PhotoBook.Rendering;

/// <summary>
/// The <see cref="ITextMeasurer"/> the layout engine injects (doc 11 "Atomic text policy and the
/// engine hook"), implemented on the very same <see cref="TextLayout"/> shaping the page draw uses —
/// so a template that measures as fitting really does fit when it is drawn.
/// <para>
/// Text fit is a hard filter in template scoring and there is no auto font-shrink anywhere
/// (kernel §9): this type answers "how tall is this entry in this column", nothing more.
/// </para>
/// <para>
/// Measurement runs at 1 device unit = 1 point, so a style's point sizes are used literally and the
/// result converts to inches by a single division. It is pure and thread-safe: the only shared state
/// is the <see cref="FontLibrary"/>'s typeface cache.
/// </para>
/// </summary>
public sealed class SkiaTextMeasurer : ITextMeasurer
{
    private readonly FontLibrary _fonts;
    private readonly TextRole _role;

    /// <summary>Creates a measurer over the given font library.</summary>
    /// <param name="fonts">The font library; defaults to <see cref="FontLibrary.Default"/>.</param>
    /// <param name="role">The role whose fallback chain applies; journal text by default.</param>
    public SkiaTextMeasurer(FontLibrary? fonts = null, TextRole role = TextRole.Journal)
    {
        _fonts = fonts ?? FontLibrary.Default;
        _role = role;
    }

    /// <summary>The font library this measurer resolves families through.</summary>
    public FontLibrary Fonts => _fonts;

    /// <inheritdoc/>
    public double MeasureHeightIn(IReadOnlyList<string> paragraphs, TextStyle style, double widthIn)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (paragraphs is null || paragraphs.Count == 0 || !(widthIn > 0)) return 0;

        var layout = Measure(paragraphs, style, widthIn);
        return layout.Height / PageGeometry.PointsPerInch;
    }

    /// <summary>
    /// The full layout behind <see cref="MeasureHeightIn"/> — line count included, for callers that
    /// want to know how a block broke and not just how tall it is.
    /// </summary>
    /// <param name="paragraphs">The paragraphs to measure.</param>
    /// <param name="style">The resolved text style.</param>
    /// <param name="widthIn">Column width in inches.</param>
    /// <param name="heightIn">Column height in inches, or <c>null</c> for unbounded.</param>
    /// <param name="maxLines">Optional hard line cap.</param>
    public TextBlockLayout Measure(
        IReadOnlyList<string> paragraphs,
        TextStyle style,
        double widthIn,
        double? heightIn = null,
        int maxLines = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (paragraphs is null || paragraphs.Count == 0 || !(widthIn > 0)) return TextBlockLayout.Empty;

        var defaultSize = _role switch
        {
            TextRole.Caption => 8.5,
            TextRole.MonthTitle => 64,
            _ => 10.5,
        };

        using var font = _fonts.CreateFont(style, _role, 1.0, defaultSize);
        var maxHeight = heightIn is > 0
            ? (float)(heightIn.Value * PageGeometry.PointsPerInch)
            : float.PositiveInfinity;
        return TextLayout.Layout(
            paragraphs,
            font,
            style.LineHeight ?? DefaultLineHeight(_role),
            (float)(widthIn * PageGeometry.PointsPerInch),
            maxHeight,
            maxLines);
    }

    /// <summary>True when the paragraphs fit the given column at the style's size — doc 11's hard filter.</summary>
    public bool Fits(IReadOnlyList<string> paragraphs, TextStyle style, double widthIn, double heightIn) =>
        !Measure(paragraphs, style, widthIn, heightIn).Overflowed;

    internal static double DefaultLineHeight(TextRole role) => role switch
    {
        TextRole.Caption => 1.25,
        TextRole.MonthTitle => 1.0,
        _ => 1.35,
    };
}
