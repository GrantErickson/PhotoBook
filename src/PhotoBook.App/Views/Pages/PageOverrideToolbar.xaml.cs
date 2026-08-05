using System.Windows.Controls;

namespace PhotoBook.App.Views.Pages;

/// <summary>
/// The layout-override toolbar of doc 09 §3.7: <em>Add photo slot</em>, <em>Add text slot</em>,
/// <em>Delete slot</em>, <em>Bring forward/backward</em>, the text-role switch and
/// <em>Revert to template</em>, with the <em>Detached</em> and <em>Pinned</em> chips that say what
/// the first edit did to the page.
/// <para>
/// Its header strip carries the way out — the mode's name, the <c>Esc</c> keycap and a primary
/// <em>Done</em> — on its own row above the tools, because a mode whose exit is the last button of a
/// crowded line is a mode people get stuck in.
/// </para>
/// <para>
/// Set <see cref="System.Windows.FrameworkElement.DataContext"/> to a
/// <see cref="ViewModels.Pages.PageOverrideViewModel"/>; the toolbar hides itself when the mode is
/// off. Pair it with a <see cref="LayoutOverrideSurface"/> over the page canvas.
/// </para>
/// </summary>
public partial class PageOverrideToolbar : UserControl
{
    /// <summary>Creates the toolbar.</summary>
    public PageOverrideToolbar() => InitializeComponent();
}
