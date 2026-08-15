using System.Windows;
using System.Windows.Controls;
using PhotoBook.App.ViewModels.Pages;

namespace PhotoBook.App.Views.Pages;

/// <summary>
/// The three R16 auto-layout commands as a toolbar (doc 09 §3.8). Dropping this control next to the
/// page canvas is all the wiring the commands need: it hooks the view model's confirmation callback
/// to the styled warning modal, so no command can reach the engine without the user first seeing the
/// page numbers it is about to replace.
/// </summary>
public partial class LayoutCommandsBar : UserControl
{
    /// <summary>Creates the bar; set <see cref="FrameworkElement.DataContext"/> to a <see cref="LayoutCommandsViewModel"/>.</summary>
    public LayoutCommandsBar()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is LayoutCommandsViewModel previous)
        {
            previous.ConfirmationRequested = null;
        }

        if (e.NewValue is LayoutCommandsViewModel model)
        {
            model.ConfirmationRequested = request =>
                LayoutWarningDialog.Show(Window.GetWindow(this), request);
        }
    }
}
