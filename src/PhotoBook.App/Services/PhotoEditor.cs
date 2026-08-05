using PhotoBook.Core.Model;
using PhotoBook.Engine;

namespace PhotoBook.App.Services;

/// <summary>Where a re-dated photo ended up (doc 09 §2.1).</summary>
public enum DateChangeScope
{
    /// <summary>Still this Chapter: the photo just re-sorts in the grid, placements are untouched.</summary>
    SameChapter,

    /// <summary>Another month of the same book year: the photo joins that Chapter's Unplaced bin.</summary>
    OtherChapter,

    /// <summary>Outside the book's year: the photo moves to the Outside-book tray, still in the catalog.</summary>
    OutsideBookYear,
}

/// <summary>The result of a re-date, with the sentence the UI should show (doc 09 §2.1).</summary>
/// <param name="Scope">Which of the three outcomes happened.</param>
/// <param name="Before">The date the photo carried before the edit.</param>
/// <param name="After">The date it carries now.</param>
/// <param name="VacatedSlots">How many Slots became empty amber holes because the photo left its Chapter.</param>
/// <param name="Message">A complete, human sentence describing the move.</param>
public sealed record DateChangeOutcome(
    DateChangeScope Scope, DateTime Before, DateTime After, int VacatedSlots, string Message);

/// <summary>One placement that kept a hand-tuned crop because its page is Pinned or Detached.</summary>
/// <param name="Month">The Chapter the page belongs to.</param>
/// <param name="PageNumber">1-based page number within that Chapter.</param>
/// <param name="SlotId">The Slot holding the photo.</param>
public sealed record StaleCrop(int Month, int PageNumber, string SlotId);

/// <summary>What a focus-driven re-crop actually did (doc 09 §2.2).</summary>
/// <param name="Updated">Placements re-cropped on unpinned pages.</param>
/// <param name="Stale">Placements deliberately left alone, for the "crop may be stale" badge.</param>
public sealed record RecropOutcome(int Updated, IReadOnlyList<StaleCrop> Stale)
{
    /// <summary>An outcome with nothing in it — the photo is on no page.</summary>
    public static RecropOutcome None { get; } = new(0, []);
}

/// <summary>The result of a grid reorder (doc 09 §2.5).</summary>
/// <param name="Applied">False when the drop was refused; <paramref name="Message"/> says why.</param>
/// <param name="Moved">How many photos were re-timed.</param>
/// <param name="Restamped">True when the day had to be re-spaced first because timestamps collided.</param>
/// <param name="Message">Status-line text, or the refusal reason.</param>
public sealed record ReorderOutcome(bool Applied, int Moved, bool Restamped, string Message);

/// <summary>
/// Every Photos-tab model edit of doc 09 §2, in one place and every one of them undoable: re-dating
/// (§2.1, R6), Focus Region editing (§2.2, R25), the adjustment stack (§2.4, R11) and grid reorder
/// (§2.5, R6).
///
/// <para>
/// The rules this class encodes rather than restates. Mutation goes through
/// <see cref="ProjectSession"/> — it owns the model and is the only writer — and through
/// <see cref="UndoStack"/>, so nothing here changes the model outside a command whose
/// <c>Undo</c> restores the exact prior state, captured by value before the edit runs. Edits that
/// have more than one consequence (a re-date that also vacates a Slot; a reorder that first
/// re-spaces a burst) commit as one <see cref="CompositeCommand"/>, so one <c>Ctrl+Z</c> reverses the
/// whole thing (doc 09 §4).
/// </para>
///
/// <para>
/// Nothing here touches pixels: adjustments are parameters on the catalog entry and the archived
/// original is never opened for writing (kernel §5, R11 "non-destructive").
/// </para>
/// </summary>
public sealed class PhotoEditor
{
    private readonly ProjectSession _session;
    private readonly UndoStack _undo;

    // Disabling a detected region parks its weight at 0; the model has nowhere to keep the old value,
    // so re-enabling in the same session restores what the detector actually said. Undo is unaffected
    // either way — it replays the captured before-value.
    // Keyed by reference on purpose: FocusRegion is a mutable record, so its value hash changes the
    // moment a weight is written and a value-keyed dictionary would lose the entry it just stored.
    private readonly Dictionary<object, double> _weightBeforeDisable = new(ReferenceEqualityComparer.Instance);

