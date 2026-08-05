using System.Globalization;
using PhotoBook.Core.Model;

namespace PhotoBook.App.Services;

/// <summary>
/// The model half of layout override mode (doc 09 §3.7, R15): detaching a page onto its own inline
/// template snapshot and then moving, resizing, adding and deleting the containers on it.
/// <para>
/// Everything here is a command factory rather than a direct write, because doc 09 §4 requires the
/// Pinned/Detached transitions to ride inside the command that caused them — one <c>Ctrl+Z</c> must
/// take back the geometry edit <em>and</em> the detach it triggered.
/// </para>
/// </summary>
public static class PageLayoutOverride
{
    /// <summary>Smallest slot the editor allows, per doc 09 §3.7.</summary>
    public const double MinimumSize = 0.05;

    /// <summary>The size of a slot dropped by <em>Add photo slot</em>.</summary>
    public const double NewSlotSize = 0.30;

    /// <summary>The width of a text slot dropped by <em>Add text slot</em>.</summary>
    public const double NewTextSlotWidth = 0.34;

    /// <summary>The height of a text slot dropped by <em>Add text slot</em>.</summary>
    public const double NewTextSlotHeight = 0.16;

    /// <summary>
    /// The template a page actually renders with: its own snapshot when detached, otherwise the
    /// library template mirrored for its side.
    /// </summary>
    public static Template? EffectiveTemplate(Page page, Func<string, Template?> library) =>
        page.ResolveTemplate(library);

    /// <summary>
    /// The first geometry edit Detaches the page (doc 09 §3.7): the library reference is replaced by
    /// an inline snapshot of what is on screen right now, and the page is Pinned — a hand-built
    /// layout is the strongest possible "engine, hands off" signal.
    /// <para>
    /// The snapshot is taken from the <em>effective</em> (already mirrored) template and
    /// <see cref="Page.Mirrored"/> is cleared with it, so the geometry cannot be mirrored twice.
    /// </para>
    /// </summary>
    /// <param name="session">The open project.</param>
    /// <param name="page">The page to detach.</param>
    /// <param name="library">Resolves the library template id.</param>
    /// <returns>The command, or null when the page is already detached.</returns>
    public static IUndoableCommand? Detach(ProjectSession session, Page page, Func<string, Template?> library)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(page);

        if (page.IsDetached)
        {
            return null;
        }

        var effective = page.ResolveTemplate(library);
        if (effective is null)
        {
            return null;
        }

        var snapshot = effective.ToDetachedSnapshot();
        var beforeRef = page.TemplateRef;
        var beforeMirrored = page.Mirrored;
        var beforePinned = page.Pinned;

