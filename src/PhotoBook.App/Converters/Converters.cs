using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PhotoBook.Core.Model;

namespace PhotoBook.App.Converters;

/// <summary>True becomes Visible. Pass "invert" as the parameter to flip a single binding.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

/// <summary>False becomes Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not Visibility.Visible;
}

/// <summary>Null becomes Visible — for empty states.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Non-null becomes Visible.</summary>
public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Negates a boolean.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

/// <summary>Quality tier to its badge brush. S reads as the accent; C recedes.</summary>
public sealed class TierToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            Tier.S => "TierSBrush",
            Tier.A => "TierABrush",
            Tier.B => "TierBBrush",
            Tier.C => "TierCBrush",
            _ => "TextTertiaryBrush",
        };

        return Application.Current.TryFindResource(key) as Brush
               ?? Application.Current.TryFindResource("TextTertiaryBrush") as Brush
               ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when the bound value equals the parameter — for radio-style selection.</summary>
public sealed class EqualityToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value?.ToString(), parameter?.ToString());

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not null ? parameter : Binding.DoNothing;
}