    /// <param name="session">The open project — the single writer of the model.</param>
    /// <param name="undo">The book's undo stack.</param>
    public PhotoEditor(ProjectSession session, UndoStack undo)
    {
        _session = session;
        _undo = undo;
    }

    /// <summary>Raised after any edit to a photo's own state (date, focus, adjustments, tier).</summary>
    public event Action<Photo>? PhotoChanged;

    /// <summary>Raised when placements changed, so the Pages tab and filmstrip re-render.</summary>
    public event Action<Photo>? PlacementsChanged;

    /// <summary>Raised when a photo left or joined a Chapter, so the rail counts and grid reload.</summary>
    public event Action<DateChangeOutcome>? DateChanged;

    /// <summary>The undo stack every edit here records into, for view models that need gestures.</summary>
    public UndoStack Undo => _undo;

    // ================================================================= adjustments (§2.4, R11)

    /// <summary>
    /// Replaces a photo's adjustment stack as one undoable edit. Both states are stored by value, so
    /// undo restores the exact parameter set — including parameters this edit did not touch.
    /// </summary>
    /// <param name="photo">The photo being edited.</param>
    /// <param name="next">The stack to apply; it is copied, not aliased.</param>
    /// <param name="description">Edit-menu text, e.g. "Contrast".</param>
    /// <param name="coalesceKey">
    /// Names the target and the parameter (<c>adjust:ph-…:contrast</c>) so repeats within the stack's
    /// coalesce window fold together and two different sliders never merge. Pass null for a one-shot
    /// edit such as a reset.
    /// </param>
    public void SetAdjustments(Photo photo, AdjustmentStack next, string description, string? coalesceKey = null)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(next);

        var before = photo.Adjustments with { };
        var after = next with { };

