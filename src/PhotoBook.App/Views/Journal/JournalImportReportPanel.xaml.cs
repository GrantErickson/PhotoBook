using System.Windows.Controls;

namespace PhotoBook.App.Views.Journal;

/// <summary>
/// The Import Report of doc 11: what the date matcher made of every journal entry, and the one place
/// an entry it could not date gets one.
/// <para>
/// Bind to a <see cref="ViewModels.Journal.JournalImportReportViewModel"/>. The panel is pure view —
/// every action is a command on that model, so each one lands on the undo stack.
/// </para>
/// </summary>
public partial class JournalImportReportPanel : UserControl
{
    /// <summary>Creates the panel.</summary>
    public JournalImportReportPanel() => InitializeComponent();
}
