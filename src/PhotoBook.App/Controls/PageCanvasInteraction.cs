using System.Windows;
using System.Windows.Input;
using PhotoBook.Rendering;
using CorePage = PhotoBook.Core.Model.Page;

namespace PhotoBook.App.Controls;

/// <summary>
/// Which half of a facing pair a <see cref="PageCanvas"/> is drawing. A Spread is a view over two
/// pages, never a stored entity (kernel §4), so this says only how the sheet is drawn: the side
/// facing the spine takes no bleed and no drop shadow, and its gutter is shaded.
/// </summary>
public enum PageSheetSide
{
    /// <summary>A single page: bleed, shadow and edge all the way round.</summary>
    None,

    /// <summary>The left page of the pair — its spine is its right edge.</summary>
    Left,

    /// <summary>The right page of the pair — its spine is its left edge.</summary>
    Right,
}

/// <summary>
/// What <see cref="PageCanvas"/> asks its renderer for: one page, at a pixel size that matches the
/// control's current aspect, cancellable because the user is still dragging.
/// </summary>
/// <param name="Page">The page to draw.</param>
/// <param name="PixelWidth">Buffer width in device pixels.</param>
/// <param name="PixelHeight">Buffer height in device pixels.</param>
/// <param name="Cancellation">Cancelled when a newer render supersedes this one.</param>
public sealed record PageCanvasRenderContext(
    CorePage Page, int PixelWidth, int PixelHeight, CancellationToken Cancellation);

/// <summary>
/// Produces the preview <see cref="PageCanvas"/> displays. Called on a background thread — the UI
/// thread never decodes an image (doc 09 §6) — so it must not touch WPF objects.
/// </summary>
/// <example>
/// <code>
/// canvas.Renderer = ctx => session.RenderPage(chapter, ctx.Page, ctx.PixelWidth, ctx.PixelHeight);
/// </code>
/// </example>
/// <param name="context">The page and buffer size to render.</param>
/// <returns>The rendered preview, or null to leave the canvas showing what it already has.</returns>
public delegate PagePreview? PageCanvasRenderer(PageCanvasRenderContext context);

/// <summary>
/// A pointer event on the canvas, already resolved to a slot and to all three coordinate spaces.
/// </summary>
public class PageCanvasPointerEventArgs : EventArgs
{
    /// <summary>The page under the pointer; null when nothing is loaded.</summary>
    public CorePage? Page { get; init; }

    /// <summary>The slot under the pointer, or null when the pointer is off every slot.</summary>
    public string? SlotId { get; init; }

    /// <summary>True when <see cref="SlotId"/> names a slot with no placement — an amber slot (R14).</summary>
    public bool IsSlotEmpty { get; init; }

    /// <summary>Pointer position in control pixels (device-independent).</summary>
    public Point ControlPoint { get; init; }

    /// <summary>Pointer position in preview-bitmap pixels — the space <c>SlotRects</c> lives in.</summary>
    public Point BitmapPoint { get; init; }

    /// <summary>
    /// Pointer position in normalized page coordinates over the trim box (kernel §3): <c>(0,0)</c> is
    /// the top-left trim corner, <c>(1,1)</c> the bottom-right. <c>NaN</c> when nothing is rendered.
    /// </summary>
    public Point NormalizedPoint { get; init; }

    /// <summary>Modifier keys held — <c>Ctrl</c> forces replace over swap at a drop (doc 09 §3.2).</summary>
    public ModifierKeys Modifiers { get; init; }

    /// <summary>Set to suppress the canvas's own default handling (selection, cursor).</summary>
    public bool Handled { get; set; }
}

/// <summary>A click or double-click on the canvas.</summary>
public sealed class PageCanvasClickEventArgs : PageCanvasPointerEventArgs
{
    /// <summary>Which button was pressed.</summary>
    public MouseButton Button { get; init; }

    /// <summary>1 for a click, 2 for a double-click (crop mode, doc 09 §3.3).</summary>
    public int ClickCount { get; init; }
}

/// <summary>
/// A pointer drag on the canvas, with deltas in the units the crop model wants.
/// </summary>
public sealed class PageCanvasDragEventArgs : PageCanvasPointerEventArgs
{
    /// <summary>The slot the drag started on — the target for the whole gesture.</summary>
    public string? OriginSlotId { get; init; }

    /// <summary>Movement since the previous event, in normalized page units.</summary>
    public Vector NormalizedDelta { get; init; }

    /// <summary>Movement since the drag started, in normalized page units.</summary>
    public Vector TotalNormalizedDelta { get; init; }

    /// <summary>
    /// Movement since the previous event in <see cref="OriginSlotId"/>'s width/height units — exactly
    /// what <c>CropState.OffsetX/OffsetY</c> are measured in (doc 09 §3.3).
    /// </summary>
    public Vector SlotDelta { get; init; }

    /// <summary>Movement since the drag started, in the origin slot's width/height units.</summary>
    public Vector TotalSlotDelta { get; init; }

    /// <summary>True on the completion event when the gesture was aborted (capture lost, <c>Esc</c>).</summary>
    public bool Canceled { get; init; }

    /// <summary>
    /// Set from a <see cref="PageCanvas.DragStarted"/> handler that takes the gesture over itself —
    /// typically by calling <see cref="PageCanvas.BeginDragDrop"/> for a photo move. The canvas then
    /// stops tracking pointer deltas for this gesture.
    /// </summary>
    public bool CancelPointerDrag { get; set; }
}

/// <summary>A wheel notch over the canvas — crop zoom in crop mode, viewport zoom with <c>Ctrl</c>.</summary>
public sealed class PageCanvasWheelEventArgs : PageCanvasPointerEventArgs
{
    /// <summary>Raw wheel delta; 120 per notch.</summary>
    public int Delta { get; init; }

    /// <summary>Notches, signed: <c>+1</c> is one notch away from the user (zoom in).</summary>
    public double Steps { get; init; }
}

/// <summary>A drag-and-drop operation passing over or dropping onto the canvas (doc 09 §3.2).</summary>
public sealed class PageCanvasDropEventArgs : EventArgs
{
    /// <summary>The page being dropped onto.</summary>
    public CorePage? Page { get; init; }

    /// <summary>The slot under the cursor, or null when the cursor is off every slot.</summary>
    public string? SlotId { get; init; }

    /// <summary>True when the target slot has no placement — a plain "Place", never a swap.</summary>
    public bool IsSlotEmpty { get; init; }

    /// <summary>The payload being dragged.</summary>
    public IDataObject? Data { get; init; }

    /// <summary>Cursor position in control pixels.</summary>
    public Point ControlPoint { get; init; }

    /// <summary>Cursor position in normalized page coordinates over the trim box.</summary>
    public Point NormalizedPoint { get; init; }

    /// <summary>Modifier keys held — <c>Ctrl</c> forces replace instead of swap.</summary>
    public ModifierKeys Modifiers { get; init; }

    /// <summary>What the source allows.</summary>
    public DragDropEffects AllowedEffects { get; init; }

    /// <summary>
    /// Set this to the effect the drop would have. <see cref="DragDropEffects.None"/> (the default)
    /// shows the no-drop cursor and suppresses the canvas's drop-target ring.
    /// </summary>
    public DragDropEffects Effects { get; set; }
}
