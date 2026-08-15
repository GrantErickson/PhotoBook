using System.Windows;
using System.Windows.Input;
using PhotoBook.App.Services;
using PhotoBook.App.ViewModels.Pages;

namespace PhotoBook.App.Views.Pages;

/// <summary>
/// The R16 warning modal (doc 09 §3.8). It is shown <em>before</em> the engine touches anything and
/// names the exact pages that will change; cancelling leaves the chapter untouched because nothing
/// has run yet.
/// </summary>
public partial class LayoutWarningDialog : Window
{
    private LayoutPlan? _accepted;

    /// <summary>Creates the modal around a precomputed pair of dry runs.</summary>
    public LayoutWarningDialog(LayoutWarningViewModel model)
    {
        InitializeComponent();
        DataContext = model;

        model.CloseRequested += plan =>
        {
            _accepted = plan;
            DialogResult = plan is not null;
            Close();
        };
    }

    /// <summary>
    /// Shows the modal and returns the plan the user accepted, or null when they cancelled — the
    /// shape <see cref="LayoutCommandsViewModel.ConfirmationRequested"/> expects.
    /// </summary>
    /// <param name="owner">The window to center on.</param>
    /// <param name="request">The default plan plus the include-pinned alternative.</param>
    public static LayoutPlan? Show(Window? owner, LayoutConfirmationRequest request)
    {
        var dialog = new LayoutWarningDialog(new LayoutWarningViewModel(request))
        {
            Owner = owner ?? Application.Current?.MainWindow,
        };

        dialog.ShowDialog();
        return dialog._accepted;
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
