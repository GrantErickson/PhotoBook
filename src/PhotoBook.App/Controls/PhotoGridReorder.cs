using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PhotoBook.App.Services;
using PhotoBook.App.ViewModels;
using PhotoBook.App.ViewModels.Photos;

namespace PhotoBook.App.Controls;

/// <summary>
/// Drag-to-reorder for the Photos grid (doc 09 §2.5, R6), as an attached behaviour so the existing
/// grid gains it with one line of XAML and no restructuring:
/// <code>&lt;ListBox pb:PhotoGridReorder.Controller="{Binding Reorder}" … /&gt;</code>
///
/// <para>
/// The gesture is deliberately conservative. A drag starts only after the system drag threshold, so
/// clicking to select still works; the insertion point is drawn as an accent bar between two tiles
/// rather than by shuffling the grid under the cursor; nothing is committed until pointer-up; and a
/// drop across a day boundary is refused with the no-drop cursor and a hint pointing at
/// <em>Change date…</em>, because moving a photo to another day is a re-date with consequences a drag
/// must not imply (doc 09 §2.5).
/// </para>
///
/// <para>
/// The payload is a plain <see cref="PhotoItemViewModel"/> array under
/// <see cref="DataFormat"/>, so other drop targets in the app — the page canvas's Slots, the bins —
/// can accept the very same drag without this behaviour knowing about them.
/// </para>
/// </summary>
public static class PhotoGridReorder
{
    /// <summary>The clipboard format the dragged photos travel under.</summary>
    public const string DataFormat = "PhotoBook.PhotoItems";

    private static readonly ConditionalWeakTable<ItemsControl, DragState> States = new();

    /// <summary>The controller that validates and commits drops; setting it enables the behaviour.</summary>
    public static readonly DependencyProperty ControllerProperty = DependencyProperty.RegisterAttached(
        "Controller", typeof(PhotoReorderController), typeof(PhotoGridReorder),
        new PropertyMetadata(null, OnControllerChanged));

    /// <summary>Gets the reorder controller attached to a grid.</summary>
    /// <param name="element">The items control.</param>
    public static PhotoReorderController? GetController(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (PhotoReorderController?)element.GetValue(ControllerProperty);
    }

