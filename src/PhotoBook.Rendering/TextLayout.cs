using SkiaSharp;

namespace PhotoBook.Rendering;

/// <summary>One laid-out line: the text, its measured width and its baseline offset from the block top.</summary>
/// <param name="Text">The line's text, trailing whitespace already trimmed.</param>
/// <param name="Width">Measured advance width in device units.</param>
/// <param name="BaselineY">Baseline offset from the top of the text block, in device units.</param>
/// <param name="ParagraphIndex">Which source paragraph the line came from.</param>
public readonly record struct TextLine(string Text, float Width, float BaselineY, int ParagraphIndex);

/// <summary>
/// The result of laying out a paragraph list into a column: the lines that fit, the height they
/// occupy, and — crucially — whether anything did <b>not</b> fit.
/// <para>
/// There is no auto font-shrink anywhere in this app (kernel §9, doc 10 §1, doc 11). When text
/// exceeds its box the layout reports <see cref="Overflowed"/> and the renderer draws what fits,
/// clipped; preflight then raises a text-overflow error. Type is never squeezed to force a fit.
/// </para>
/// </summary>
/// <param name="Lines">The lines that fit, in order.</param>
/// <param name="LineSpacing">Distance between consecutive baselines, in device units.</param>
/// <param name="Height">Height of the laid-out block, in device units.</param>
/// <param name="Overflowed">True when some text did not fit the requested box.</param>
/// <param name="OverflowCharacters">How many characters were dropped; <c>0</c> when nothing overflowed.</param>
/// <param name="Truncated">True when the last visible line was ellipsized rather than simply clipped.</param>
public sealed record TextBlockLayout(
    IReadOnlyList<TextLine> Lines,
    float LineSpacing,
    float Height,
    bool Overflowed,
    int OverflowCharacters,
    bool Truncated)
{
    /// <summary>An empty layout — what an absent caption or a journal slot with no entry produces.</summary>
    public static TextBlockLayout Empty { get; } = new([], 0, 0, false, 0, false);

    /// <summary>True when there is nothing to draw.</summary>
    public bool IsEmpty => Lines.Count == 0;
}

/// <summary>
/// Word wrapping and line breaking measured against real <see cref="SKFont"/> metrics — the same
/// shaping the page draw uses, so measurement and rendering can never disagree (the WYSIWYG promise
/// of ADR-0003, and the contract behind <see cref="PhotoBook.Core.Abstractions.ITextMeasurer"/>).
/// </summary>
public static class TextLayout
{
    /// <summary>The ellipsis appended when a block is truncated rather than clipped.</summary>
    public const string Ellipsis = "…";

