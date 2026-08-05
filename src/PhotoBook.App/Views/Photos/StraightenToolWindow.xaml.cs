using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PhotoBook.App.Controls;
using PhotoBook.App.ViewModels.Photos;

namespace PhotoBook.App.Views.Photos;

/// <summary>
/// Hosts the straightening tool at a size worth aiming in. Everything it changes is already committed
/// and undoable by the time the window closes — there is no OK/Cancel, because "Cancel" over a series
/// of undoable edits would be a second, weaker undo model (the same reasoning as the Focus Region
/// editor).
///
/// <para>
/// The code-behind owns exactly two things the view model cannot: turning the surface's line events
/// into view-model calls, and opening an undo gesture at slider thumb-press so a whole drag folds into
/// one entry (doc 09 §4). A traced line is a single edit already and needs no gesture.
/// </para>
/// </summary>
public partial class StraightenToolWindow : Window
{
    private readonly StraightenToolViewModel _model;

    /// <param name="model">The tool's view model, already pointed at a photo.</param>
    public StraightenToolWindow(StraightenToolViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        InitializeComponent();
        _model = model;
        DataContext = model;

        Surface.LineChanged += OnLineChanged;
        Surface.LineCommitted += OnLineCommitted;
        Surface.LineCanceled += OnLineCanceled;

        // handledEventsToo: Slider's own class handler runs first, so without this the gesture would
        // never open.
        AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _model.BeginSliderGesture()), true);
        AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => _model.EndSliderGesture()), true);

        Closed += (_, _) => _model.EndSliderGesture();
    }

    private void OnLineChanged(object? sender, StraightenLineEventArgs e) =>
        _model.PreviewLine(e.Delta, e.IsUsable);

    private void OnLineCommitted(object? sender, StraightenLineEventArgs e) =>
        _model.ApplyLine(e.Delta, e.Reference);

    private void OnLineCanceled(object? sender, EventArgs e) => _model.CancelLine();

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);

        // Esc leaves the tool, matching every other mode in the editor (doc 09 §5).
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
