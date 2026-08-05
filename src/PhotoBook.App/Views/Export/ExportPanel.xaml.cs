using System.Windows.Controls;

namespace PhotoBook.App.Views.Export;

/// <summary>
/// The doc 12 export flow: scope on the left, the preflight gate on the right, and two buttons that
/// mean exactly what they say — <em>Export PDF</em> is gated, <em>Draft PDF</em> never is.
/// Set <see cref="System.Windows.FrameworkElement.DataContext"/> to an
/// <see cref="ViewModels.Export.ExportViewModel"/>.
/// </summary>
public partial class ExportPanel : UserControl
{
    /// <summary>Creates the panel.</summary>
    public ExportPanel() => InitializeComponent();
}
