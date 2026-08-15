using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PhotoBook.App.Services;
using PhotoBook.App.ViewModels.Pages;

namespace PhotoBook.App.Views.Pages;

/// <summary>
/// The dockable bin panel (doc 09 §3.5). The view model owns the three bins and the persisted dock
/// preference; this file owns only the three things that need real input handling:
/// <list type="number">
/// <item><description>starting a drag from a tile, with the shared
/// <see cref="PhotoDragPayload"/> the page canvas reads;</description></item>
/// <item><description>accepting a photo dragged out of a slot, which unplaces it (§3.2);</description></item>
/// <item><description>the resize grip, which writes straight into the persisted size.</description></item>
/// </list>
/// </summary>
public partial class BinsPanel : UserControl
{
    private Point _pressPoint;
    private BinItemViewModel? _pressItem;
    private bool _dragging;

    private Point _gripOrigin;
    private double _gripStartSize;
    private bool _resizing;

    /// <summary>Creates the panel. The view model arrives as the DataContext.</summary>
    public BinsPanel() => InitializeComponent();

    private BinsViewModel? Model => DataContext as BinsViewModel;

    // ------------------------------------------------------------------ drag out

    private void OnItemsPressed(object sender, MouseButtonEventArgs e)
    {
        _pressPoint = e.GetPosition(this);
        _pressItem = ItemUnder(e.OriginalSource as DependencyObject);
        _dragging = false;
    }

    private void OnItemsMoved(object sender, MouseEventArgs e)
    {
        if (_dragging || _pressItem is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var item = _pressItem;
        _pressItem = null;
        _dragging = true;

        try
        {
            // Copy is offered as well as Move so a drop onto a target that only accepts a copy still
            // shows a usable cursor; the page decides which it performs.
            DragDrop.DoDragDrop(this, item.ToDragPayload().ToDataObject(), DragDropEffects.Move);
        }
        finally
        {
            _dragging = false;
        }
    }

    private static BinItemViewModel? ItemUnder(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: BinItemViewModel item })
            {
                return item;
            }

            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }

    // ------------------------------------------------------------------ drop in

    private void OnPanelDragOver(object sender, DragEventArgs e)
    {
        var accepted = CanAccept(e.Data);
        e.Effects = accepted ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
        DropRing.Visibility = accepted ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPanelDragLeave(object sender, DragEventArgs e) =>
        DropRing.Visibility = Visibility.Collapsed;

    private void OnPanelDrop(object sender, DragEventArgs e)
    {
        DropRing.Visibility = Visibility.Collapsed;

        var payload = PhotoDragPayload.From(e.Data);
        if (CanAccept(e.Data) && Model?.Unplace(payload) == true)
        {
            e.Effects = DragDropEffects.Move;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    /// <summary>
    /// Only a photo that is currently in a slot can be dropped here — dropping a bin item back into
    /// its own bin would be a no-op, so it shows the no-drop cursor instead of pretending to work.
    /// </summary>
    private static bool CanAccept(IDataObject? data) =>
        PhotoDragPayload.From(data) is { IsPlaced: true, Origin: PhotoDragOrigin.Slot };

    // ------------------------------------------------------------------ resize grip

    private void OnGripPressed(object sender, MouseButtonEventArgs e)
    {
        if (Model is not { } model)
        {
            return;
        }

        // Relative to the window, not to the panel: the panel's own edge moves as it resizes, which
        // would feed back into the delta and make the drag run away.
        _gripOrigin = e.GetPosition(null);
        _gripStartSize = model.IsBottomDock
            ? model.Settings.BinBottomHeight
            : model.Settings.BinSideWidth;
        _resizing = true;
        Grip.CaptureMouse();
        e.Handled = true;
    }

    private void OnGripMoved(object sender, MouseEventArgs e)
    {
        if (!_resizing || Model is not { } model)
        {
            return;
        }

        var point = e.GetPosition(null);
        if (model.IsBottomDock)
        {
            // The grip is the panel's top edge, so dragging up (negative dy) makes it taller.
            model.Settings.BinBottomHeight = Math.Clamp(
                _gripStartSize - (point.Y - _gripOrigin.Y),
                EditorSettings.MinBinBottomHeight,
                EditorSettings.MaxBinBottomHeight);
        }
        else
        {
            var delta = point.X - _gripOrigin.X;
            model.Settings.BinSideWidth = Math.Clamp(
                model.IsLeftDock ? _gripStartSize + delta : _gripStartSize - delta,
                EditorSettings.MinBinSideWidth,
                EditorSettings.MaxBinSideWidth);
        }
    }

    private void OnGripReleased(object sender, MouseButtonEventArgs e)
    {
        if (!_resizing)
        {
            return;
        }

        _resizing = false;
        Grip.ReleaseMouseCapture();
        e.Handled = true;
    }
}
