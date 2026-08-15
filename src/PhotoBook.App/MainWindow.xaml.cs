using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using PhotoBook.App.ViewModels;

namespace PhotoBook.App;

/// <summary>
/// The shell window. Chrome is custom (a borderless window with our own caption bar), so the
/// resize border and caption height are configured through <see cref="WindowChrome"/> rather than
/// the default Win32 frame.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(ShellViewModel shell)
    {
        InitializeComponent();
        DataContext = shell;

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            CornerRadius = default,
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
        });

        StateChanged += (_, _) => SyncMaximizeState();
        SyncMaximizeState();
    }

    /// <summary>
    /// A maximized borderless window would otherwise cover the taskbar, so the maximized state
    /// gets an explicit inset.
    /// </summary>
    private void SyncMaximizeState()
    {
        BorderThickness = WindowState == WindowState.Maximized
            ? new Thickness(SystemParameters.WindowResizeBorderThickness.Left + 4)
            : default;

        if (FindName("MaxIcon") is Controls.Icon icon)
        {
            var key = WindowState == WindowState.Maximized ? "Chrome.Restore" : "Chrome.Maximize";
            icon.Data = TryFindResource(key) as Geometry;
        }
    }

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
