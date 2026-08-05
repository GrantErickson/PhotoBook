using System.Windows.Controls;

namespace PhotoBook.App.Views;

/// <summary>
/// The open book's settings: title, year, page size, print profile and layout seed (R3, R19,
/// kernel §7).
/// <para>
/// Everything on it is a draft until Apply. Each field carries the consequence of changing it —
/// which photos a year move would strand in the Outside-book tray, how many layouts exist for a
/// trim, what a new seed does — because three of these five settings reach past their own field and
/// a form that hid that would be lying. Set
/// <see cref="System.Windows.FrameworkElement.DataContext"/> to a
/// <see cref="ViewModels.BookSettingsViewModel"/>; it is hosted by
/// <see cref="BookSettingsWindow"/> but is a plain panel and will dock anywhere.
/// </para>
/// </summary>
public partial class BookSettingsPanel : UserControl
{
    /// <summary>Creates the panel.</summary>
    public BookSettingsPanel() => InitializeComponent();
}
