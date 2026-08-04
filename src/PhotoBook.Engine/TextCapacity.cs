using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;

namespace PhotoBook.Engine;

/// <summary>
/// The text-fit arithmetic of doc 08 §6 and doc 11: <c>capacity(slot) = slotArea_in² ×
/// 60 chars/in²</c> at the default journal style, generalized so a Style change recomputes it
/// instead of invalidating it. Capacity feeds the <c>S_text</c> soft score and the
/// <c>CHARS_PER_FULL_PAGE</c> demand constant; the <b>hard</b> fit filter goes through
/// <see cref="ITextMeasurer"/> so it agrees with the renderer (kernel §9 — never auto-shrink type).
/// </summary>
public static class TextCapacity
{
    /// <summary>Characters of journal body text per square inch at the shipped default style.</summary>
    public const double BaselineCharsPerSquareInch = 60.0;

    /// <summary>The journal size the baseline density was measured at, in points (doc 10).</summary>
    public const double BaselineSizePt = 10.5;

    /// <summary>The journal line height the baseline density was measured at (doc 10).</summary>
    public const double BaselineLineHeight = 1.35;

    /// <summary>
    /// Character density at a given style. Characters per line scale as <c>1/size</c> and lines per
    /// inch as <c>1/(size × lineHeight)</c>, so density scales as <c>1/(size² × lineHeight)</c>.
    /// </summary>
    public static double CharsPerSquareInch(TextStyle? journal)
    {
        var sizePt = journal?.SizePt is > 0 ? journal.SizePt!.Value : BaselineSizePt;
        var lineHeight = journal?.LineHeight is > 0 ? journal.LineHeight!.Value : BaselineLineHeight;
        var sizeRatio = BaselineSizePt / sizePt;
        return BaselineCharsPerSquareInch * sizeRatio * sizeRatio * (BaselineLineHeight / lineHeight);
    }

    /// <summary>
    /// The doc 08 §4 <c>CHARS_PER_FULL_PAGE</c> recomputed from the current Style and page size —
    /// the safe area (kernel §3) times <see cref="CharsPerSquareInch"/>. ≈ 4766 at the shipped
    /// defaults on an 11 × 8.5 in page, which is where the doc's 4800 comes from.
    /// </summary>
    public static double CharsPerFullPage(Style style, double trimWidthIn, double trimHeightIn)
    {
        ArgumentNullException.ThrowIfNull(style);
        var safeW = trimWidthIn - 2 * PageGeometry.SafeMarginIn;
        var safeH = trimHeightIn - 2 * PageGeometry.SafeMarginIn;
        return Math.Max(1.0, safeW * safeH) * CharsPerSquareInch(style.JournalText);
    }

    /// <summary>How many characters a chain of journal text slots holds at the given style.</summary>
    /// <param name="chain">The journal-role text slots, in flow order.</param>
    /// <param name="style">The resolved style supplying journal metrics.</param>
    /// <param name="trimWidthIn">Page trim width in inches.</param>
    /// <param name="trimHeightIn">Page trim height in inches.</param>
    public static double CapacityChars(
        IReadOnlyList<TextSlot> chain, Style style, double trimWidthIn, double trimHeightIn)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(style);

        var density = CharsPerSquareInch(style.JournalText);
        var total = 0.0;
        foreach (var slot in chain)
        {
            var (w, h) = PageGeometry.PhysicalSize(slot.Rect, trimWidthIn, trimHeightIn);
            total += Math.Max(0, w) * Math.Max(0, h) * density;
        }

        return total;
    }

    /// <summary>The total height, in inches, a chain of text slots offers.</summary>
    public static double AvailableHeightIn(IReadOnlyList<TextSlot> chain, double trimHeightIn)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var total = 0.0;
        foreach (var slot in chain) total += Math.Max(0, slot.Rect.H) * trimHeightIn;
        return total;
    }

    /// <summary>The column width, in inches, text flows at — the first slot of the chain (doc 11).</summary>
    public static double ColumnWidthIn(IReadOnlyList<TextSlot> chain, double trimWidthIn)
    {
        ArgumentNullException.ThrowIfNull(chain);
        return chain.Count == 0 ? 0 : Math.Max(0.01, chain[0].Rect.W * trimWidthIn);
    }
}

