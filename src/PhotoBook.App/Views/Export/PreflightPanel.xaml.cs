using System.Windows.Controls;

namespace PhotoBook.App.Views.Export;

/// <summary>
/// The doc 12 preflight gate as a panel: findings grouped by severity, every row a link to the page,
/// slot or photo it is about, a <em>Re-check</em> that re-runs the scan in place, and the single
/// acknowledgement that covers all warnings. Errors are stated as blocking with no override.
/// <para>
/// Set <see cref="System.Windows.FrameworkElement.DataContext"/> to a
/// <see cref="ViewModels.Export.PreflightViewModel"/> and subscribe to its
/// <c>NavigationRequested</c> to move the editor's selection.
/// </para>
/// </summary>
public partial class PreflightPanel : UserControl
{
    /// <summary>Creates the panel.</summary>
    public PreflightPanel() => InitializeComponent();
}
