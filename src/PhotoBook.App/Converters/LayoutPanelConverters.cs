using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PhotoBook.App.ViewModels.Pages;
using PhotoBook.Core.Model;
using PhotoBook.Rendering;

namespace PhotoBook.App.Converters;

/// <summary>
/// Looks a semantic brush up in <c>Themes/Palette.xaml</c>. Every colour in this file comes back
/// through here, so no view in the layout, preflight, export or style panels ever names a hex value.
/// </summary>
internal static class PaletteLookup
{
    /// <summary>The palette brush for a key, or transparent when the resource scope is absent (designer).</summary>
    public static Brush Brush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
}

/// <summary>Colours one page chip in the R16 warning dialog's chapter map by what the run does to it.</summary>
public sealed class LayoutPageFateToBrushConverter : IValueConverter
{
    /// <summary>True to return the chip's text colour rather than its fill.</summary>
    public bool Foreground { get; set; }

    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var fate = value as LayoutPageFate? ?? LayoutPageFate.Untouched;
        if (Foreground)
        {
            return fate switch
            {
                LayoutPageFate.Rebuilt => PaletteLookup.Brush("TextOnAccentBrush"),
                LayoutPageFate.Pinned or LayoutPageFate.Detached => PaletteLookup.Brush("TextPrimaryBrush"),
                LayoutPageFate.InsertAfter => PaletteLookup.Brush("SuccessBrush"),
                _ => PaletteLookup.Brush("TextTertiaryBrush"),
            };
        }

        return fate switch
        {
            LayoutPageFate.Rebuilt => PaletteLookup.Brush("AccentBrush"),
            LayoutPageFate.Pinned => PaletteLookup.Brush("WarningWashBrush"),
            LayoutPageFate.Detached => PaletteLookup.Brush("AccentWashBrush"),
            LayoutPageFate.InsertAfter => PaletteLookup.Brush("SuccessWashBrush"),
            _ => PaletteLookup.Brush("SurfaceHighestBrush"),
        };
    }

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Border stroke for a page chip; only the protected states draw one.</summary>
public sealed class LayoutPageFateToStrokeConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as LayoutPageFate?) switch
        {
            LayoutPageFate.Pinned => PaletteLookup.Brush("WarningBrush"),
            LayoutPageFate.Detached => PaletteLookup.Brush("AccentBrush"),
            LayoutPageFate.InsertAfter => PaletteLookup.Brush("SuccessBrush"),
            _ => PaletteLookup.Brush("BorderSubtleBrush"),
        };

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Error red or warning amber for a preflight row (doc 12).</summary>
public sealed class PreflightSeverityToBrushConverter : IValueConverter
{
    /// <summary>True to return the translucent wash used behind the row instead of the solid stroke.</summary>
    public bool Wash { get; set; }

    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isError = value as PreflightSeverity? == PreflightSeverity.Error;
        return Wash
            ? PaletteLookup.Brush(isError ? "ErrorWashBrush" : "WarningWashBrush")
            : PaletteLookup.Brush(isError ? "ErrorBrush" : "WarningBrush");
    }

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// The inheritance chip of doc 10 §2: a field set at the level being edited reads as an accent
/// override; one inherited from above reads as quiet secondary text.
/// </summary>
public sealed class StyleLevelToBrushConverter : IValueConverter
{
    /// <summary>True to return the chip's text colour rather than its fill.</summary>
    public bool Foreground { get; set; }

    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var owned = value is bool flag && flag;
        if (Foreground)
        {
            return PaletteLookup.Brush(owned ? "TextOnAccentBrush" : "TextTertiaryBrush");
        }

        return PaletteLookup.Brush(owned ? "AccentBrush" : "SurfaceHighestBrush");
    }

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Turns a <c>#RRGGBB</c> style colour into a swatch brush, and back again.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && TryParse(hex, out var color))
        {
            return new SolidColorBrush(color);
        }

        return PaletteLookup.Brush("SurfaceHighestBrush");
    }

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SolidColorBrush brush ? Format(brush.Color) : string.Empty;

    /// <summary>
    /// Parses <c>#RGB</c>, <c>#RRGGBB</c> or <c>#AARRGGBB</c>. Hand-rolled rather than delegating to
    /// <see cref="ColorConverter"/> because this runs on every keystroke in the hex box, and a
    /// half-typed value must be a quiet "not yet" rather than an exception.
    /// </summary>
    /// <param name="hex">The text to parse.</param>
    /// <param name="color">The parsed colour.</param>
    /// <returns>True when the text is a complete colour.</returns>
    public static bool TryParse(string? hex, out Color color)
    {
        color = Colors.Transparent;
        var text = hex?.Trim();
        if (text is null || text.Length < 4 || text[0] != '#')
        {
            return false;
        }

        var digits = text.AsSpan(1);
        if (digits.Length is not (3 or 6 or 8))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[4];
        bytes[0] = 0xFF;

        if (digits.Length == 3)
        {
            for (var i = 0; i < 3; i++)
            {
                if (!TryHex(digits[i], out var v))
                {
                    return false;
                }

                bytes[i + 1] = (byte)((v << 4) | v);
            }
        }
        else
        {
            var offset = digits.Length == 8 ? 0 : 1;
            for (var i = 0; i < digits.Length / 2; i++)
            {
                if (!TryHex(digits[i * 2], out var high) || !TryHex(digits[(i * 2) + 1], out var low))
                {
                    return false;
                }

                bytes[i + offset] = (byte)((high << 4) | low);
            }
        }

        color = Color.FromArgb(bytes[0], bytes[1], bytes[2], bytes[3]);
        return true;

        static bool TryHex(char c, out int value)
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

    /// <summary>Formats a colour the way doc 10 stores it: <c>#RRGGBB</c>, upper case.</summary>
    public static string Format(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}

/// <summary>Collapses an element when a count is zero — used for "no findings" and empty sections.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    /// <summary>True to invert: visible only when the count is zero.</summary>
    public bool Invert { get; set; }

    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value switch
        {
            int n => n,
            System.Collections.ICollection c => c.Count,
            _ => 0,
        };

        return (count > 0) != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when the bound value equals the converter's <see cref="Match"/> — for segmented radio groups.</summary>
public sealed class EnumMatchConverter : IValueConverter
{
    /// <summary>The value that reads as checked.</summary>
    public object? Match { get; set; }

    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value, Match ?? parameter);

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? (Match ?? parameter) ?? Binding.DoNothing : Binding.DoNothing;
}

/// <summary>Renders a <see cref="StyleLevel"/> as the chip text doc 10 §2 asks for.</summary>
public sealed class StyleLevelToTextConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as StyleLevel?) switch
        {
            StyleLevel.Page => "Page",
            StyleLevel.Chapter => "Chapter",
            StyleLevel.Book => "Book",
            _ => "Default",
        };

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
