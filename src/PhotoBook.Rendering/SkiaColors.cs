using System.Globalization;
using SkiaSharp;

namespace PhotoBook.Rendering;

/// <summary>
/// Parsing of the <c>#RRGGBB</c> sRGB colors the <see cref="PhotoBook.Core.Model.Style"/> cascade
/// stores (doc 10 §1). Unparseable values fall back to a caller-supplied default rather than
/// throwing — a typo in a hand-edited <c>book.json</c> must not make a page unrenderable.
/// </summary>
public static class SkiaColors
{
    /// <summary>The v1 page background, <c>#000000</c> (kernel §3, R21).</summary>
    public static SKColor Background { get; } = SKColors.Black;

    /// <summary>The default text color on that background, <c>#FFFFFF</c> (kernel §3).</summary>
    public static SKColor Text { get; } = SKColors.White;

    /// <summary>The empty-slot flag color <c>#FFB300</c> (doc 09 §3.6, R14).</summary>
    public static SKColor Amber { get; } = new(0xFF, 0xB3, 0x00);

    /// <summary>
    /// Parses <c>#RRGGBB</c>, <c>#AARRGGBB</c>, <c>#RGB</c> or a bare hex triplet, returning
    /// <paramref name="fallback"/> when the text is null, empty or malformed.
    /// </summary>
    public static SKColor Parse(string? text, SKColor fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var span = text.AsSpan().Trim();
        if (span.Length > 0 && span[0] == '#') span = span[1..];

        switch (span.Length)
        {
            case 3:
            {
                if (!TryNibble(span[0], out var r) || !TryNibble(span[1], out var g) || !TryNibble(span[2], out var b))
                    return fallback;
                return new SKColor((byte)(r * 17), (byte)(g * 17), (byte)(b * 17));
            }
            case 6:
            {
                if (!uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return fallback;
                return new SKColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            }
            case 8:
            {
                if (!uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb)) return fallback;
                return new SKColor((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));
            }
            default:
                return fallback;
        }
    }

    /// <summary>The color with its alpha replaced by <paramref name="opacity"/> in <c>0..1</c>.</summary>
    public static SKColor WithOpacity(this SKColor color, double opacity)
    {
        var clamped = double.IsNaN(opacity) ? 0 : Math.Clamp(opacity, 0, 1);
        return color.WithAlpha((byte)Math.Round(clamped * 255, MidpointRounding.AwayFromZero));
    }

    private static bool TryNibble(char c, out int value)
    {
        value = c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
        return value >= 0;
    }
}
