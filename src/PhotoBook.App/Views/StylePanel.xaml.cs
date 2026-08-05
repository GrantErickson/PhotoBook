using System.Windows;
using System.Windows.Controls;
using PhotoBook.App.ViewModels;

namespace PhotoBook.App.Views;

/// <summary>Picks the editor for one style leaf: slider, swatch, font picker or switch.</summary>
public sealed class StyleFieldTemplateSelector : DataTemplateSelector
{
    /// <summary>Template for <see cref="StyleFieldKind.Number"/>.</summary>
    public DataTemplate? Number { get; set; }

    /// <summary>Template for <see cref="StyleFieldKind.Color"/>.</summary>
    public DataTemplate? Color { get; set; }

    /// <summary>Template for <see cref="StyleFieldKind.Family"/>.</summary>
    public DataTemplate? Family { get; set; }

    /// <summary>Template for <see cref="StyleFieldKind.Toggle"/>.</summary>
    public DataTemplate? Toggle { get; set; }

    /// <inheritdoc/>
    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) =>
        (item as StyleFieldViewModel)?.Kind switch
        {
            StyleFieldKind.Number => Number,
            StyleFieldKind.Color => Color,
            StyleFieldKind.Family => Family,
            StyleFieldKind.Toggle => Toggle,
            _ => base.SelectTemplate(item, container),
        };
}

/// <summary>
/// The style panel of R23 and doc 10: image borders on every photo at once, journal and caption type
/// set independently, month titles, the overlay scrim and the page background — each row carrying
/// the inheritance chip (<em>Book</em>, <em>Chapter</em>, <em>Page</em>, <em>Default</em>) and a
/// per-field reset that restores inheritance instead of freezing a copy.
/// <para>
/// Set <see cref="FrameworkElement.DataContext"/> to a <see cref="StyleViewModel"/> and subscribe to
/// its <c>StyleChanged</c> to re-render the page preview live.
/// </para>
/// </summary>
public partial class StylePanel : UserControl
{
    /// <summary>Creates the panel.</summary>
    public StylePanel() => InitializeComponent();
}
