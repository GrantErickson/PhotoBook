using System.Windows;
using System.Windows.Input;
using PhotoBook.App.ViewModels.Journal;

namespace PhotoBook.App.Views.Journal;

/// <summary>
/// Journal review as a modeless window: the Import Report and the day map over one book.
///
/// <para>
/// Doc 11 opens the report automatically after an import that produced any non-matched entry — that
/// is the whole point, since the alternative is journal text quietly missing from the book — and
/// keeps it reachable afterwards. Use <see cref="ShowAfterImport"/> for the first case and
/// <see cref="ShowReview"/> for the second; both reuse a single window per owner, because dating a
/// long journal is work the user comes back to rather than a modal moment.
/// </para>
/// </summary>
public partial class JournalReviewWindow : Window
{
    private static readonly Dictionary<Window, JournalReviewWindow> Open = [];

    private readonly JournalReviewViewModel _model;

    /// <param name="model">The review model over the open session.</param>
    public JournalReviewWindow(JournalReviewViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        InitializeComponent();

        _model = model;
        DataContext = model;
        model.CloseRequested += Close;
    }

    /// <summary>Raised when a page chip is clicked: the chapter month and the page id to open.</summary>
    public event Action<int, string>? PageActivated
    {
        add => _model.PageActivated += value;
        remove => _model.PageActivated -= value;
    }

    /// <summary>
    /// Shows the review for a book already open — the "reviewable later" half of doc 11. Brings the
    /// existing window forward rather than stacking a second copy of the same work.
    /// </summary>
    /// <param name="owner">The shell window.</param>
    /// <param name="model">The review model.</param>
    /// <param name="month">The month the day map should show.</param>
    /// <returns>The window, so the caller can wire navigation.</returns>
    public static JournalReviewWindow ShowReview(Window owner, JournalReviewViewModel model, int month)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(model);

        model.Load(month);
        return Present(owner, model);
    }

    /// <summary>
    /// Shows the review after an import, but only when the import produced something worth saying:
    /// an entry with no date, an unsure date, a date outside the book's year, or an assignment the
    /// re-import could not place (doc 11).
    /// </summary>
    /// <param name="owner">The shell window.</param>
    /// <param name="model">The review model.</param>
    /// <param name="month">The month the day map should show.</param>
    /// <returns>The window when it was shown, or null when the import was clean.</returns>
    public static JournalReviewWindow? ShowAfterImport(Window owner, JournalReviewViewModel model, int month)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(model);

        return model.LoadAfterImport(month) ? Present(owner, model) : null;
    }

    private static JournalReviewWindow Present(Window owner, JournalReviewViewModel model)
    {
        if (Open.TryGetValue(owner, out var existing))
        {
            existing.Activate();
            return existing;
        }

        var window = new JournalReviewWindow(model) { Owner = owner };
        Open[owner] = window;
        window.Closed += (_, _) => Open.Remove(owner);
        window.Show();
        return window;
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
