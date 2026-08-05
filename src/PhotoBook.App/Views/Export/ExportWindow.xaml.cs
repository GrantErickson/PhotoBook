using System.Windows;
using System.Windows.Input;
using PhotoBook.App.ViewModels.Export;
using PhotoBook.Rendering;

namespace PhotoBook.App.Views.Export;

/// <summary>
/// Hosts the export flow as a modal, for shells that would rather not dock it. The preflight gate
/// runs on open, so the window never appears claiming it can export a book it has not checked.
/// </summary>
public partial class ExportWindow : Window
{
    private readonly ExportViewModel _model;

    /// <summary>Creates the window around an export model.</summary>
    /// <param name="model">The export model, already constructed over the open session.</param>
    /// <param name="preferredMonth">The month to preselect for a chapter-scoped export.</param>
    public ExportWindow(ExportViewModel model, int? preferredMonth = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        InitializeComponent();

        _model = model;
        Panel.DataContext = model;
        model.CloseRequested += Close;

        Loaded += async (_, _) => await model.InitializeAsync(preferredMonth);
    }

    /// <summary>Raised when a preflight row is clicked, so the shell can navigate; the window closes first.</summary>
    public event Action<PreflightFinding>? NavigationRequested
    {
        add => _model.Preflight.NavigationRequested += value;
        remove => _model.Preflight.NavigationRequested -= value;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