    /// <summary>Attaches (or detaches) the reorder controller.</summary>
    /// <param name="element">The items control.</param>
    /// <param name="value">The controller, or null to switch the behaviour off.</param>
    public static void SetController(DependencyObject element, PhotoReorderController? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ControllerProperty, value);
    }

    private static void OnControllerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl items)
        {
            return;
        }

        items.PreviewMouseLeftButtonDown -= OnMouseDown;
        items.PreviewMouseMove -= OnMouseMove;
        items.DragOver -= OnDragOver;
        items.DragLeave -= OnDragLeave;
        items.Drop -= OnDrop;
        items.QueryContinueDrag -= OnQueryContinueDrag;

        if (e.NewValue is not PhotoReorderController)
        {
            States.Remove(items);
            return;
        }

        items.AllowDrop = true;
        items.PreviewMouseLeftButtonDown += OnMouseDown;
        items.PreviewMouseMove += OnMouseMove;
        items.DragOver += OnDragOver;
        items.DragLeave += OnDragLeave;
        items.Drop += OnDrop;
        items.QueryContinueDrag += OnQueryContinueDrag;
    }

    // ================================================================= drag source

    private static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ItemsControl items)
        {
            return;
        }

        var state = States.GetValue(items, _ => new DragState());
        state.Origin = e.GetPosition(items);
        state.Candidate = ItemAt(items, e.OriginalSource as DependencyObject);
    }

    private static void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not ItemsControl items ||
            !States.TryGetValue(items, out var state) ||
            state.Candidate is null ||
            state.IsDragging ||
            e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var point = e.GetPosition(items);
        if (Math.Abs(point.X - state.Origin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - state.Origin.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var dragged = Selection(items, state.Candidate);
        var data = new DataObject(DataFormat, dragged);
        data.SetData(typeof(PhotoItemViewModel[]), dragged);

        // The same drag must also be droppable on a page Slot (doc 09 §3.2 lists "any Photos-tab
        // thumbnail" as a drag source), so it carries the editor's standard payload as well. This
        // behaviour only ever *reads* its own format, so the two never fight over a drop.
        var payload = new PhotoDragPayload(dragged[0].Photo.Id, PhotoDragOrigin.PhotoGrid);
        data.SetData(PhotoDragPayload.DataFormat, payload);
        data.SetData(DataFormats.UnicodeText, dragged[0].Photo.Id);

        state.IsDragging = true;
        try
        {
            DragDrop.DoDragDrop(items, data, DragDropEffects.Move);
        }
        finally
        {
            state.IsDragging = false;
            state.Candidate = null;
            Clear(items, state);
            GetController(items)?.ClearHint();
        }
    }

    /// <summary>
    /// The dragged block: the whole selection when the grabbed tile is part of it (multi-select drags
    /// move as a block, preserving internal order), otherwise just the grabbed tile.
    /// </summary>
    private static PhotoItemViewModel[] Selection(ItemsControl items, PhotoItemViewModel grabbed)
    {
        if (items is not ListBox { SelectionMode: not SelectionMode.Single } list ||
            !list.SelectedItems.Contains(grabbed))
        {
            return [grabbed];
        }

        var selected = list.SelectedItems.OfType<PhotoItemViewModel>().ToHashSet();
        return [.. Ordered(items).Where(selected.Contains)];
    }

    // ================================================================= drop target

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        if (sender is not ItemsControl items || GetController(items) is not { } controller)
        {
            return;
        }

        var dragged = Payload(e);
        if (dragged.Length == 0)
        {
            return;
        }

        var state = States.GetValue(items, _ => new DragState());
        var (_, container, after, target) = Position(items, e.GetPosition(items));

        var allowed = controller.CanDrop([.. dragged.Select(d => d.Photo)], target?.Photo);
        e.Effects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;

        if (allowed && container is not null)
        {
            Show(items, state, container, after);
        }
        else
        {
            Clear(items, state);
        }
    }

    private static void OnDragLeave(object sender, DragEventArgs e)
    {
        if (sender is ItemsControl items && States.TryGetValue(items, out var state))
        {
            Clear(items, state);
            GetController(items)?.ClearHint();
        }
    }

    private static void OnQueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (e.EscapePressed && sender is ItemsControl items && States.TryGetValue(items, out var state))
        {
            Clear(items, state);
            GetController(items)?.ClearHint();
        }
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        if (sender is not ItemsControl items || GetController(items) is not { } controller)
        {
            return;
        }

        var dragged = Payload(e);
        if (dragged.Length == 0)
        {
            return;
        }

        var state = States.GetValue(items, _ => new DragState());
        Clear(items, state);

        var (index, _, _, target) = Position(items, e.GetPosition(items));
        if (!controller.CanDrop([.. dragged.Select(d => d.Photo)], target?.Photo))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var ordered = Ordered(items).Select(i => i.Photo).ToList();
        controller.Drop(ordered, [.. dragged.Select(d => d.Photo)], index);

        foreach (var item in dragged)
        {
            item.Refresh();
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    // ================================================================= geometry

    /// <summary>
    /// Where the cursor says the block should land: the index in display order, the tile the marker
    /// hangs off, which side of it, and the tile under the cursor for the same-day check.
    /// </summary>
    private static (int Index, FrameworkElement? Container, bool After, PhotoItemViewModel? Target) Position(
        ItemsControl items, Point point)
    {
        var list = Ordered(items);
        if (list.Count == 0)
        {
            return (0, null, false, null);
        }

        var hit = items.InputHitTest(point) as DependencyObject;
        var item = ItemAt(items, hit);
        if (item is null)
        {
            // Past the last tile: append, and validate against the last photo's day.
            var last = ContainerOf(items, list[^1]);
            return (list.Count, last, true, list[^1]);
        }

        var container = ContainerOf(items, item);
        var index = list.IndexOf(item);
        var after = false;

        if (container is not null)
        {
            var local = items.TranslatePoint(point, container);
            after = local.X > container.ActualWidth / 2;
        }

        return (after ? index + 1 : index, container, after, item);
    }

    private static List<PhotoItemViewModel> Ordered(ItemsControl items) =>
        [.. items.Items.OfType<PhotoItemViewModel>()];

    private static PhotoItemViewModel? ItemAt(ItemsControl items, DependencyObject? source)
    {
        if (source is null)
        {
            return null;
        }

        var container = ItemsControl.ContainerFromElement(items, source) as FrameworkElement;
        return container?.DataContext as PhotoItemViewModel;
    }

    private static FrameworkElement? ContainerOf(ItemsControl items, PhotoItemViewModel item) =>
        items.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;

    private static void Show(ItemsControl items, DragState state, FrameworkElement container, bool after)
    {
        var layer = AdornerLayer.GetAdornerLayer(items);
        if (layer is null)
        {
            return;
        }

        if (state.Adorner is null)
        {
            state.Adorner = new InsertionAdorner(items);
            layer.Add(state.Adorner);
        }

        try
        {
            var origin = container.TransformToAncestor(items).Transform(new Point(0, 0));
            var x = origin.X + (after ? container.ActualWidth : 0);
            state.Adorner.Marker = new Rect(x - 1.5, origin.Y + 4, 3, Math.Max(8, container.ActualHeight - 8));
        }
        catch (InvalidOperationException)
        {
            // The container was virtualized away mid-drag; the marker simply skips this frame.
        }
    }

    private static void Clear(ItemsControl items, DragState state)
    {
        if (state.Adorner is null)
        {
            return;
        }

        AdornerLayer.GetAdornerLayer(items)?.Remove(state.Adorner);
        state.Adorner = null;
    }

    private static PhotoItemViewModel[] Payload(DragEventArgs e) =>
        e.Data?.GetDataPresent(DataFormat) == true
            ? e.Data.GetData(DataFormat) as PhotoItemViewModel[] ?? []
            : [];

    private sealed class DragState
    {
        public Point Origin { get; set; }

        public PhotoItemViewModel? Candidate { get; set; }

        public bool IsDragging { get; set; }

        public InsertionAdorner? Adorner { get; set; }
    }
}

