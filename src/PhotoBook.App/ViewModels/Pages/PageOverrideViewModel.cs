using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoBook.App.Services;
using PhotoBook.Core.Model;

namespace PhotoBook.App.ViewModels.Pages;

/// <summary>One movable container on the page in layout override mode: an image slot or a text slot.</summary>
public sealed partial class OverrideItemViewModel : ObservableObject
{
    /// <summary>Creates the item from a template slot.</summary>
    /// <param name="id">The slot id, unique within the template.</param>
    /// <param name="isText">True for a text container.</param>
    /// <param name="rect">Its rect in normalized page space.</param>
    /// <param name="zIndex">Its position in the template's slot order — the z-order (doc 09 §3.7).</param>
    public OverrideItemViewModel(string id, bool isText, Rect rect, int zIndex)
    {
        Id = id;
        IsText = isText;
        _rect = rect;
        ZIndex = zIndex;
    }

    /// <summary>The slot id.</summary>
    public string Id { get; }

    /// <summary>True for a text container, false for an image slot.</summary>
    public bool IsText { get; }

    /// <summary>Its position in the template's declaration order.</summary>
    public int ZIndex { get; }

    /// <summary>The container's rect in normalized page space, <c>[0,1]²</c> over the trim box.</summary>
    [ObservableProperty]
    private Rect _rect;

    /// <summary>True when this image slot holds no photo — the amber state (R14).</summary>
    public bool IsEmpty { get; init; }

    /// <summary>The text container's role, or null for an image slot.</summary>
    public TextRole? Role { get; init; }

    /// <summary>The short label drawn on the container.</summary>
    public string Label { get; init; } = string.Empty;
}

