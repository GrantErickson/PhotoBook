using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PhotoBook.Core.Model;

namespace PhotoBook.App.Converters;

/// <summary>
/// A Focus Region's kind to its themed brush — the doc 09 §2.2 colour code (user = accent blue,
/// person = green, face = teal, saliency = grey). Pass <c>wash</c> as the parameter for the
/// translucent fill. Colour is looked up from <c>Themes/PhotoEditing.xaml</c>, never hardcoded.
/// </summary>
public sealed class FocusKindToBrushConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var wash = parameter is string s && s.Equals("wash", StringComparison.OrdinalIgnoreCase);
        var key = value switch
        {
            FocusKind.User => wash ? "FocusUserWashBrush" : "FocusUserBrush",
            FocusKind.Person => wash ? "FocusPersonWashBrush" : "FocusPersonBrush",
            FocusKind.Face => wash ? "FocusFaceWashBrush" : "FocusFaceBrush",
            _ => wash ? "FocusSaliencyWashBrush" : "FocusSaliencyBrush",
        };

        return Application.Current?.TryFindResource(key) as Brush ?? Fallback(key);
    }

    /// <summary>
    /// A converter can only see <see cref="Application.Resources"/>, so these mirror
    /// <c>Themes/PhotoEditing.xaml</c> exactly for the case where that dictionary has not been merged
    /// into App.xaml (and for the XAML designer). Merging it makes these unreachable.
    /// </summary>
    private static Brush Fallback(string key)
    {
        if (Fallbacks.TryGetValue(key, out var brush))
        {
            return brush;
        }

        return Brushes.Gray;
    }

    private static readonly Dictionary<string, Brush> Fallbacks = Build();

    private static Dictionary<string, Brush> Build()
    {
        Brush Frozen(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        return new Dictionary<string, Brush>(StringComparer.Ordinal)
        {
            ["FocusUserBrush"] = Frozen("#FF4C8DF5"),
            ["FocusUserWashBrush"] = Frozen("#294C8DF5"),
            ["FocusPersonBrush"] = Frozen("#FF3FB765"),
            ["FocusPersonWashBrush"] = Frozen("#1F3FB765"),
            ["FocusFaceBrush"] = Frozen("#FF35B9B4"),
            ["FocusFaceWashBrush"] = Frozen("#1F35B9B4"),
            ["FocusSaliencyBrush"] = Frozen("#FF767F8A"),
            ["FocusSaliencyWashBrush"] = Frozen("#14767F8A"),
        };
    }

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Any non-empty string becomes Visible — for status lines that should not reserve space.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
