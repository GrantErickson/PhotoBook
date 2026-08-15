using System.Windows;
using System.Windows.Input;
using PhotoBook.App.ViewModels.Photos;

namespace PhotoBook.App.Views.Photos;

/// <summary>
/// Hosts the Focus Region editor at a size worth editing in (doc 09 §2.2). Every change is already
/// committed and undoable by the time this window closes — there is no OK/Cancel, because "Cancel"
/// on a series of undoable edits would be a second, weaker undo model.
/// </summary>
public partial class FocusRegionWindow : Window
{
    /// <param name="model">The editor view model, already pointed at a photo.</param>
    public FocusRegionWindow(FocusRegionEditorViewModel model)
    {
        InitializeComponent();
        DataContext = model;
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
