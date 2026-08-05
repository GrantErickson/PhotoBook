using System.Windows;
using System.Windows.Input;
using PhotoBook.App.ViewModels.Photos;

namespace PhotoBook.App.Views.Photos;

/// <summary>
/// The <em>Change date…</em> dialog (doc 09 §2.1, R6). It exists to make the consequence visible
/// before it happens: a new month moves the photo to that Chapter's Unplaced bin, and a date outside
/// the book's year moves it to the Outside-book tray. The banner above the buttons says which, live.
/// </summary>
public partial class ChangeDateWindow : Window
{
    /// <param name="model">The dialog's view model, bound to the photo being re-dated.</param>
    public ChangeDateWindow(ChangeDateViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        InitializeComponent();
        DataContext = model;

        model.CloseRequested += changed =>
        {
            DialogResult = changed;
            Close();
        };
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
