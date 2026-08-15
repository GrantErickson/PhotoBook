using System.Windows.Controls;

namespace PhotoBook.App.Views.Journal;

/// <summary>
/// The month, day by day, with the pages that carry each day's journal text — the answer to "did my
/// words actually make it into the book".
/// <para>Bind to a <see cref="ViewModels.Journal.JournalDayMapViewModel"/>.</para>
/// </summary>
public partial class JournalDayMapPanel : UserControl
{
    /// <summary>Creates the panel.</summary>
    public JournalDayMapPanel() => InitializeComponent();
}
