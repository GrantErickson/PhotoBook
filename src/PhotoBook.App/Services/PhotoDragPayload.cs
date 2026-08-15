using System.Windows;

namespace PhotoBook.App.Services;

/// <summary>Where a dragged photo came from — it decides what the drop must do (doc 09 §3.2).</summary>
public enum PhotoDragOrigin
{
    /// <summary>The Unplaced bin: the photo is on no page, so a drop is a plain place.</summary>
    UnplacedBin,

    /// <summary>
    /// The Upcoming bin: the photo is placed on a <em>later</em> page. A drop pulls it forward and
    /// vacates that later slot, leaving an amber hole (doc 09 §3.5).
    /// </summary>
    UpcomingBin,

    /// <summary>The Outside-book tray: the photo's date is outside the book year (R6).</summary>
    OutsideTray,

    /// <summary>A slot on the page being edited: a move, or a swap when the target is occupied.</summary>
    Slot,

    /// <summary>A thumbnail in the Photos tab grid.</summary>
    PhotoGrid,
}

/// <summary>
/// The payload every photo drag in the editor carries (doc 09 §3.2). It names the photo <em>and</em>
/// where it came from, because the drop rules differ: a bin drop places, a slot drop swaps, and an
/// Upcoming drop additionally vacates the source slot on the later page.
/// <para>
/// Put it on the clipboard with <see cref="ToDataObject"/> (which also publishes the photo id as
/// plain text, so a drag into another app is not silently empty) and read it back with
/// <see cref="From(IDataObject?)"/>.
/// </para>
/// </summary>
/// <example>
/// <code>
/// // source
/// canvas.BeginDragDrop(new PhotoDragPayload(photo.Id, PhotoDragOrigin.Slot, page.Id, slotId, number));
/// // target
/// if (PhotoDragPayload.From(e.Data) is { } drag) { … }
/// </code>
/// </example>
public sealed class PhotoDragPayload
{
    /// <summary>The clipboard format name; unique to PhotoBook so no other app's drag is mistaken for one.</summary>
    public const string DataFormat = "PhotoBook.PhotoDrag";

    /// <param name="photoId">The <see cref="PhotoBook.Core.Model.Photo.Id"/> being dragged.</param>
    /// <param name="origin">Where the drag started.</param>
    /// <param name="sourcePageId">The page the photo currently sits on, for a slot or Upcoming drag.</param>
    /// <param name="sourceSlotId">The slot the photo currently sits in, for a slot or Upcoming drag.</param>
    /// <param name="sourcePageNumber">That page's number in the chapter, for messages and badges.</param>
    public PhotoDragPayload(
        string photoId,
        PhotoDragOrigin origin,
        string? sourcePageId = null,
        string? sourceSlotId = null,
        int? sourcePageNumber = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(photoId);

        PhotoId = photoId;
        Origin = origin;
        SourcePageId = sourcePageId;
        SourceSlotId = sourceSlotId;
        SourcePageNumber = sourcePageNumber;
    }

    /// <summary>The photo being dragged.</summary>
    public string PhotoId { get; }

    /// <summary>Where the drag started.</summary>
    public PhotoDragOrigin Origin { get; }

    /// <summary>The page the photo is currently placed on, or null when it is unplaced.</summary>
    public string? SourcePageId { get; }

    /// <summary>The slot the photo is currently placed in, or null when it is unplaced.</summary>
    public string? SourceSlotId { get; }

    /// <summary>The source page's number within the chapter, for the "p. 9" badge and toasts.</summary>
    public int? SourcePageNumber { get; }

    /// <summary>True when the drop must also vacate a slot on another page (Upcoming pull, doc 09 §3.5).</summary>
    public bool IsPlaced => SourcePageId is not null && SourceSlotId is not null;

    /// <summary>True when the drag started in one of the three bins rather than on the page.</summary>
    public bool IsFromBin =>
        Origin is PhotoDragOrigin.UnplacedBin or PhotoDragOrigin.UpcomingBin or PhotoDragOrigin.OutsideTray;

    /// <summary>Wraps the payload for <c>DragDrop.DoDragDrop</c>, with the photo id also as text.</summary>
    public DataObject ToDataObject()
    {
        var data = new DataObject(DataFormat, this);
        data.SetData(DataFormats.UnicodeText, PhotoId);
        return data;
    }

    /// <summary>Reads a payload back out of a drop, or null when the drag is not one of ours.</summary>
    public static PhotoDragPayload? From(IDataObject? data) =>
        data is not null && data.GetDataPresent(DataFormat)
            ? data.GetData(DataFormat) as PhotoDragPayload
            : null;
}
