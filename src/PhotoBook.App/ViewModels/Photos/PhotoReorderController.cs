using CommunityToolkit.Mvvm.ComponentModel;
using PhotoBook.App.Services;
using PhotoBook.Core.Model;

namespace PhotoBook.App.ViewModels.Photos;

/// <summary>
/// The model side of grid reorder (doc 09 §2.5, R6). The grid's order <b>is</b> chronological order —
/// the one axis the layout engine reads — so dragging a thumbnail between two neighbours rewrites its
/// <c>takenTime</c> to their midpoint rather than inventing a second, hidden sort key that the engine
/// would have to learn about.
///
/// <para>
/// Drops are accepted only within one calendar day. Crossing a day boundary is a <em>re-date</em>,
/// with consequences (Day Group membership, Chapter membership, the Outside-book tray) that a drag
/// gesture must not imply, so the drop is refused and <see cref="Hint"/> points at
/// <em>Change date…</em>. Reordering never moves a photo between pages: placed photos keep their
/// Placements and the new order feeds the <em>next</em> engine run.
/// </para>
/// </summary>
public sealed partial class PhotoReorderController : ObservableObject
{
    private readonly PhotoEditor _editor;

    /// <param name="editor">The undoable edit service.</param>
    public PhotoReorderController(PhotoEditor editor) => _editor = editor;

    /// <summary>Raised after a successful drop, so the grid can re-sort and the status line update.</summary>
    public event Action<ReorderOutcome>? Reordered;

    /// <summary>
    /// Why the current drag cannot be dropped where it is, or empty. Bound to the drag banner so the
    /// no-drop cursor is never the only explanation.
    /// </summary>
    [ObservableProperty]
    private string _hint = string.Empty;

    /// <summary>True while a refusal hint is showing.</summary>
    public bool HasHint => Hint.Length > 0;

    /// <summary>True when the block may be dropped at this position, updating <see cref="Hint"/>.</summary>
    /// <param name="dragged">The photos being dragged.</param>
    /// <param name="target">The photo the cursor is over, or null for the end of the grid.</param>
    public bool CanDrop(IReadOnlyList<Photo> dragged, Photo? target)
    {
        if (dragged.Count == 0)
        {
            Hint = string.Empty;
            return false;
        }

        var day = dragged[0].TakenOn;
        if (dragged.Any(p => p.TakenOn != day))
        {
            Hint = "Those photos are from different days, so they cannot be reordered as a block.";
            return false;
        }

        if (target is not null && target.TakenOn != day)
        {
            Hint = $"Photos only reorder within one day. To move this to {target.TakenOn:MMMM d}, " +
                   "use Change date… — that is a re-date, and it can move the photo to another month.";
            return false;
        }

        Hint = string.Empty;
        return true;
    }

    /// <summary>Clears the refusal hint when the drag ends or leaves the grid.</summary>
    public void ClearHint() => Hint = string.Empty;

    /// <summary>
    /// Commits a drop: one undoable entry, or a refusal. <paramref name="insertIndex"/> is an index
    /// into <paramref name="ordered"/> — the position the block should occupy — as the behaviour
    /// computed it from the insertion marker.
    /// </summary>
    /// <param name="ordered">Every photo in the grid, in display (chronological) order.</param>
    /// <param name="dragged">The dragged photos, in their existing relative order.</param>
    /// <param name="insertIndex">Where the block lands, 0..Count.</param>
    public ReorderOutcome Drop(IReadOnlyList<Photo> ordered, IReadOnlyList<Photo> dragged, int insertIndex)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(dragged);

        Hint = string.Empty;
        if (dragged.Count == 0)
        {
            return new ReorderOutcome(false, 0, false, string.Empty);
        }

        var day = dragged[0].TakenOn;
        if (dragged.Any(p => p.TakenOn != day))
        {
            return new ReorderOutcome(
                false, 0, false, "Those photos are from different days, so they cannot be reordered as a block.");
        }

        // The engine's axis is the whole day, not the visible page of the grid, so the day's photos
        // are gathered from the grid's own order — which is that same axis.
        var dayPhotos = ordered.Where(p => p.TakenOn == day).ToList();
        if (dayPhotos.Count == 0)
        {
            return new ReorderOutcome(false, 0, false, string.Empty);
        }

        var firstOfDay = ordered.Count == dayPhotos.Count ? 0 : IndexOfFirst(ordered, day);
        var dayIndex = Math.Clamp(insertIndex - firstOfDay, 0, dayPhotos.Count);

        var outcome = _editor.ReorderWithinDay(dayPhotos, dragged, dayIndex);
        if (outcome.Applied)
        {
            Reordered?.Invoke(outcome);
        }

        return outcome;
    }

    private static int IndexOfFirst(IReadOnlyList<Photo> ordered, DateOnly day)
    {
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].TakenOn == day)
            {
                return i;
            }
        }

        return 0;
    }

    partial void OnHintChanged(string value) => OnPropertyChanged(nameof(HasHint));
}