/// <summary>
/// The headless <see cref="ITextMeasurer"/> the engine falls back to when the app does not inject
/// the SkiaSharp one from <c>PhotoBook.Rendering</c> (doc 11). It is calibrated so that at the
/// shipped journal style it reproduces doc 08's <c>60 chars/in²</c> capacity <em>exactly</em>,
/// which keeps the hard fit filter and the <c>S_text</c> soft score consistent, and it is pure,
/// allocation-light, culture-free and deterministic — so golden layout tests run with no fonts
/// installed (doc 13).
/// <para>
/// With <see cref="RoundToWholeLines"/> it also charges each paragraph a whole number of lines,
/// which is what real shaping does; that is <b>off</b> by default so the headless engine reproduces
/// doc 08's <c>capacity = slotArea_in² × 60</c> exactly, including its worked examples.
/// </para>
/// </summary>
public sealed class DefaultTextMeasurer : ITextMeasurer
{
    /// <summary>Creates a measurer.</summary>
    /// <param name="roundToWholeLines">
    /// When true, every paragraph occupies a whole number of lines — closer to real shaping, and
    /// slightly more conservative than doc 08's area formula.
    /// </param>
    public DefaultTextMeasurer(bool roundToWholeLines = false) => RoundToWholeLines = roundToWholeLines;

    /// <summary>The shared instance; the measurer holds no state.</summary>
    public static DefaultTextMeasurer Instance { get; } = new();

    /// <summary>Whether each paragraph is charged a whole number of lines.</summary>
    public bool RoundToWholeLines { get; }

    /// <summary>
    /// Mean character advance as a fraction of the em, derived from the baseline density so the two
    /// stay in lockstep: <c>1 / (charsPerIn² × sizeIn × lineHeightIn)</c> ≈ 0.58 em for Source
    /// Serif 4 at 10.5 pt / 1.35.
    /// </summary>
    public static double AverageCharWidthEm =>
        1.0 / (TextCapacity.BaselineCharsPerSquareInch
               * (TextCapacity.BaselineSizePt / PageGeometry.PointsPerInch)
               * (TextCapacity.BaselineSizePt / PageGeometry.PointsPerInch)
               * TextCapacity.BaselineLineHeight);

    /// <inheritdoc/>
    public double MeasureHeightIn(IReadOnlyList<string> paragraphs, TextStyle style, double widthIn)
    {
        ArgumentNullException.ThrowIfNull(paragraphs);
        if (!(widthIn > 0) || !double.IsFinite(widthIn)) return double.PositiveInfinity;

        var sizePt = style?.SizePt is > 0 ? style.SizePt!.Value : TextCapacity.BaselineSizePt;
        var lineHeight = style?.LineHeight is > 0 ? style.LineHeight!.Value : TextCapacity.BaselineLineHeight;

        var sizeIn = sizePt / PageGeometry.PointsPerInch;
        var lineHeightIn = sizeIn * lineHeight;
        var charWidthIn = sizeIn * AverageCharWidthEm;
        var charsPerLineExact = Math.Max(1.0, widthIn / charWidthIn);
        var charsPerLine = Math.Max(1, (int)Math.Floor(charsPerLineExact));

        var lines = 0.0;
        foreach (var paragraph in paragraphs)
        {
            var length = paragraph?.Length ?? 0;
            if (RoundToWholeLines)
            {
                lines += length <= 0 ? 1 : (length + charsPerLine - 1) / charsPerLine;
            }
            else
            {
                lines += length <= 0 ? 0 : length / charsPerLineExact;
            }
        }

        return lines * lineHeightIn;
    }
}
