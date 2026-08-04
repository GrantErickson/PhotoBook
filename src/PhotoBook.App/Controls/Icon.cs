using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PhotoBook.App.Controls;

/// <summary>
/// Renders one of the vector <see cref="Geometry"/> resources from <c>Themes/Icons.xaml</c>.
/// <para>
/// The icon always paints in the inherited <see cref="Control.Foreground"/>, so an icon inside a
/// button dims, highlights and inverts with its host automatically — never set a brush on an icon.
/// App icons are authored on a 24 × 24 grid as stroke artwork; set <see cref="Filled"/> for the
/// area-artwork icons (<c>Icon.Star.Filled</c>, <c>Icon.Dot</c>).
/// </para>
/// <example>
/// <code>&lt;pb:Icon Data="{StaticResource Icon.Crop}" Width="18" Height="18" /&gt;</code>
/// </example>
/// </summary>
public sealed class Icon : Control
{
    /// <summary>The geometry to draw; use a key from <c>Themes/Icons.xaml</c>.</summary>
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(Icon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>True to fill the geometry instead of stroking it (area artwork).</summary>
    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
        nameof(Filled), typeof(bool), typeof(Icon),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Stroke width in device-independent pixels. It is not scaled by the icon's size, so a smaller
    /// icon keeps a hairline: 1.4 reads correctly from 14 px to 20 px.
    /// </summary>
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(1.4d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <inheritdoc cref="DataProperty"/>
    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <inheritdoc cref="FilledProperty"/>
    public bool Filled
    {
        get => (bool)GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    /// <inheritdoc cref="StrokeThicknessProperty"/>
    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }
}