        return new EditCommand(
            "Detach page layout",
            () =>
            {
                page.DetachedTemplate = ModelClone.CloneTemplate(snapshot);
                page.TemplateRef = null;
                page.Mirrored = false;
                page.Pinned = true;
                session.MarkDirty();
            },
            () =>
            {
                page.DetachedTemplate = null;
                page.TemplateRef = beforeRef;
                page.Mirrored = beforeMirrored;
                page.Pinned = beforePinned;
                session.MarkDirty();
            });
    }

    /// <summary>
    /// <em>Revert to template</em> (doc 09 §3.7): re-attaches the library template the snapshot came
    /// from and clears the Detached chip. The page stays Pinned, because the user is still actively
    /// editing it.
    /// <para>
    /// Photos are re-seated deterministically rather than by a full engine pass: a placement whose
    /// slot still exists keeps its slot and its crop; anything left over fills the remaining slots in
    /// reading order at the default cover crop, and the overflow goes to the Unplaced bin per §3.4.
    /// </para>
    /// </summary>
    /// <param name="session">The open project.</param>
    /// <param name="page">The detached page.</param>
    /// <param name="library">Resolves the library template id.</param>
    /// <returns>The command, or null when the page is not detached or its origin is unknown.</returns>
    public static IUndoableCommand? RevertToTemplate(
        ProjectSession session, Page page, Func<string, Template?> library)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(library);

        if (page.DetachedTemplate is not { BasedOn: { Length: > 0 } basedOn } snapshot)
        {
            return null;
        }

        var target = library(basedOn);
        if (target is null)
        {
            return null;
        }

        var beforeSnapshot = ModelClone.CloneTemplate(snapshot);
        var beforePlacements = page.Placements.Select(p => p with { }).ToList();
        var beforeAssignments = page.JournalAssignments
            .Select(a => a with { EntryIds = new List<string>(a.EntryIds) })
            .ToList();
        var beforeMirrored = page.Mirrored;

        // The library template is authored for a right page; a left page shows it mirrored, which is
        // exactly what Page.Mirrored means. Detaching cleared the flag, so restore what it was.
        var reseated = Reseat(beforePlacements, target);
        var keptTextSlots = target.TextSlots.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var reboundAssignments = beforeAssignments
            .Where(a => keptTextSlots.Contains(a.TextSlotId))
            .Select(a => a with { EntryIds = new List<string>(a.EntryIds) })
            .ToList();

        return new EditCommand(
            "Revert page to template",
            () =>
            {
                page.DetachedTemplate = null;
                page.TemplateRef = target.Id ?? basedOn;
                page.Mirrored = beforeMirrored;
                page.Pinned = true;
                page.Placements = [.. reseated.Select(p => p with { })];
                page.JournalAssignments = [.. reboundAssignments.Select(a => a with { EntryIds = new List<string>(a.EntryIds) })];
                session.MarkDirty();
            },
            () =>
            {
                page.DetachedTemplate = ModelClone.CloneTemplate(beforeSnapshot);
                page.TemplateRef = null;
                page.Mirrored = false;
                page.Placements = [.. beforePlacements.Select(p => p with { })];
                page.JournalAssignments = [.. beforeAssignments.Select(a => a with { EntryIds = new List<string>(a.EntryIds) })];
                session.MarkDirty();
            });
    }

    private static List<Placement> Reseat(IReadOnlyList<Placement> placements, Template target)
    {
        var slots = target.Slots.Select(s => s.Id).ToList();
        var result = new List<Placement>(Math.Min(placements.Count, slots.Count));
        var taken = new HashSet<string>(StringComparer.Ordinal);

        foreach (var placement in placements.Where(p => slots.Contains(p.SlotId, StringComparer.Ordinal)))
        {
            result.Add(placement with { });
            taken.Add(placement.SlotId);
        }

        var spare = new Queue<Placement>(placements.Where(p => !taken.Contains(p.SlotId)));
        foreach (var slotId in slots.Where(id => !taken.Contains(id)))
        {
            if (spare.Count == 0)
            {
                break;
            }

            result.Add(spare.Dequeue() with { SlotId = slotId, Crop = CropState.Default });
        }

        return result;
    }

    /// <summary>
    /// Records a finished move or resize of an image slot as one undo entry. The geometry is already
    /// applied — the surface writes it live so the page re-renders under the pointer — so this is a
    /// <see cref="UndoStack.Push"/>-shaped command.
    /// </summary>
    /// <param name="session">The open project.</param>
    /// <param name="template">The page's inline template snapshot.</param>
    /// <param name="slotId">The slot that moved.</param>
    /// <param name="before">Its rect before the gesture.</param>
    /// <param name="after">Its rect after it.</param>
    /// <param name="description">The undo-menu wording.</param>
    public static IUndoableCommand SetSlotRect(
        ProjectSession session, Template template, string slotId, Rect before, Rect after, string description)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(template);

        return EditCommand.ForValue(
            description,
            before,
            after,
            rect =>
            {
                if (template.FindSlot(slotId) is { } slot)
                {
                    slot.Rect = rect;
                    slot.Aspect = PageGeometry.PhysicalAspect(rect);
                }

                session.MarkDirty();
            });
    }

    /// <summary>Records a finished move or resize of a text slot as one undo entry.</summary>
    /// <param name="session">The open project.</param>
    /// <param name="template">The page's inline template snapshot.</param>
    /// <param name="textSlotId">The text slot that moved.</param>
    /// <param name="before">Its rect before the gesture.</param>
    /// <param name="after">Its rect after it.</param>
    /// <param name="description">The undo-menu wording.</param>
    public static IUndoableCommand SetTextSlotRect(
        ProjectSession session, Template template, string textSlotId, Rect before, Rect after, string description)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(template);

        return EditCommand.ForValue(
            description,
            before,
            after,
            rect =>
            {
                if (template.FindTextSlot(textSlotId) is { } text)
                {
                    text.Rect = rect;
                }

                session.MarkDirty();
            });
    }

    /// <summary>
    /// <em>Add photo slot</em> (doc 09 §3.7): a 0.30 × 0.30 slot at the cursor, aspect derived from
    /// its rect, no tier affinity and no caption — immediately an empty amber one (R14).
    /// </summary>
    /// <param name="session">The open project.</param>
    /// <param name="template">The page's inline template snapshot.</param>
    /// <param name="rect">Where to drop it, already clamped to the page.</param>
    /// <returns>The command and the id it will create.</returns>
    public static (IUndoableCommand Command, string SlotId) AddSlot(
        ProjectSession session, Template template, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(template);

        var slot = new ImageSlot
        {
            Id = NextId(template.Slots.Select(s => s.Id), "s"),
            Rect = rect,
            Aspect = PageGeometry.PhysicalAspect(rect),
            TierAffinity = TierAffinity.Any,
            CaptionPolicy = CaptionPolicy.None,
        };

        var command = new EditCommand(
            "Add photo slot",
            () =>
            {
                template.Slots.Add(slot);
                template.PhotoCount = template.Slots.Count;
                session.MarkDirty();
            },
            () =>
            {
                template.Slots.Remove(slot);
                template.PhotoCount = template.Slots.Count;
                session.MarkDirty();
            });

        return (command, slot.Id);
    }

    /// <summary>
    /// <em>Add text slot</em> (doc 09 §3.7): a caption-role text container whose role is editable
    /// afterwards. Typography always comes from the style cascade, never from the slot (doc 10 §2).
    /// </summary>
    /// <param name="session">The open project.</param>
    /// <param name="template">The page's inline template snapshot.</param>
    /// <param name="rect">Where to drop it, already clamped to the page.</param>
    /// <returns>The command and the id it will create.</returns>
    public static (IUndoableCommand Command, string TextSlotId) AddTextSlot(
        ProjectSession session, Template template, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(template);

        var text = new TextSlot
        {
            Id = NextId(template.TextSlots.Select(t => t.Id), "t"),
            Rect = rect,
            Role = TextRole.Caption,
        };

        var command = new EditCommand(
            "Add text slot",
            () =>
            {
                template.TextSlots.Add(text);
                session.MarkDirty();
            },
            () =>
            {
                template.TextSlots.Remove(text);
                session.MarkDirty();
            });

        return (command, text.Id);
    }

    /// <summary>
    /// Deletes an image slot. Any photo in it goes to the Unplaced bin (doc 09 §3.7), which is what
    /// the composite's <see cref="BinPlacementCommands.RemovePlacement"/> child does.
    /// </summary>
    /// <param name="session">The open project.</param>
    /// <param name="page">The page being edited.</param>
    /// <param name="template">Its inline template snapshot.</param>
    /// <param name="slotId">The slot to delete.</param>
    /// <returns>The command, or null when the slot is not there.</returns>
    public static IUndoableCommand? DeleteSlot(
        ProjectSession session, Page page, Template template, string slotId)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(template);

        var slot = template.FindSlot(slotId);
        if (slot is null)
        {
            return null;
        }

        var index = template.Slots.IndexOf(slot);
        var captions = template.TextSlots
            .Where(t => string.Equals(t.AttachedTo, slotId, StringComparison.Ordinal))
            .ToList();

        var removeSlot = new EditCommand(
            "Delete photo slot",
            () =>
            {
                template.Slots.Remove(slot);
                foreach (var caption in captions)
                {
                    template.TextSlots.Remove(caption);
                }

                template.PhotoCount = template.Slots.Count;
                session.MarkDirty();
            },
            () =>
            {
                template.Slots.Insert(Math.Min(index, template.Slots.Count), slot);
                foreach (var caption in captions)
                {
                    template.TextSlots.Add(caption);
                }

                template.PhotoCount = template.Slots.Count;
                session.MarkDirty();
            });

        var unplace = BinPlacementCommands.RemovePlacement(session, page, slotId, "Unplace photo");
        return unplace is null
            ? removeSlot
            : new CompositeCommand("Delete photo slot", unplace, removeSlot);
    }

    /// <summary>Deletes a text container — R15's "remove text containers".</summary>
    /// <param name="session">The open project.</param>
    /// <param name="page">The page being edited.</param>
    /// <param name="template">Its inline template snapshot.</param>
    /// <param name="textSlotId">The text slot to delete.</param>
    /// <returns>The command, or null when the text slot is not there.</returns>
    public static IUndoableCommand? DeleteTextSlot(
        ProjectSession session, Page page, Template template, string textSlotId)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(template);

        var text = template.FindTextSlot(textSlotId);
        if (text is null)
        {
            return null;
        }

        var index = template.TextSlots.IndexOf(text);
        var assignments = page.JournalAssignments
            .Where(a => string.Equals(a.TextSlotId, textSlotId, StringComparison.Ordinal))
            .Select(a => a with { EntryIds = new List<string>(a.EntryIds) })
            .ToList();

        return new EditCommand(
            "Delete text slot",
            () =>
            {
                template.TextSlots.Remove(text);
                foreach (var assignment in page.JournalAssignments
                             .Where(a => string.Equals(a.TextSlotId, textSlotId, StringComparison.Ordinal))
                             .ToList())
                {
                    page.JournalAssignments.Remove(assignment);
                }

                session.MarkDirty();
            },
            () =>
            {
                template.TextSlots.Insert(Math.Min(index, template.TextSlots.Count), text);
                foreach (var assignment in assignments)
                {
                    page.JournalAssignments.Add(assignment with { EntryIds = new List<string>(assignment.EntryIds) });
                }

                session.MarkDirty();
            });
    }

    /// <summary>
    /// Moves a container in its list — doc 09 §3.7's explicit z-order reorder, since overlap is
    /// allowed and z-order is slot order.
    /// </summary>
    /// <param name="session">The open project.</param>
    /// <param name="template">The page's inline template snapshot.</param>
    /// <param name="id">The container to move.</param>
    /// <param name="isText">True for a text slot.</param>
    /// <param name="delta">+1 to bring forward, -1 to send backward.</param>
    /// <returns>The command, or null when it is already at that end.</returns>
    public static IUndoableCommand? Reorder(
        ProjectSession session, Template template, string id, bool isText, int delta)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(template);

        if (isText)
        {
            var text = template.FindTextSlot(id);
            return text is null ? null : Move(template.TextSlots, text);
        }

        var slot = template.FindSlot(id);
        return slot is null ? null : Move(template.Slots, slot);

        IUndoableCommand? Move<T>(IList<T> list, T item)
        {
            var from = list.IndexOf(item);
            var to = from + delta;
            if (from < 0 || to < 0 || to >= list.Count)
            {
                return null;
            }

            return new EditCommand(
                delta > 0 ? "Bring forward" : "Send backward",
                () => Swap(list, from, to),
                () => Swap(list, to, from));

            void Swap(IList<T> target, int a, int b)
            {
                var moved = target[a];
                target.RemoveAt(a);
                target.Insert(b, moved);
                session.MarkDirty();
            }
        }
    }

    /// <summary>Switches a text container between journal and caption roles (doc 09 §3.7).</summary>
    /// <param name="session">The open project.</param>
    /// <param name="template">The page's inline template snapshot.</param>
    /// <param name="textSlotId">The text slot to retype.</param>
    /// <param name="role">The new role.</param>
    /// <returns>The command, or null when nothing changes.</returns>
    public static IUndoableCommand? SetTextRole(
        ProjectSession session, Template template, string textSlotId, TextRole role)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(template);

        var text = template.FindTextSlot(textSlotId);
        if (text is null || text.Role == role)
        {
            return null;
        }

        return EditCommand.ForValue(
            $"Set text role to {role.ToString().ToLowerInvariant()}",
            text.Role,
            role,
            value =>
            {
                if (template.FindTextSlot(textSlotId) is { } slot)
                {
                    slot.Role = value;
                }

                session.MarkDirty();
            });
    }

    /// <summary>The next free <c>s1..sN</c> / <c>t1..tN</c> id in a template.</summary>
    public static string NextId(IEnumerable<string> existing, string prefix)
    {
        var taken = existing.ToHashSet(StringComparer.Ordinal);
        for (var n = 1; ; n++)
        {
            var candidate = prefix + n.ToString(CultureInfo.InvariantCulture);
            if (taken.Add(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Clamps a rect to the page and to the minimum container size.</summary>
    public static Rect Clamp(Rect rect)
    {
        var w = Math.Clamp(rect.W, MinimumSize, 1);
        var h = Math.Clamp(rect.H, MinimumSize, 1);
        var x = Math.Clamp(rect.X, 0, 1 - w);
        var y = Math.Clamp(rect.Y, 0, 1 - h);
        return new Rect(x, y, w, h);
    }
}