    /// <summary>
    /// Lays paragraphs out into a column of <paramref name="maxWidth"/> device units.
    /// </summary>
    /// <param name="paragraphs">Source paragraphs, in order. Nulls and blanks become blank lines between paragraphs.</param>
    /// <param name="font">The font to measure and later draw with.</param>
    /// <param name="lineHeight">Line height as a multiple of the font size (doc 10 §1); <c>0</c> uses the font's own spacing.</param>
    /// <param name="maxWidth">Column width in device units.</param>
    /// <param name="maxHeight">Column height in device units; <see cref="float.PositiveInfinity"/> for unbounded.</param>
    /// <param name="maxLines">Hard line cap, e.g. the caption band's two lines (doc 07/10).</param>
    /// <param name="ellipsize">True to end a truncated block with an ellipsis instead of clipping it.</param>
    /// <param name="paragraphSpacing">Extra space between paragraphs, in device units.</param>
    public static TextBlockLayout Layout(
        IReadOnlyList<string>? paragraphs,
        SKFont font,
        double lineHeight,
        float maxWidth,
        float maxHeight = float.PositiveInfinity,
        int maxLines = int.MaxValue,
        bool ellipsize = false,
        float paragraphSpacing = 0)
    {
        ArgumentNullException.ThrowIfNull(font);
        if (paragraphs is null || paragraphs.Count == 0 || maxWidth <= 0 || maxLines <= 0)
            return TextBlockLayout.Empty;

        var metrics = font.Metrics;
        var spacing = lineHeight > 0 ? (float)(font.Size * lineHeight) : font.Spacing;
        if (spacing <= 0) spacing = font.Size;

        // The first baseline sits one ascent below the block top, so the block's box starts at the top
        // of the tallest glyph rather than at the baseline.
        var ascent = -metrics.Ascent;
        var lines = new List<TextLine>();
        var overflowChars = 0;
        var overflowed = false;
        var truncated = false;
        var y = ascent;
        var blockHeight = 0f;

        for (var p = 0; p < paragraphs.Count && !overflowed; p++)
        {
            var text = paragraphs[p] ?? string.Empty;
            if (p > 0 && paragraphSpacing > 0) y += paragraphSpacing;

            var wrapped = WrapParagraph(text, font, maxWidth);
            for (var i = 0; i < wrapped.Count; i++)
            {
                // A line fits when its descender still clears the box, not merely its baseline.
                var candidateBottom = y + metrics.Descent;

                if (lines.Count >= maxLines || candidateBottom > maxHeight + 0.01f)
                {
                    overflowed = true;
                    overflowChars += wrapped.Skip(i).Sum(l => l.Length);
                    for (var rest = p + 1; rest < paragraphs.Count; rest++)
                        overflowChars += (paragraphs[rest] ?? string.Empty).Length;
                    break;
                }

                var line = wrapped[i];
                lines.Add(new TextLine(line, font.MeasureText(line), y, p));
                blockHeight = candidateBottom;
                y += spacing;
            }
        }

        if (overflowed && ellipsize && lines.Count > 0)
        {
            var last = lines[^1];
            var shortened = Ellipsize(last.Text, font, maxWidth);
            lines[^1] = last with { Text = shortened, Width = font.MeasureText(shortened) };
            truncated = true;
        }

        return new TextBlockLayout(lines, spacing, blockHeight, overflowed, overflowChars, truncated);
    }

    /// <summary>
    /// Draws a laid-out block with its top-left at <paramref name="origin"/>. The caller is
    /// responsible for any clipping; this method never moves or scales the text.
    /// </summary>
    /// <param name="canvas">The target canvas.</param>
    /// <param name="layout">The layout produced by <see cref="Layout"/>.</param>
    /// <param name="font">The same font the layout was measured with.</param>
    /// <param name="paint">The fill paint.</param>
    /// <param name="origin">Top-left of the text block, in device units.</param>
    /// <param name="width">Column width, used to place centered and right-aligned lines.</param>
    /// <param name="align">Horizontal alignment.</param>
    public static void Draw(
        SKCanvas canvas,
        TextBlockLayout layout,
        SKFont font,
        SKPaint paint,
        SKPoint origin,
        float width,
        SKTextAlign align = SKTextAlign.Left)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(paint);
        if (layout.IsEmpty) return;

        var x = align switch
        {
            SKTextAlign.Center => origin.X + width / 2f,
            SKTextAlign.Right => origin.X + width,
            _ => origin.X,
        };

