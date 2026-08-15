using System.Windows;
using System.Windows.Input;
using PhotoBook.App.ViewModels;

namespace PhotoBook.App.Views;

/// <summary>
/// The discoverable half of doc 09 §5: the keyboard map, rendered from the same
/// <see cref="EditorShortcuts"/> table the router dispatches from.
///
/// <para>
/// Modeless and single-instance, because it is a reference — the user opens it, leaves it beside the
/// editor while they learn two keys, and closes it. Reached from the <c>?</c> button in the title bar,
/// <c>F1</c>, or <c>?</c>.
/// </para>
/// </summary>
public partial class ShortcutsWindow : Window
{
    private static ShortcutsWindow? _open;

    /// <summary>Creates the reference window.</summary>
    public ShortcutsWindow()
    {
        InitializeComponent();
        DataContext = new ShortcutsViewModel();
    }

    /// <summary>
    /// Shows the reference over <paramref name="owner"/>, bringing the existing one forward rather
    /// than stacking copies of a static list.
    /// </summary>
    /// <param name="owner">The window to centre on; the main window by default.</param>
    public static void Show(Window? owner)
    {
        if (_open is { } existing)
        {
            existing.Activate();
            return;
        }

        var window = new ShortcutsWindow { Owner = owner ?? Application.Current?.MainWindow };
        _open = window;
        window.Closed += (_, _) => _open = null;
        window.Show();
    }

    /// <inheritdoc/>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPreviewKeyDown(e);

        // Esc and F1 both close it: whichever key the user reached for, the reference gets out of
        // the way rather than needing the mouse.
        if (e.Key is Key.Escape or Key.F1)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