/// <summary>
/// Layout override mode (doc 09 §3.7, R15): the page's own editable copy of its layout. Slots and
/// text slots grow move/resize handles, containers can be added and deleted, and z-order is
/// explicit because overlap is allowed.
/// <para>
/// The <b>first geometry edit Detaches the page</b> — its template reference is replaced by an
/// inline snapshot and the page is Pinned — and the library template is never mutated. The detach
/// rides inside the same undo entry as the edit that caused it (doc 09 §4), so one <c>Ctrl+Z</c>
/// takes back both.
/// </para>
/// </summary>
public sealed partial class PageOverrideViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly UndoStack _undo;

    private string? _editingId;
    private Rect _editBefore;
    private IUndoableCommand? _pendingDetach;

    /// <summary>Creates override mode over the open session.</summary>
    public PageOverrideViewModel(ProjectSession session, UndoStack undo)
    {
        _session = session;
        _undo = undo;
    }

    /// <summary>Raised whenever the page's geometry changed and the canvas needs a fresh render.</summary>
    public event Action? Invalidated;

    /// <summary>Raised when the mode is left, so the shell can restore normal editing.</summary>
    public event Action? Exited;

    /// <summary>The containers on the page, in z-order (last drawn on top).</summary>
    public ObservableCollection<OverrideItemViewModel> Items { get; } = [];

    /// <summary>True while override mode is on — <c>L</c> toggles it (doc 09 §5).</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>The page being edited.</summary>
    [ObservableProperty]
    private Page? _page;

    /// <summary>The chapter the page belongs to — what the canvas renderer needs alongside the page.</summary>
    public Chapter? Chapter { get; private set; }

    /// <summary>The selected container's id.</summary>
    [ObservableProperty]
    private string? _selectedId;

    /// <summary>Where the pointer last was, in normalized page space — where a new container drops.</summary>
    public (double X, double Y) CursorNormalized { get; set; } = (0.35, 0.35);

    /// <summary>The template the page renders with right now: its snapshot once detached.</summary>
    public Template? EffectiveTemplate => Page?.ResolveTemplate(_session.FindTemplate);

    /// <summary>The page's own inline snapshot, or null while it is still on a library template.</summary>
    public Template? DetachedTemplate => Page?.DetachedTemplate;

    /// <summary>True once the page owns its layout (the <em>Detached</em> chip).</summary>
    public bool IsDetached => Page?.IsDetached == true;

    /// <summary>True when the page is pinned — always true for a detached page.</summary>
    public bool IsPinned => Page?.Pinned == true;

    /// <summary>The selected container, or null.</summary>
    public OverrideItemViewModel? Selected =>
        Items.FirstOrDefault(i => string.Equals(i.Id, SelectedId, StringComparison.Ordinal));

    /// <summary>True when a container is selected.</summary>
    public bool HasSelection => Selected is not null;

    /// <summary>True when the selection is a text container, so the role switch applies.</summary>
    public bool IsTextSelected => Selected?.IsText == true;

    /// <summary>The status line under the toolbar.</summary>
    public string StatusMessage => Selected is { } item
        ? $"{(item.IsText ? "Text" : "Photo")} slot {item.Id} · " +
          $"{item.Rect.W * 100:F0}% × {item.Rect.H * 100:F0}% at ({item.Rect.X * 100:F0}%, {item.Rect.Y * 100:F0}%)"
        : "Drag a container to move it, or its handles to resize. Snapping is on.";

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Points the mode at a page. Safe to call on every page change.</summary>
    /// <param name="chapter">The page's chapter.</param>
    /// <param name="page">The page to edit.</param>
    public void Attach(Chapter? chapter, Page? page)
    {
        Chapter = chapter;
        Page = page;
        SelectedId = null;
        Rebuild();
    }

    partial void OnPageChanged(Page? value) => Rebuild();

    partial void OnIsActiveChanged(bool value)
    {
        if (!value)
        {
            SelectedId = null;
            Exited?.Invoke();
        }

        Rebuild();
    }

    partial void OnSelectedIdChanged(string? value)
    {
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsTextSelected));
        OnPropertyChanged(nameof(StatusMessage));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        BringForwardCommand.NotifyCanExecuteChanged();
        SendBackwardCommand.NotifyCanExecuteChanged();
        MakeJournalCommand.NotifyCanExecuteChanged();
        MakeCaptionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Re-reads the containers from the page's effective template.</summary>
    public void Rebuild()
    {
        Items.Clear();
        if (Page is { } page && EffectiveTemplate is { } template)
        {
            var z = 0;
            foreach (var slot in template.Slots)
            {
                Items.Add(new OverrideItemViewModel(slot.Id, isText: false, slot.Rect, z++)
                {
                    IsEmpty = page.PlacementFor(slot.Id) is null,
                    Label = slot.Id,
                });
            }

            foreach (var text in template.TextSlots)
            {
                Items.Add(new OverrideItemViewModel(text.Id, isText: true, text.Rect, z++)
                {
                    Role = text.Role,
                    Label = text.Role switch
                    {
                        TextRole.Journal => "journal",
                        TextRole.MonthTitle => "month title",
                        _ => "caption",
                    },
                });
            }
        }

        if (Selected is null)
        {
            SelectedId = null;
        }

        RaiseState();
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(EffectiveTemplate));
        OnPropertyChanged(nameof(DetachedTemplate));
        OnPropertyChanged(nameof(IsDetached));
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsTextSelected));
        OnPropertyChanged(nameof(StatusMessage));
        RevertToTemplateCommand.NotifyCanExecuteChanged();
        AddPhotoSlotCommand.NotifyCanExecuteChanged();
        AddTextSlotCommand.NotifyCanExecuteChanged();
        Invalidated?.Invoke();
    }

    // ------------------------------------------------------------ drag gestures

    /// <summary>
    /// Starts a move or resize. This is where the page Detaches if it has not already: the snapshot
    /// is taken before a single coordinate moves, so the library template never sees the edit.
    /// </summary>
    /// <param name="id">The container being dragged.</param>
    /// <returns>False when there is nothing to edit.</returns>
    public bool BeginGeometryEdit(string id)
    {
        if (Page is not { } page || !IsActive)
        {
            return false;
        }

        _pendingDetach = null;
        if (!page.IsDetached)
        {
            var detach = PageLayoutOverride.Detach(_session, page, _session.FindTemplate);
            if (detach is null)
            {
                return false;
            }

            detach.Do();
            _pendingDetach = detach;
            Rebuild();
        }

        var item = Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));
        if (item is null)
        {
            RollBackPendingDetach();
            return false;
        }

        _editingId = id;
        _editBefore = item.Rect;
        SelectedId = id;
        return true;
    }

    /// <summary>
    /// Writes a live rect during a drag: the model changes immediately so the page re-renders under
    /// the pointer, and only the finished gesture becomes an undo entry.
    /// </summary>
    /// <param name="id">The container being dragged.</param>
    /// <param name="rect">Its new rect, already snapped and clamped.</param>
    public void UpdateGeometry(string id, Rect rect)
    {
        if (Page?.DetachedTemplate is not { } template)
        {
            return;
        }

        var clamped = PageLayoutOverride.Clamp(rect);
        var item = Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));
        if (item is null)
        {
            return;
        }

        if (item.IsText)
        {
            if (template.FindTextSlot(id) is { } text)
            {
                text.Rect = clamped;
            }
        }
        else if (template.FindSlot(id) is { } slot)
        {
            slot.Rect = clamped;
            slot.Aspect = PageGeometry.PhysicalAspect(clamped);
        }

        item.Rect = clamped;
        _session.MarkDirty();
        OnPropertyChanged(nameof(StatusMessage));
        Invalidated?.Invoke();
    }

    /// <summary>Ends the gesture and records it — with the detach it caused — as one undo entry.</summary>
    public void EndGeometryEdit()
    {
        var id = _editingId;
        _editingId = null;

        if (id is null || Page?.DetachedTemplate is not { } template)
        {
            RollBackPendingDetach();
            return;
        }

        var item = Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));
        var after = item?.Rect ?? _editBefore;
        var moved = after != _editBefore;

        if (!moved && _pendingDetach is null)
        {
            return;
        }

        if (!moved)
        {
            // Nothing actually moved, so a detach on its own would be a surprise. Take it back.
            RollBackPendingDetach();
            return;
        }

        var description = item?.IsText == true ? "Move text slot" : "Move photo slot";
        var geometry = item?.IsText == true
            ? PageLayoutOverride.SetTextSlotRect(_session, template, id, _editBefore, after, description)
            : PageLayoutOverride.SetSlotRect(_session, template, id, _editBefore, after, description);

        var detach = _pendingDetach;
        _pendingDetach = null;

        _undo.Push(detach is null
            ? geometry
            : new CompositeCommand("Edit page layout", detach, geometry));

        RaiseState();
    }

    /// <summary>Abandons a gesture in flight, putting the page back the way it was.</summary>
    public void CancelGeometryEdit()
    {
        var id = _editingId;
        _editingId = null;

        if (id is not null)
        {
            UpdateGeometry(id, _editBefore);
        }

        RollBackPendingDetach();
    }

    private void RollBackPendingDetach()
    {
        if (_pendingDetach is null)
        {
            return;
        }

        _pendingDetach.Undo();
        _pendingDetach = null;
        Rebuild();
    }

    // ---------------------------------------------------------------- commands

    /// <summary>Toggles the mode — the <c>L</c> key of doc 09 §5.</summary>
    [RelayCommand]
    public void Toggle() => IsActive = !IsActive;

    /// <summary>Leaves the mode without undoing anything (<c>Esc</c>).</summary>
    [RelayCommand]
    public void Exit() => IsActive = false;

    /// <summary>Doc 09 §3.7: a 0.30 × 0.30 photo slot at the cursor, immediately empty-amber.</summary>
    [RelayCommand(CanExecute = nameof(CanEditLayout))]
    private void AddPhotoSlot()
    {
        var rect = PageLayoutOverride.Clamp(Rect.FromCenter(
            CursorNormalized.X, CursorNormalized.Y, PageLayoutOverride.NewSlotSize, PageLayoutOverride.NewSlotSize));

        RunEdit("Add photo slot", template =>
        {
            var (command, slotId) = PageLayoutOverride.AddSlot(_session, template, rect);
            _selectAfterEdit = slotId;
            return command;
        });
    }

    /// <summary>Doc 09 §3.7: a caption-role text container at the cursor; the role is editable after.</summary>
    [RelayCommand(CanExecute = nameof(CanEditLayout))]
    private void AddTextSlot()
    {
        var rect = PageLayoutOverride.Clamp(Rect.FromCenter(
            CursorNormalized.X, CursorNormalized.Y,
            PageLayoutOverride.NewTextSlotWidth, PageLayoutOverride.NewTextSlotHeight));

        RunEdit("Add text slot", template =>
        {
            var (command, textSlotId) = PageLayoutOverride.AddTextSlot(_session, template, rect);
            _selectAfterEdit = textSlotId;
            return command;
        });
    }

    /// <summary>
    /// <c>Del</c> in override mode: removes the selected container. A photo in a deleted slot goes to
    /// the Unplaced bin rather than out of the book (doc 09 §3.7).
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteSelected()
    {
        if (Selected is not { } item || Page is not { } page)
        {
            return;
        }

        RunEdit(item.IsText ? "Delete text slot" : "Delete photo slot", template => item.IsText
            ? PageLayoutOverride.DeleteTextSlot(_session, page, template, item.Id)
            : PageLayoutOverride.DeleteSlot(_session, page, template, item.Id));

        SelectedId = null;
    }

    /// <summary>Moves the selection one step up the z-order.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void BringForward() => Reorder(+1);

    /// <summary>Moves the selection one step down the z-order.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void SendBackward() => Reorder(-1);

    private void Reorder(int delta)
    {
        if (Selected is not { } item)
        {
            return;
        }

        var id = item.Id;
        var isText = item.IsText;
        RunEdit(delta > 0 ? "Bring forward" : "Send backward",
            template => PageLayoutOverride.Reorder(_session, template, id, isText, delta));
        SelectedId = id;
    }

    /// <summary>Retypes the selected text container as journal text.</summary>
    [RelayCommand(CanExecute = nameof(IsTextSelected))]
    private void MakeJournal() => SetRole(TextRole.Journal);

    /// <summary>Retypes the selected text container as a caption.</summary>
    [RelayCommand(CanExecute = nameof(IsTextSelected))]
    private void MakeCaption() => SetRole(TextRole.Caption);

    private void SetRole(TextRole role)
    {
        if (Selected is not { IsText: true } item)
        {
            return;
        }

        var id = item.Id;
        RunEdit($"Set text role to {role}", template => PageLayoutOverride.SetTextRole(_session, template, id, role));
        SelectedId = id;
    }

    /// <summary>
    /// <em>Revert to template</em> (doc 09 §3.7): re-attaches the library template the snapshot came
    /// from and clears the Detached chip. The page stays Pinned, because the user is still editing it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsDetached))]
    private void RevertToTemplate()
    {
        if (Page is not { } page)
        {
            return;
        }

        var command = PageLayoutOverride.RevertToTemplate(_session, page, _session.FindTemplate);
        if (command is null)
        {
            return;
        }

        _undo.Execute(command);
        SelectedId = null;
        Rebuild();
    }

    private bool CanEditLayout() => IsActive && Page is not null;

    private string? _selectAfterEdit;

    /// <summary>
    /// Runs one override action as a single undo entry, detaching the page first if this is its
    /// first geometry edit. An action that turns out to be a no-op takes its own detach back with
    /// it, so nothing is ever left detached for no reason.
    /// </summary>
    private void RunEdit(string description, Func<Template, IUndoableCommand?> build)
    {
        if (Page is not { } page || !IsActive)
        {
            return;
        }

        IUndoableCommand? detach = null;
        if (!page.IsDetached)
        {
            detach = PageLayoutOverride.Detach(_session, page, _session.FindTemplate);
            if (detach is null)
            {
                return;
            }

            detach.Do();
        }

        if (page.DetachedTemplate is not { } template)
        {
            detach?.Undo();
            return;
        }

        _selectAfterEdit = null;
        var action = build(template);
        if (action is null)
        {
            detach?.Undo();
            Rebuild();
            return;
        }

        action.Do();
        _undo.Push(detach is null ? action : new CompositeCommand(description, detach, action));

        Rebuild();
        if (_selectAfterEdit is not null)
        {
            SelectedId = _selectAfterEdit;
            _selectAfterEdit = null;
        }
    }
}
