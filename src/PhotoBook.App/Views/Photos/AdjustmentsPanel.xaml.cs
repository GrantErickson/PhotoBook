using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PhotoBook.App.ViewModels.Photos;

namespace PhotoBook.App.Views.Photos;

/// <summary>
/// The inspector's <b>Adjust</b> section (doc 09 §2.4, R11). The view's only job beyond layout is the
/// undo gesture: a slider drag opens an <see cref="Services.UndoStack"/> gesture at thumb-press and
/// closes it at thumb-release, which is what turns a hundred pointer frames into <b>one</b> undo
/// entry (doc 09 §4). Double-clicking a track resets that parameter, the behaviour the palette's
/// bipolar slider was designed for.
/// </summary>
public partial class AdjustmentsPanel : UserControl
{
    private IDisposable? _gesture;

    /// <summary>Creates the panel.</summary>
    public AdjustmentsPanel()
    {
        InitializeComponent();

        // handledEventsToo: Slider's own class handler runs first; without this the gesture would
        // never open on the sliders that consume the event.
        AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnThumbDragStarted), true);
        AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnThumbDragCompleted), true);
        Unloaded += (_, _) => EndGesture();
    }

    private AdjustmentsViewModel? Model => DataContext as AdjustmentsViewModel;

    private void OnThumbDragStarted(object sender, DragStartedEventArgs e)
    {
        EndGesture();
        if (Model is { } model && SliderModel(e.OriginalSource) is { } slider)
        {
            _gesture = model.BeginDrag(slider);
        }
    }

    private void OnThumbDragCompleted(object sender, DragCompletedEventArgs e) => EndGesture();

    private void OnSliderDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider { DataContext: AdjustmentSliderViewModel slider } && slider.ResetCommand.CanExecute(null))
        {
            slider.ResetCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void EndGesture()
    {
        _gesture?.Dispose();
        _gesture = null;
    }

    /// <summary>Walks up from the thumb to the slider that owns it, and takes its row's view model.</summary>
    private static AdjustmentSliderViewModel? SliderModel(object? source)
    {
        var node = source as DependencyObject;
        while (node is not null)
        {
            if (node is Slider { DataContext: AdjustmentSliderViewModel model })
            {
                return model;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }
}
