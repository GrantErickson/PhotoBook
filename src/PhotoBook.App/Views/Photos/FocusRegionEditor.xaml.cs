using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PhotoBook.App.Controls;
using PhotoBook.App.ViewModels.Photos;

namespace PhotoBook.App.Views.Photos;

/// <summary>
/// The Focus Region editor (doc 09 §2.2, R25). The surface reports gestures; this file turns them
/// into the view model's undoable commits. Nothing is written while the pointer is down — the surface
/// draws the in-flight rect itself — so a whole drag arrives here once, at pointer-up, and becomes a
/// single undo entry that already contains the re-crop of every page using the photo.
/// </summary>
public partial class FocusRegionEditor : UserControl
{
    /// <summary>Creates the editor.</summary>
    public FocusRegionEditor()
    {
        InitializeComponent();

        Surface.RegionDrawn += (_, e) => Model?.AddRegion(e.Rect);
        Surface.RegionEditCompleted += (_, e) =>
            Model?.CommitRegionRect(e.Region, e.StartRect, e.Rect, e.Canceled);
        Surface.DeleteRequested += (_, _) => Model?.DeleteSelectedCommand.Execute(null);

        // The weight slider follows the same one-gesture-one-entry rule as the Adjust panel.
        WeightSlider.AddHandler(
            Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => Model?.BeginWeightDrag()), true);
        WeightSlider.AddHandler(
            Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => Model?.EndWeightDrag()), true);

        Loaded += (_, _) => Surface.Focus();
    }

    private FocusRegionEditorViewModel? Model => DataContext as FocusRegionEditorViewModel;
}
