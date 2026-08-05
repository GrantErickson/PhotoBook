using System.Windows;
using System.Windows.Input;
using PhotoBook.App.ViewModels;

namespace PhotoBook.App.Views;

/// <summary>Modal picker for the album or folder a book syncs from.</summary>
public partial class OneDrivePickerWindow : Window
{
    public OneDrivePickerWindow(OneDrivePickerViewModel model)
    {
        InitializeComponent();
        DataContext = model;

        model.CloseRequested += chosen =>
        {
            DialogResult = chosen;
            Close();
        };

        Loaded += async (_, _) => await model.LoadAsync();
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
