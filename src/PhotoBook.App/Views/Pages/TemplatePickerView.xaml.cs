using System.Windows.Controls;

namespace PhotoBook.App.Views.Pages;

/// <summary>
/// The template gallery popover (doc 09 §3.4). All behaviour lives in
/// <see cref="ViewModels.Pages.TemplatePickerViewModel"/>; the view is pure markup plus this
/// constructor, so the gallery can be hosted in a Popup, a flyout or a docked panel unchanged.
/// </summary>
public partial class TemplatePickerView : UserControl
{
    /// <summary>Creates the gallery. The view model arrives as the DataContext.</summary>
    public TemplatePickerView() => InitializeComponent();
}
