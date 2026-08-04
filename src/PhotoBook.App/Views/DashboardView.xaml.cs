using System.IO;
using System.Windows;
using System.Windows.Controls;
using PhotoBook.App.ViewModels;

namespace PhotoBook.App.Views;

/// <summary>The first screen: recent books, the two primary actions, and folder drop.</summary>
public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var isFolder = e.Data.GetDataPresent(DataFormats.FileDrop)
                       && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths
                       && Directory.Exists(paths[0]);

        e.Effects = isFolder ? DragDropEffects.Link : DragDropEffects.None;
        DropHint.Visibility = isFolder ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) =>
        DropHint.Visibility = Visibility.Collapsed;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;

        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths
            && Directory.Exists(paths[0])
            && DataContext is ShellViewModel shell)
        {
            await shell.OpenPathAsync(paths[0]);
        }
    }
}
