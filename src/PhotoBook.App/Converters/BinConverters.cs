using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PhotoBook.App.Services;

namespace PhotoBook.App.Converters;

/// <summary>
/// The bin panel's dock edge to a stack orientation: bottom docks are a horizontal filmstrip,
/// side docks a vertical list (doc 09 §3.5).
/// </summary>
public sealed class BinDockToOrientationConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is BinDock.Bottom ? Orientation.Horizontal : Orientation.Vertical;

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Orientation.Horizontal ? BinDock.Bottom : BinDock.Left;
}

/// <summary>
/// A count to visibility: visible when it is greater than zero. Used by the count chips so a bin
/// with nothing in it does not carry a "0" badge.
/// </summary>
public sealed class PositiveCountToVisibilityConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>A count to a boolean: true when it is greater than zero — enables "Fill from…" buttons.</summary>
public sealed class PositiveCountToBooleanConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count > 0;

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>A non-empty string becomes Visible — for the panel's one-line status note.</summary>
public sealed class NonEmptyStringToVisibilityConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
