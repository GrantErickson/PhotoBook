using PhotoBook.Core.Model;
using PhotoBook.Engine;

namespace PhotoBook.App.Services;

/// <summary>
/// The small undoable edits the bins and the template picker are built from (doc 09 §3.4, §3.5,
/// §3.9). Every one of them captures the exact prior state — including a placement's index in the
/// page's list — so <c>Ctrl+Z</c> restores the model byte for byte rather than approximately.
/// <para>
/// Nothing here executes anything: each factory returns a command for the caller to run through
/// <see cref="UndoStack"/>, alone or inside a <see cref="CompositeCommand"/>.
/// </para>
/// </summary>
public static class BinPlacementCommands
{
    /// <summary>
    /// Which side of the spread a page falls on. Even page numbers are left-hand pages (doc 07),
    /// which is what decides where smart crop keeps a face clear of the gutter.
    /// </summary>
    /// <param name="pageNumber">The page's 1-based number in the chapter.</param>
    public static PageSide SideOf(int pageNumber) => pageNumber % 2 == 0 ? PageSide.Left : PageSide.Right;

    /// <summary>The book's trim size in inches, falling back to the kernel §3 default.</summary>
    public static (double WidthIn, double HeightIn) TrimSizeOf(Book? book)
    {
        var spec = book is null ? null : BuiltInPrintProfiles.Generic.FindPageSize(book.PageSize);
        return spec is null
            ? (PageGeometry.TrimWidthIn, PageGeometry.TrimHeightIn)
            : (spec.TrimWidthIn, spec.TrimHeightIn);
    }

    /// <summary>
    /// A fresh smart crop for this photo in this slot — doc 09 §3.2's rule that "crops never travel
    /// with the photo, because a crop is a property of the photo-in-this-slot pairing". The slot is
    /// taken from the page's <em>effective</em> (mirrored where applicable) template.
    /// </summary>
    /// <param name="session">The open project.</param>
    /// <param name="page">The destination page.</param>
    /// <param name="pageNumber">Its 1-based number in the chapter.</param>
    /// <param name="photo">The photo being placed.</param>
    /// <param name="slotId">The destination slot.</param>
    public static CropState SmartCropFor(
        ProjectSession session, Page page, int pageNumber, Photo photo, string slotId)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(photo);

