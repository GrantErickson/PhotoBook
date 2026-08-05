using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    /// <summary>
    /// Turns a click on the preview into the normalized point the white-balance picker needs.
    /// <para>
    /// The image is drawn <c>Stretch="Uniform"</c>, so it is letterboxed inside the element and a click
    /// must be mapped against the <em>drawn</em> rectangle rather than the element — otherwise the
    /// sampled pixel drifts by the size of the bars, which is exactly the error that would make the
    /// eyedropper feel unreliable.
    /// </para>
    /// </summary>
    private void OnPreviewClick(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (Model is not { IsPickingWhiteBalance: true } model ||
            sender is not Image { Source: BitmapSource source } image ||
            source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            return;
        }

        var width = image.ActualWidth;
        var height = image.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var scale = Math.Min(width / source.PixelWidth, height / source.PixelHeight);
        var drawnWidth = source.PixelWidth * scale;
        var drawnHeight = source.PixelHeight * scale;
        var originX = (width - drawnWidth) / 2;
        var originY = (height - drawnHeight) / 2;

        var point = e.GetPosition(image);
        var x = (point.X - originX) / drawnWidth;
        var y = (point.Y - originY) / drawnHeight;
        if (x is < 0 or > 1 || y is < 0 or > 1)
        {
            return;
        }

        model.PickWhiteBalanceAt(x, y);
        e.Handled = true;
    }

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