        _undo.ExecuteValue(
            description,
            before,
            after,
            value =>
            {
                photo.Adjustments = value with { };
                _session.MarkDirty();
                PhotoChanged?.Invoke(photo);
            },
            coalesceKey);
    }

    /// <summary>
    /// Opens the coalescing scope for a slider drag: every frame between thumb-press and thumb-release
    /// folds into one undo entry, whatever the pointer-move count (doc 09 §4).
    /// </summary>
    /// <param name="photo">The photo being edited.</param>
    /// <param name="parameterId">The slider's id, so two parameters never merge.</param>
    public IDisposable BeginAdjustmentGesture(Photo photo, string parameterId)
    {
        ArgumentNullException.ThrowIfNull(photo);
        return _undo.BeginGesture($"adjust:{photo.Id}:{parameterId}");
    }

    /// <summary>Returns the whole stack to identity in one undoable step.</summary>
    /// <param name="photo">The photo to reset.</param>
    public void ResetAdjustments(Photo photo)
    {
        ArgumentNullException.ThrowIfNull(photo);
        if (photo.Adjustments.IsIdentity)
        {
            return;
        }

        SetAdjustments(photo, AdjustmentStack.Identity, "Reset adjustments");
    }

    // ================================================================= focus regions (§2.2, R25)

    /// <summary>
    /// Adds a user-drawn Focus Region. <see cref="FocusKind.User"/> outranks every detector in fusion,
    /// which is precisely what makes smart-crop obey the user (kernel §4).
    /// </summary>
    /// <param name="photo">The photo being edited.</param>
    /// <param name="rect">The region in normalized image coordinates.</param>
    /// <returns>The region that was added, so the caller can select it.</returns>
    public FocusRegion AddUserRegion(Photo photo, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(photo);

        var region = new FocusRegion { Rect = Clamp(rect), Weight = 1.0, Kind = FocusKind.User };
        var index = photo.FocusRegions.Count;

        _undo.Execute(new EditCommand(
            "Add focus area",
            () =>
            {
                photo.FocusRegions.Insert(Math.Min(index, photo.FocusRegions.Count), region);
                _session.MarkDirty();
                PhotoChanged?.Invoke(photo);
            },
            () =>
            {
                RemoveByReference(photo.FocusRegions, region);
                _session.MarkDirty();
                PhotoChanged?.Invoke(photo);
            }));

        return region;
    }

    /// <summary>Deletes a region. Only <see cref="FocusKind.User"/> regions can be deleted (doc 09 §2.2).</summary>
    /// <param name="photo">The photo being edited.</param>
    /// <param name="region">The region to remove.</param>
    public void RemoveRegion(Photo photo, FocusRegion region)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(region);

        var index = IndexOfReference(photo.FocusRegions, region);
        if (index < 0 || region.Kind != FocusKind.User)
        {
            return;
        }

        _undo.Execute(new EditCommand(
            "Delete focus area",
            () =>
            {
                RemoveByReference(photo.FocusRegions, region);
                _session.MarkDirty();
                PhotoChanged?.Invoke(photo);
            },
            () =>
            {
                photo.FocusRegions.Insert(Math.Min(index, photo.FocusRegions.Count), region);
                _session.MarkDirty();
                PhotoChanged?.Invoke(photo);
            }));
    }

    /// <summary>
    /// Moves or resizes a user region. Called once per pointer frame inside
    /// <see cref="BeginFocusGesture"/>, so the whole drag is one undo entry.
    /// </summary>
    /// <param name="photo">The photo being edited.</param>
    /// <param name="region">The region being reshaped.</param>
    /// <param name="before">Its rect when the gesture started — what undo restores.</param>
    /// <param name="after">The rect to apply now.</param>
    public void SetRegionRect(Photo photo, FocusRegion region, Rect before, Rect after)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(region);

        _undo.ExecuteValue(
            "Move focus area",
            before,
            Clamp(after),
            value =>
            {
                region.Rect = value;
                _session.MarkDirty();
                PhotoChanged?.Invoke(photo);
            },
            $"focus.rect:{photo.Id}:{RegionKey(photo, region)}");
    }

    /// <summary>Sets a region's weight; 0 disables it without deleting it (doc 09 §2.2).</summary>
    /// <param name="photo">The photo being edited.</param>
    /// <param name="region">The region.</param>
    /// <param name="before">The weight to restore on undo.</param>
    /// <param name="after">The weight to apply.</param>
    /// <param name="description">Edit-menu text.</param>
    public void SetRegionWeight(
        Photo photo, FocusRegion region, double before, double after, string description = "Focus weight")
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(region);

        if (after <= 0 && before > 0)
        {
            _weightBeforeDisable[region] = before;
        }

        _undo.ExecuteValue(
            description,
            before,
            Math.Clamp(after, 0, 1),
            value =>
            {
                region.Weight = value;
                _session.MarkDirty();
                PhotoChanged?.Invoke(photo);
            },
            $"focus.weight:{photo.Id}:{RegionKey(photo, region)}");
    }

    /// <summary>Toggles a detected region between disabled (weight 0) and its previous weight.</summary>
    /// <param name="photo">The photo being edited.</param>
    /// <param name="region">The region to toggle.</param>
    public void ToggleRegionDisabled(Photo photo, FocusRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);

        if (region.Weight > 0)
        {
            SetRegionWeight(photo, region, region.Weight, 0, "Disable focus area");
        }
        else
        {
            var restored = _weightBeforeDisable.TryGetValue(region, out var previous) && previous > 0 ? previous : 1.0;
            SetRegionWeight(photo, region, region.Weight, restored, "Enable focus area");
        }
    }

    /// <summary>
    /// Opens the coalescing scope for a focus drag (draw, move, resize, weight), so pointer-down to
    /// pointer-up is one undo entry.
    /// </summary>
    /// <param name="photo">The photo being edited.</param>
    /// <param name="scope">What kind of gesture — part of the key, so a move never merges into a resize.</param>
    public IDisposable BeginFocusGesture(Photo photo, string scope)
    {
        ArgumentNullException.ThrowIfNull(photo);
        return _undo.BeginGesture($"focus.{scope}:{photo.Id}");
    }

    /// <summary>
    /// Removes every user region and re-enables every disabled detected one, returning the photo to
    /// what analysis said — one composite undo entry (doc 09 §2.2 "reset to automatic").
    /// </summary>
    /// <param name="photo">The photo to reset.</param>
    public void ResetFocusToAutomatic(Photo photo)
    {
        ArgumentNullException.ThrowIfNull(photo);

        var userRegions = photo.FocusRegions.Where(r => r.Kind == FocusKind.User).ToList();
        var disabled = photo.FocusRegions.Where(r => r.Kind != FocusKind.User && r.Weight <= 0).ToList();
        if (userRegions.Count == 0 && disabled.Count == 0)
        {
            return;
        }

        using (_undo.BeginBatch("Reset focus areas"))
        {
            foreach (var region in userRegions)
            {
                RemoveRegion(photo, region);
            }

            foreach (var region in disabled)
            {
                var restored = _weightBeforeDisable.TryGetValue(region, out var previous) && previous > 0
                    ? previous
                    : 1.0;
                SetRegionWeight(photo, region, region.Weight, restored, "Enable focus area");
            }
        }
    }

    /// <summary>
    /// Re-runs smart-crop for every placement of this photo, immediately and silently on
    /// <b>unpinned</b> pages and never on Pinned or Detached ones — a hand-tuned crop is only ever
    /// replaced by an explicit gesture (doc 09 §2.2). Recorded as one composite entry.
    /// </summary>
    /// <param name="photo">The photo whose focus changed.</param>
    /// <param name="includePinned">True for the Slot's explicit "Re-crop" action.</param>
    public RecropOutcome RecropPlacements(Photo photo, bool includePinned = false)
    {
        ArgumentNullException.ThrowIfNull(photo);

        var book = _session.Book;
        if (book is null)
        {
            return RecropOutcome.None;
        }

        var profile = BuiltInPrintProfiles.Generic;
        var size = profile.FindPageSize(book.PageSize) ?? profile.PageSizes[0];
        var stale = new List<StaleCrop>();
        var updates = new List<(Placement Placement, CropState Before, CropState After, Photo Photo)>();

        foreach (var chapter in _session.Chapters)
        {
            for (var index = 0; index < chapter.Pages.Count; index++)
            {
                var page = chapter.Pages[index];
                var placement = page.Placements.FirstOrDefault(p =>
                    string.Equals(p.PhotoId, photo.Id, StringComparison.Ordinal));
                if (placement is null)
                {
                    continue;
                }

                if ((page.Pinned || page.IsDetached) && !includePinned)
                {
                    stale.Add(new StaleCrop(chapter.Month, index + 1, placement.SlotId));
                    continue;
                }

                var template = page.ResolveTemplate(_session.FindTemplate);
                var slot = template?.FindSlot(placement.SlotId);
                if (slot is null)
                {
                    continue;
                }

                var crop = SmartCrop.Crop(
                    photo,
                    slot,
                    Spreads.SideOf(index),
                    size.TrimWidthIn,
                    size.TrimHeightIn,
                    LayoutWeights.Default,
                    slot.SpanId is null ? 1.0 : 2.0);

                if (crop.Crop != placement.Crop)
                {
                    updates.Add((placement, placement.Crop, crop.Crop, photo));
                }
            }
        }

        if (updates.Count > 0)
        {
            using (_undo.BeginBatch("Re-crop for focus change"))
            {
                foreach (var (placement, before, after, target) in updates)
                {
                    _undo.ExecuteValue(
                        "Re-crop",
                        before,
                        after,
                        value =>
                        {
                            placement.Crop = value;
                            _session.MarkDirty();
                            PlacementsChanged?.Invoke(target);
                        });
                }
            }
        }

        return new RecropOutcome(updates.Count, stale);
    }

    // ================================================================= re-date (§2.1, R6)

    /// <summary>
    /// What would happen if this photo were re-dated, without changing anything — this is what the
    /// dialog shows live as the user picks a date, so a move to another month or out of the book year
    /// is never a surprise.
    /// </summary>
    /// <param name="photo">The photo being re-dated.</param>
    /// <param name="newDate">The candidate date.</param>
    public DateChangeOutcome Preview(Photo photo, DateTime newDate)
    {
        ArgumentNullException.ThrowIfNull(photo);

        var year = _session.Book?.Year ?? photo.TakenAt.Year;
        var scope = Scope(photo.TakenAt, newDate, year);
        var vacates = scope != DateChangeScope.SameChapter ? CountPlacements(photo) : 0;
        return new DateChangeOutcome(scope, photo.TakenAt, newDate, vacates, Describe(scope, newDate, vacates, year));
    }

    /// <summary>
    /// Re-dates a photo (R6). Setting a date by hand always clears <c>dateUncertain</c> and records the
    /// provenance as <see cref="DateSource.User"/>. When the new date leaves the Chapter the photo's
    /// Slots are vacated in the same composite, so one <c>Ctrl+Z</c> puts it back on its page.
    /// </summary>
    /// <param name="photo">The photo to re-date.</param>
    /// <param name="newDate">The new local wall-clock capture time.</param>
    public DateChangeOutcome ChangeDate(Photo photo, DateTime newDate)
    {
        ArgumentNullException.ThrowIfNull(photo);

        var year = _session.Book?.Year ?? photo.TakenAt.Year;
        var before = photo.TakenAt;
        var beforeSource = photo.DateSource;
        var beforeUncertain = photo.DateUncertain;
        var scope = Scope(before, newDate, year);
        var vacated = 0;

        using (_undo.BeginBatch($"Change date of {photo.OriginalFileName}"))
        {
            _undo.Execute(new EditCommand(
                "Change date",
                () =>
                {
                    photo.TakenAt = newDate;
                    photo.DateSource = DateSource.User;
                    photo.DateUncertain = false;
                    _session.MarkDirty();
                    PhotoChanged?.Invoke(photo);
                },
                () =>
                {
                    photo.TakenAt = before;
                    photo.DateSource = beforeSource;
                    photo.DateUncertain = beforeUncertain;
                    _session.MarkDirty();
                    PhotoChanged?.Invoke(photo);
                }));

            if (scope != DateChangeScope.SameChapter)
            {
                vacated = VacatePlacements(photo);
            }
        }

        var outcome = new DateChangeOutcome(scope, before, newDate, vacated, Describe(scope, newDate, vacated, year));
        DateChanged?.Invoke(outcome);
        return outcome;
    }

    /// <summary>
    /// Removes every placement of a photo, leaving empty amber Slots behind (R14). The source pages'
    /// Pinned state is deliberately untouched — doc 09 §3.5's rationale, so auto-layout can heal the
    /// hole later.
    /// </summary>
    private int VacatePlacements(Photo photo)
    {
        var removals = new List<(Page Page, Placement Placement, int Index)>();
        foreach (var chapter in _session.Chapters)
        {
            foreach (var page in chapter.Pages)
            {
                for (var i = page.Placements.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(page.Placements[i].PhotoId, photo.Id, StringComparison.Ordinal))
                    {
                        removals.Add((page, page.Placements[i], i));
                    }
                }
            }
        }

        foreach (var (page, placement, index) in removals)
        {
            _undo.Execute(new EditCommand(
                "Vacate slot",
                () =>
                {
                    page.Placements.Remove(placement);
                    _session.MarkDirty();
                    PlacementsChanged?.Invoke(photo);
                },
                () =>
                {
                    page.Placements.Insert(Math.Min(index, page.Placements.Count), placement);
                    _session.MarkDirty();
                    PlacementsChanged?.Invoke(photo);
                }));
        }

        return removals.Count;
    }

    private int CountPlacements(Photo photo) =>
        _session.Chapters
            .SelectMany(c => c.Pages)
            .SelectMany(p => p.Placements)
            .Count(p => string.Equals(p.PhotoId, photo.Id, StringComparison.Ordinal));

    private static DateChangeScope Scope(DateTime before, DateTime after, int bookYear) =>
        after.Year != bookYear ? DateChangeScope.OutsideBookYear
        : after.Year == before.Year && after.Month == before.Month ? DateChangeScope.SameChapter
        : DateChangeScope.OtherChapter;

    private static string Describe(DateChangeScope scope, DateTime date, int vacated, int bookYear)
    {
        var month = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(date.Month);
        var holes = vacated switch
        {
            0 => string.Empty,
            1 => " Its slot on the page it was on becomes an empty amber slot.",
            _ => $" The {vacated} slots it was in become empty amber slots.",
        };

        return scope switch
        {
            DateChangeScope.SameChapter =>
                $"Stays in {month} — the photo just moves in the grid; pages are untouched.",
            DateChangeScope.OtherChapter =>
                $"Moves to {month} — the photo joins {month}'s Unplaced bin.{holes}",
            _ =>
                $"Leaves {bookYear} — the photo moves to the Outside-book tray, where it stays in the " +
                $"catalog with its new date rather than disappearing. Give it a {bookYear} date and it " +
                $"comes straight back.{holes}",
        };
    }

    // ================================================================= grid reorder (§2.5, R6)

    /// <summary>
    /// Reorders photos within one calendar day by rewriting <c>takenTime</c> to the midpoint of the
    /// drop's neighbours — the grid's order <em>is</em> chronological order, the single axis the engine
    /// reads, so a reorder is a time edit rather than a hidden second sort key (doc 09 §2.5).
    ///
    /// <para>
    /// A drop across a day boundary is refused with the hint that points at <em>Change date…</em>;
    /// the calendar date and <c>dateUncertain</c> are never touched here. When the neighbours share a
    /// timestamp — bursts, or scans that all read midnight — the day is first re-stamped at even
    /// intervals across its own span and the drop then applies, all inside one composite entry.
    /// </para>
    /// </summary>
    /// <param name="dayPhotos">Every photo of the target day, in current grid (chronological) order.</param>
    /// <param name="moving">The dragged photos, in their existing relative order.</param>
    /// <param name="insertIndex">Insertion position in <paramref name="dayPhotos"/>, 0..Count.</param>
    public ReorderOutcome ReorderWithinDay(
        IReadOnlyList<Photo> dayPhotos, IReadOnlyList<Photo> moving, int insertIndex)
    {
        ArgumentNullException.ThrowIfNull(dayPhotos);
        ArgumentNullException.ThrowIfNull(moving);

        if (moving.Count == 0 || dayPhotos.Count == 0)
        {
            return new ReorderOutcome(false, 0, false, string.Empty);
        }

        var day = dayPhotos[0].TakenOn;
        if (moving.Any(p => p.TakenOn != day) || dayPhotos.Any(p => p.TakenOn != day))
        {
            return new ReorderOutcome(
                false, 0, false,
                "Photos can only be reordered within one day. To move this photo to another day, use " +
                "Change date… — that is a re-date, with all its consequences.");
        }

        var identity = moving.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var remaining = dayPhotos.Where(p => !identity.Contains(p.Id)).ToList();
        var target = dayPhotos.Take(Math.Clamp(insertIndex, 0, dayPhotos.Count)).Count(p => !identity.Contains(p.Id));

        var previous = target > 0 ? remaining[target - 1] : null;
        var next = target < remaining.Count ? remaining[target] : null;

        // Dropping a block back where it already was is a no-op and must not burn an undo entry.
        var reordered = remaining.Take(target).Concat(moving).Concat(remaining.Skip(target)).ToList();
        if (reordered.Select(p => p.Id).SequenceEqual(dayPhotos.Select(p => p.Id), StringComparer.Ordinal))
        {
            return new ReorderOutcome(false, 0, false, string.Empty);
        }

        var restamped = false;
        using (_undo.BeginBatch($"Reorder {moving.Count} photo{(moving.Count == 1 ? "" : "s")}"))
        {
            if (NeedsRestamp(previous, next, moving.Count))
            {
                RestampDay(dayPhotos);
                restamped = true;
            }

            var times = SlotTimes(previous, next, moving.Count, day);
            for (var i = 0; i < moving.Count; i++)
            {
                SetTakenAt(moving[i], times[i]);
            }
        }

        var message = restamped
            ? $"Reordered {moving.Count} photo{(moving.Count == 1 ? "" : "s")}; the day's times were " +
              "evenly re-spaced first because several photos shared a timestamp."
            : $"Reordered {moving.Count} photo{(moving.Count == 1 ? "" : "s")} within {day:MMMM d}.";

        return new ReorderOutcome(true, moving.Count, restamped, message);
    }

    /// <summary>True when there is no room between the neighbours to interleave the block sensibly.</summary>
    private static bool NeedsRestamp(Photo? previous, Photo? next, int count)
    {
        if (previous is null || next is null)
        {
            return false;
        }

        var gap = next.TakenAt - previous.TakenAt;
        return gap <= TimeSpan.Zero || gap < TimeSpan.FromSeconds(count + 1);
    }

    /// <summary>
    /// Spreads a day's photos at even intervals across their existing span, preserving their order.
    /// A day whose photos all carry the same instant gets a synthetic one-minute-per-photo span
    /// starting at that instant, clamped so the calendar date cannot roll over.
    /// </summary>
    private void RestampDay(IReadOnlyList<Photo> dayPhotos)
    {
        if (dayPhotos.Count < 2)
        {
            return;
        }

        var day = dayPhotos[0].TakenOn;
        var start = dayPhotos.Min(p => p.TakenAt);
        var end = dayPhotos.Max(p => p.TakenAt);
        var endOfDay = day.ToDateTime(TimeOnly.MaxValue);

        if (end - start < TimeSpan.FromSeconds(dayPhotos.Count))
        {
            end = start + TimeSpan.FromMinutes(dayPhotos.Count);
            if (end > endOfDay)
            {
                end = endOfDay;
                start = day.ToDateTime(TimeOnly.MinValue);
            }
        }

        var step = (end - start) / (dayPhotos.Count - 1);
        for (var i = 0; i < dayPhotos.Count; i++)
        {
            SetTakenAt(dayPhotos[i], Clamp(start + (step * i), day));
        }
    }

    /// <summary>The times the dragged block should take, given its new neighbours.</summary>
    private static IReadOnlyList<DateTime> SlotTimes(Photo? previous, Photo? next, int count, DateOnly day)
    {
        var times = new DateTime[count];

        if (previous is not null && next is not null)
        {
            var step = (next.TakenAt - previous.TakenAt) / (count + 1);
            for (var i = 0; i < count; i++)
            {
                times[i] = Clamp(previous.TakenAt + (step * (i + 1)), day);
            }
        }
        else if (previous is null && next is not null)
        {
            var room = next.TakenAt - day.ToDateTime(TimeOnly.MinValue);
            var step = Min(room / (count + 1), TimeSpan.FromMinutes(1));
            for (var i = 0; i < count; i++)
            {
                times[i] = Clamp(next.TakenAt - (step * (count - i)), day);
            }
        }
        else if (previous is not null)
        {
            var room = day.ToDateTime(TimeOnly.MaxValue) - previous.TakenAt;
            var step = Min(room / (count + 1), TimeSpan.FromMinutes(1));
            for (var i = 0; i < count; i++)
            {
                times[i] = Clamp(previous.TakenAt + (step * (i + 1)), day);
            }
        }
        else
        {
            var start = day.ToDateTime(new TimeOnly(12, 0));
            for (var i = 0; i < count; i++)
            {
                times[i] = start + TimeSpan.FromSeconds(i);
            }
        }

        return times;
    }

    /// <summary>Writes one photo's time as its own undoable step; the batch makes them one entry.</summary>
    private void SetTakenAt(Photo photo, DateTime when)
    {
        if (photo.TakenAt == when)
        {
            return;
        }

        _undo.ExecuteValue(
            "Reorder photo",
            photo.TakenAt,
            when,
            value =>
            {
                photo.TakenAt = value;
                _session.MarkDirty();
                PhotoChanged?.Invoke(photo);
            });
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static DateTime Clamp(DateTime when, DateOnly day) => new(
        Math.Clamp(
            when.Ticks,
            day.ToDateTime(TimeOnly.MinValue).Ticks,
            day.ToDateTime(TimeOnly.MaxValue).Ticks));

    private static Rect Clamp(Rect rect)
    {
        var w = Math.Clamp(rect.W, 0.01, 1);
        var h = Math.Clamp(rect.H, 0.01, 1);
        var x = Math.Clamp(rect.X, 0, 1 - w);
        var y = Math.Clamp(rect.Y, 0, 1 - h);
        return new Rect(x, y, w, h);
    }

    /// <summary>Identifies a region by position for coalescing keys — reference, never value, equality.</summary>
    private static string RegionKey(Photo photo, FocusRegion region) =>
        IndexOfReference(photo.FocusRegions, region)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static int IndexOfReference(IList<FocusRegion> regions, FocusRegion region)
    {
        for (var i = 0; i < regions.Count; i++)
        {
            if (ReferenceEquals(regions[i], region))
            {
                return i;
            }
        }

        return -1;
    }

    private static void RemoveByReference(IList<FocusRegion> regions, FocusRegion region)
    {
        var index = IndexOfReference(regions, region);
        if (index >= 0)
        {
            regions.RemoveAt(index);
        }
    }
}