/// <summary>The accent bar drawn between two tiles to show where a dragged block will land.</summary>
public sealed class InsertionAdorner : Adorner
{
    private Rect _marker;

    /// <param name="adorned">The items control being adorned.</param>
    public InsertionAdorner(UIElement adorned) : base(adorned) => IsHitTestVisible = false;

    /// <summary>The bar's rectangle, in the adorned element's coordinates.</summary>
    public Rect Marker
    {
        get => _marker;
        set
        {
            if (_marker != value)
            {
                _marker = value;
                InvalidateVisual();
            }
        }
    }

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (_marker.Width <= 0 || _marker.Height <= 0)
        {
            return;
        }

        var brush = TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;
        drawingContext.DrawRoundedRectangle(brush, null, _marker, 1.5, 1.5);

        // Small caps top and bottom, so the bar reads as an insertion point rather than a border.
        var cap = new Size(7, 3);
        drawingContext.DrawRoundedRectangle(
            brush, null,
            new Rect(_marker.X - ((cap.Width - _marker.Width) / 2), _marker.Y - cap.Height, cap.Width, cap.Height),
            1.5, 1.5);
        drawingContext.DrawRoundedRectangle(
            brush, null,
            new Rect(_marker.X - ((cap.Width - _marker.Width) / 2), _marker.Bottom, cap.Width, cap.Height),
            1.5, 1.5);
    }
}
