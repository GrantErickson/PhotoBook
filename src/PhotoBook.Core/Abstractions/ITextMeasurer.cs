using PhotoBook.Core.Model;

namespace PhotoBook.Core.Abstractions;

/// <summary>
/// The measurement contract injected into the layout engine (doc 11, "Atomic text policy and the
/// engine hook"). Text fit is a <b>hard filter</b> in template scoring: there is no auto font-shrink
/// anywhere, so a template whose journal slot chain cannot hold a day's entry at the current
/// <see cref="Style"/> sizes is rejected before any soft scoring happens (kernel §9).
/// <para>
/// Implemented in <c>PhotoBook.Rendering</c> on the very same SkiaSharp shaping code that draws the
/// page, so measurement and rendering can never disagree — WYSIWYG by construction. It lives in
/// Core because the engine consumes it and the renderer implements it, and those two projects never
/// reference each other (kernel §12).
/// </para>
/// <para>
/// Contract rules: shaping uses the three bundled OFL families only, with no system font fallback,
/// so the same inputs measure identically on any machine; the method is pure, thread-safe and free
/// of I/O beyond the typefaces loaded once at startup.
/// </para>
/// </summary>
public interface ITextMeasurer
{
    /// <summary>
    /// The height, in inches, that the paragraphs occupy when laid out at
    /// <paramref name="widthIn"/> inches wide in the given style.
    /// </summary>
    /// <param name="paragraphs">The entry's plain-text paragraphs, in order.</param>
    /// <param name="style">The resolved text style — family, size and line height (doc 10).</param>
    /// <param name="widthIn">The available column width in inches.</param>
    /// <returns>The required height in inches.</returns>
    double MeasureHeightIn(IReadOnlyList<string> paragraphs, TextStyle style, double widthIn);
}
