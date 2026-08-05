using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PhotoBook.App.ViewModels;

namespace PhotoBook.App.Views;

/// <summary>
/// The open book: chapter rail, Photos grid, and the Pages editor. The only behaviour here is doc 09
/// §5's keyboard map for the bits the shell owns — the tab toggle, the photo-selection keys and the
/// Pages-tab panel toggles. Crop, page navigation and layout-override keys belong to the surfaces
/// that own those gestures and are deliberately not repeated.
/// </summary>
public partial class BookView : UserControl
{
    private Window? _window;

    /// <summary>Creates the view.</summary>
    public BookView()
    {
        InitializeComponent();

        // The shortcuts hang off the *window*, not off this control: a tunnelling handler here only
        // ever fires once keyboard focus has already landed inside the workspace, so `T` did nothing
        // until the user had clicked something first.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private BookViewModel? Model => DataContext as BookViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            return;
        }

        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.PreviewKeyDown += OnShortcutKey;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.PreviewKeyDown -= OnShortcutKey;
            _window = null;
        }
    }

    private void OnShortcutKey(object sender, KeyEventArgs e)
    {
        // A collapsed book view is the dashboard's turn: its keys must not be stolen from it.
        if (!IsVisible || Model is not { } model || e.OriginalSource is TextBoxBase)
        {
            return;
        }

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        // Ctrl+Tab flips the two halves of the month workspace, in both directions.
        if (ctrl && e.Key is Key.Tab)
        {
            model.ToggleTabCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (ctrl)
        {
            return;
        }

        switch (e.Key)
        {
            // ---- photo keys, live in either tab while a photo is selected
            case Key.D when model.SelectedPhoto is not null:
                model.Inspector.ChangeDateCommand.Execute(null);
                break;

            case Key.F when model.SelectedPhoto is not null:
                model.Inspector.EditFocusCommand.Execute(null);
                break;

            case Key.OemOpenBrackets when model.SelectedPhoto is not null:
                model.DemoteCommand.Execute(null);
                break;

            case Key.OemCloseBrackets when model.SelectedPhoto is not null:
                model.PromoteCommand.Execute(null);
                break;

            case Key.E when model.SelectedPhoto is not null && !model.IsPagesTab:
                model.ToggleExcludeCommand.Execute(null);
                break;

            // ---- Pages-tab panels
            case Key.T when model.IsPagesTab:
                model.ToggleTemplatePickerCommand.Execute(null);
                break;

            case Key.B when model.IsPagesTab:
                model.ToggleBinsCommand.Execute(null);
                break;

            case Key.L when model.IsPagesTab:
                model.PageOverride.ToggleCommand.Execute(null);
                break;

            case Key.G when model.IsPagesTab:
                model.PageEditor.ToggleGuidesCommand.Execute(null);
                break;

            case Key.Escape when model.IsTemplatePickerOpen || model.IsStylePanelOpen:
                model.IsTemplatePickerOpen = false;
                model.IsStylePanelOpen = false;
                break;

            default:
                return;
        }

        e.Handled = true;
    }
}