        var slot = page.ResolveTemplate(session.FindTemplate)?.FindSlot(slotId);
        return slot is null ? CropState.Default : SmartCropFor(session, pageNumber, photo, slot);
    }

    /// <summary>
    /// The same fresh smart crop for a slot the caller already has in hand — the template picker
    /// works against the <em>candidate</em> template's slots, which the page does not yet reference.
    /// </summary>
    /// <param name="session">The open project.</param>
    /// <param name="pageNumber">The destination page's 1-based number in the chapter.</param>
    /// <param name="photo">The photo being placed.</param>
    /// <param name="slot">The destination slot, already oriented for the page side.</param>
    public static CropState SmartCropFor(
        ProjectSession session, int pageNumber, Photo photo, ImageSlot slot)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(slot);

        var (trimW, trimH) = TrimSizeOf(session.Book);
        return SmartCrop.Crop(photo, slot, SideOf(pageNumber), trimW, trimH, LayoutWeights.Default).Crop;
    }

    /// <summary>
    /// Vacates a slot: the placement leaves the page and the slot becomes an empty amber one (R14).
    /// Undo puts the placement back at the same index, so the page's slot order is unchanged.
    /// </summary>
    /// <param name="session">The open project.</param>
    /// <param name="page">The page to edit.</param>
    /// <param name="slotId">The slot to empty.</param>
    /// <param name="description">The undo-menu wording.</param>
    /// <returns>The command, or null when the slot is already empty.</returns>
    public static IUndoableCommand? RemovePlacement(
        ProjectSession session, Page page, string slotId, string description)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(page);

        var index = IndexOfSlot(page, slotId);
        if (index < 0)
        {
            return null;
        }

        var placement = page.Placements[index];
        return new EditCommand(
            description,
            () =>
            {
                var at = IndexOfSlot(page, slotId);
                if (at >= 0)
                {
                    page.Placements.RemoveAt(at);
                }

                session.MarkDirty();
            },
            () =>
            {
                page.Placements.Insert(Math.Min(index, page.Placements.Count), placement);
                session.MarkDirty();
            });
    }

    /// <summary>
    /// Puts a photo in a slot. Any placement already in that slot is displaced — the caller decides
    /// where it goes (doc 09 §3.2 sends it to the Unplaced bin) — so pair this with
    /// <see cref="RemovePlacement"/> inside a composite when replacing.
    /// </summary>
    /// <param name="session">The open project.</param>
    /// <param name="page">The page to edit.</param>
    /// <param name="placement">The placement to add, crop included.</param>
    /// <param name="description">The undo-menu wording.</param>
    public static IUndoableCommand AddPlacement(
        ProjectSession session, Page page, Placement placement, string description)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(placement);

        return new EditCommand(
            description,
            () =>
            {
                page.Placements.Add(placement);
                session.MarkDirty();
            },
            () =>
            {
                page.Placements.Remove(placement);
                session.MarkDirty();
            });
    }

    /// <summary>
    /// Pins or unpins a page (kernel §7). Doc 09 §4 requires pin transitions to ride inside the
    /// command that caused them, which is why this is a factory rather than a direct write.
    /// </summary>
    /// <param name="session">The open project.</param>
    /// <param name="page">The page to pin.</param>
    /// <param name="pinned">The new pin state.</param>
    /// <param name="description">The undo-menu wording.</param>
    /// <returns>The command, or null when the page is already in that state.</returns>
    public static IUndoableCommand? SetPinned(
        ProjectSession session, Page page, bool pinned, string description)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(page);

        if (page.Pinned == pinned)
        {
            return null;
        }

        var before = page.Pinned;
        return EditCommand.ForValue(
            description,
            before,
            pinned,
            value =>
            {
                page.Pinned = value;
                session.MarkDirty();
            });
    }

    /// <summary>
    /// Sets or clears the R17 exclusion tombstone. The original file in <c>originals/</c> is never
    /// touched — exclusion only means "this photo is not part of the book", permanently across
    /// re-scans and re-syncs (doc 09 §3.9).
    /// </summary>
    /// <param name="session">The open project.</param>
    /// <param name="photo">The catalog entry to flag.</param>
    /// <param name="excluded">True to exclude, false to restore.</param>
    /// <param name="description">The undo-menu wording.</param>
    public static IUndoableCommand SetExcluded(
        ProjectSession session, Photo photo, bool excluded, string description)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(photo);

        var before = photo.Excluded;
        return EditCommand.ForValue(
            description,
            before,
            excluded,
            value =>
            {
                photo.Excluded = value;
                session.MarkDirty();
            });
    }

    /// <summary>
    /// Swaps a page onto a different library template with a whole new set of placements — the
    /// atomic half of doc 09 §3.4's template switch. The prior template reference, detached
    /// snapshot, mirroring and placement list are all captured, so undo restores a detached page's
    /// hand-built geometry exactly.
    /// </summary>
    /// <param name="session">The open project.</param>
    /// <param name="page">The page to retemplate.</param>
    /// <param name="templateId">The library template to adopt.</param>
    /// <param name="mirrored">Whether the template is mirrored for a left-hand page.</param>
    /// <param name="placements">The new placements, in slot order.</param>
    /// <param name="description">The undo-menu wording.</param>
    public static IUndoableCommand ApplyTemplate(
        ProjectSession session,
        Page page,
        string templateId,
        bool mirrored,
        IReadOnlyList<Placement> placements,
        string description)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(placements);

        var beforeRef = page.TemplateRef;
        var beforeDetached = page.DetachedTemplate;
        var beforeMirrored = page.Mirrored;
        var beforePlacements = page.Placements.ToList();
        var after = placements.ToList();

        return new EditCommand(
            description,
            () =>
            {
                page.TemplateRef = templateId;
                page.DetachedTemplate = null;
                page.Mirrored = mirrored;
                page.Placements = [.. after];
                session.MarkDirty();
            },
            () =>
            {
                page.TemplateRef = beforeRef;
                page.DetachedTemplate = beforeDetached;
                page.Mirrored = beforeMirrored;
                page.Placements = [.. beforePlacements];
                session.MarkDirty();
            });
    }

    private static int IndexOfSlot(Page page, string slotId)
    {
        for (var i = 0; i < page.Placements.Count; i++)
        {
            if (string.Equals(page.Placements[i].SlotId, slotId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
