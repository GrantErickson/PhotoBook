using System.Windows;
using System.Windows.Input;
using PhotoBook.App.Services;
using PhotoBook.App.ViewModels;

namespace PhotoBook.App.Views;

/// <summary>
/// The modal wrapper around <see cref="BookSettingsPanel"/> — the surface that makes a book's own
/// settings reachable at all (R19: page size is data everywhere in the engine and had no control
/// anywhere in the app).
/// </summary>
public partial class BookSettingsWindow : Window
{
    private bool _applied;

    /// <summary>Creates the modal around a settings view model.</summary>
    public BookSettingsWindow(BookSettingsViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        InitializeComponent();
        DataContext = model;

        model.CloseRequested += applied =>
        {
            _applied = applied;
            DialogResult = applied;
            Close();
        };
    }

    /// <summary>
    /// Shows the settings modal over <paramref name="owner"/> and returns true when the user applied
    /// something. The caller keeps the view model, so it can subscribe to its
    /// <see cref="BookSettingsViewModel.ChapterChanged"/> and
    /// <see cref="BookSettingsViewModel.Applied"/> events before showing.
    /// </summary>
    /// <param name="owner">The window to centre on; the main window by default.</param>
    /// <param name="model">The settings view model, already pointed at the open session.</param>
    public static bool Show(Window? owner, BookSettingsViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        model.Reload();
        var dialog = new BookSettingsWindow(model)
        {
            Owner = owner ?? Application.Current?.MainWindow,
        };

        dialog.ShowDialog();
        return dialog._applied;
    }

    /// <summary>Builds the view model and shows the modal — the one-liner a command handler wants.</summary>
    /// <param name="owner">The window to centre on.</param>
    /// <param name="session">The open project.</param>
    /// <param name="undo">The book's undo history.</param>
    /// <param name="jobs">The background queue the optional layout run uses.</param>
    /// <param name="chapterChanged">Called with each chapter month whose pages were rebuilt.</param>
    public static bool Show(
        Window? owner,
        ProjectSession session,
        UndoStack undo,
        JobQueue jobs,
        Action<int>? chapterChanged = null)
    {
        var model = new BookSettingsViewModel(session, undo, jobs);
        if (chapterChanged is not null)
        {
            model.ChapterChanged += chapterChanged;
        }

        return Show(owner, model);
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
