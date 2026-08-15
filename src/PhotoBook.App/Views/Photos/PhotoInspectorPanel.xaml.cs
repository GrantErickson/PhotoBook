using System.Windows.Controls;

namespace PhotoBook.App.Views.Photos;

/// <summary>
/// The three inspector sections doc 09 §2 asks for that edit the photo itself — <b>Date</b> (§2.1),
/// <b>Focus</b> (§2.2) and <b>Adjust</b> (§2.4) — as one drop-in panel. Bind its
/// <c>DataContext</c> to a <see cref="ViewModels.Photos.PhotoInspectorViewModel"/> and place it in
/// the Photos tab's inspector rail; Tier and Exclude already live there and are not repeated.
/// </summary>
public partial class PhotoInspectorPanel : UserControl
{
    /// <summary>Creates the panel.</summary>
    public PhotoInspectorPanel() => InitializeComponent();
}