        foreach (var line in layout.Lines)
        {
            if (line.Text.Length == 0) continue;
            canvas.DrawText(line.Text, x, origin.Y + line.BaselineY, align, font, paint);
        }
    }

    /// <summary>
    /// Draws a single line with extra letter spacing — the +0.05 em tracking doc 10 §7 asks for on
    /// the month-title year subtitle. Skia has no tracking knob, so the glyphs are advanced by hand.
    /// </summary>
    /// <param name="canvas">The target canvas.</param>
    /// <param name="text">The text to draw.</param>
    /// <param name="font">The font.</param>
    /// <param name="paint">The fill paint.</param>
    /// <param name="origin">The baseline start point when left-aligned.</param>
    /// <param name="trackingEm">Extra advance per character, in ems.</param>
    /// <param name="width">Column width, for centered and right-aligned text.</param>
    /// <param name="align">Horizontal alignment.</param>
    public static void DrawTracked(
        SKCanvas canvas,
        string text,
        SKFont font,
        SKPaint paint,
        SKPoint origin,
        double trackingEm,
        float width,
        SKTextAlign align = SKTextAlign.Left)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(paint);
        if (string.IsNullOrEmpty(text)) return;

        var tracking = (float)(font.Size * trackingEm);
        var total = MeasureTracked(text, font, trackingEm);
        var x = align switch
        {
            SKTextAlign.Center => origin.X + (width - total) / 2f,
            SKTextAlign.Right => origin.X + width - total,
            _ => origin.X,
        };

        foreach (var rune in text.EnumerateRunes())
        {
            var glyph = rune.ToString();
            canvas.DrawText(glyph, x, origin.Y, SKTextAlign.Left, font, paint);
            x += font.MeasureText(glyph) + tracking;
        }
    }

    /// <summary>The advance width of <paramref name="text"/> including <paramref name="trackingEm"/> tracking.</summary>
    public static float MeasureTracked(string text, SKFont font, double trackingEm)
    {
        ArgumentNullException.ThrowIfNull(font);
        if (string.IsNullOrEmpty(text)) return 0;
        var tracking = (float)(font.Size * trackingEm);
        var total = 0f;
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            total += font.MeasureText(rune.ToString());
            count++;
        }

        return count == 0 ? 0 : total + tracking * (count - 1);
    }

    /// <summary>
    /// Greedy word wrap of one paragraph. Words longer than the column are broken by character —
    /// a URL or a long compound must not loop forever or spill silently.
    /// </summary>
    public static IReadOnlyList<string> WrapParagraph(string? text, SKFont font, float maxWidth)
    {
        ArgumentNullException.ThrowIfNull(font);
        if (string.IsNullOrEmpty(text)) return [string.Empty];
        if (maxWidth <= 0) return [text];

        var lines = new List<string>();
        var current = new System.Text.StringBuilder();
        var currentWidth = 0f;
        var spaceWidth = font.MeasureText(" ");

        foreach (var word in Tokenize(text))
        {
            var wordWidth = font.MeasureText(word);

            if (current.Length == 0)
            {
                if (wordWidth <= maxWidth)
                {
                    current.Append(word);
                    currentWidth = wordWidth;
                    continue;
                }

                // A single word wider than the column: break it across lines by character.
                foreach (var piece in BreakWord(word, font, maxWidth))
                {
                    if (current.Length > 0)
                    {
                        lines.Add(current.ToString());
                        current.Clear();
                    }

                    current.Append(piece);
                    currentWidth = font.MeasureText(piece);
                    if (currentWidth >= maxWidth)
                    {
                        lines.Add(current.ToString());
                        current.Clear();
                        currentWidth = 0;
                    }
                }

                continue;
            }

            if (currentWidth + spaceWidth + wordWidth <= maxWidth)
            {
                current.Append(' ').Append(word);
                currentWidth += spaceWidth + wordWidth;
                continue;
            }

            lines.Add(current.ToString());
            current.Clear();
            currentWidth = 0;

            if (wordWidth <= maxWidth)
            {
                current.Append(word);
                currentWidth = wordWidth;
            }
            else
            {
                foreach (var piece in BreakWord(word, font, maxWidth))
                {
                    if (current.Length > 0)
                    {
                        lines.Add(current.ToString());
                        current.Clear();
                    }

                    current.Append(piece);
                    currentWidth = font.MeasureText(piece);
                }
            }
        }

        if (current.Length > 0) lines.Add(current.ToString());
        if (lines.Count == 0) lines.Add(string.Empty);
        return lines;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                if (start >= 0)
                {
                    yield return text[start..i];
                    start = -1;
                }
            }
            else if (start < 0)
            {
                start = i;
            }
        }

        if (start >= 0) yield return text[start..];
    }

    private static IEnumerable<string> BreakWord(string word, SKFont font, float maxWidth)
    {
        var start = 0;
        while (start < word.Length)
        {
            var length = 1;
            while (start + length < word.Length &&
                   font.MeasureText(word.AsSpan(start, length + 1).ToString()) <= maxWidth)
            {
                length++;
            }

            yield return word.Substring(start, length);
            start += length;
        }
    }

    private static string Ellipsize(string text, SKFont font, float maxWidth)
    {
        if (font.MeasureText(text + Ellipsis) <= maxWidth) return text + Ellipsis;
        for (var length = text.Length - 1; length > 0; length--)
        {
            var candidate = text[..length].TrimEnd() + Ellipsis;
            if (font.MeasureText(candidate) <= maxWidth) return candidate;
        }

        return Ellipsis;
    }
}
