using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoBook.App.Controls;
using PhotoBook.App.Services;
using PhotoBook.Core.Model;
using CoreRect = PhotoBook.Core.Model.Rect;

namespace PhotoBook.App.ViewModels.Photos;

/// <summary>
/// One Focus Region as the editor sees it: a live projection of the model record, never a copy, so a
/// command's undo is immediately visible on the surface.
/// </summary>
public sealed partial class FocusRegionItemViewModel : ObservableObject, IFocusRegionShape
{
    /// <param name="region">The model record this item projects.</param>
    public FocusRegionItemViewModel(FocusRegion region) => Region = region;

    /// <summary>The underlying record in <c>photos.json</c>.</summary>
    public FocusRegion Region { get; }

    /// <inheritdoc/>
    public CoreRect Rect => Region.Rect;

    /// <inheritdoc/>
    public FocusKind Kind => Region.Kind;

    /// <inheritdoc/>
    public double Weight => Region.Weight;

    /// <inheritdoc/>
    public bool IsEditable => Region.Kind == FocusKind.User;

    /// <summary>True when the region is kept but ignored by fusion (doc 09 §2.2).</summary>
    public bool IsDisabled => Region.Weight <= 0;

    /// <inheritdoc/>
    public string Label => Region.Kind switch
    {
        FocusKind.User => "You",
        FocusKind.Person => string.IsNullOrWhiteSpace(Region.PersonName) ? "Person" : Region.PersonName!,
        FocusKind.Face => "Face",
        _ => "Subject",
    };

    /// <summary>The kind, spelled for the legend and the list row.</summary>
    public string KindLabel => Region.Kind switch
    {
        FocusKind.User => "Drawn by you",
        FocusKind.Person => "Named person",
        FocusKind.Face => "Detected face",
        _ => "Salient subject",
    };

    /// <summary>Its weight as a percentage, for the row's readout.</summary>
    public string WeightLabel => IsDisabled
        ? "off"
        : Region.Weight.ToString("P0", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Its position, for the row's second line.</summary>
    public string RectLabel =>
        $"{Region.Rect.X:P0} · {Region.Rect.Y:P0} — {Region.Rect.W:P0} × {Region.Rect.H:P0}";

    /// <summary>Re-reads everything derived from the record after a command ran.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Rect));
        OnPropertyChanged(nameof(Weight));
        OnPropertyChanged(nameof(IsDisabled));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(WeightLabel));
        OnPropertyChanged(nameof(RectLabel));
    }
}

/// <summary>
/// The Focus Region editor of doc 09 §2.2 (R25): draw, move, resize and delete the regions smart-crop
/// must keep in frame, over a large preview of the photo.
///
/// <para>
/// Two things make this feel like an editor rather than a form. Every gesture commits as <b>one</b>
/// undo entry that already contains its consequences — the re-crop of every placement of this photo on
/// an unpinned page rides inside the same composite, so <c>Ctrl+Z</c> puts both the region and the
/// pages back (doc 09 §2.2, §4). And the model is only written at pointer-up: the surface draws the
/// in-flight rect itself, so a drag costs no commands at all and the undo history stays one entry per
/// intention.
/// </para>
///
/// <para>
/// Placements on Pinned or Detached pages are deliberately left alone and reported instead — a
/// hand-tuned crop is never overwritten without an explicit gesture, which is what
/// <see cref="RecropPinnedCommand"/> is for.
/// </para>
/// </summary>
public sealed partial class FocusRegionEditorViewModel : ObservableObject
{
    private readonly PhotoEditor _editor;
    private readonly PhotoPreviewService _previews;

    private CancellationTokenSource? _previewCancellation;
    private bool _refreshing;
    private bool _draggingWeight;
    private double _weightAtDragStart;
    private IReadOnlyList<StaleCrop> _stale = [];

    /// <param name="editor">The undoable edit service.</param>
    /// <param name="previews">Renders the preview the regions are drawn over.</param>
    public FocusRegionEditorViewModel(PhotoEditor editor, PhotoPreviewService previews)
    {
        _editor = editor;
        _previews = previews;
        _editor.PhotoChanged += OnPhotoChanged;
    }

    /// <summary>The photo being edited, or null when nothing is selected.</summary>
    public Photo? Photo { get; private set; }

    /// <summary>The regions, in model order.</summary>
    public ObservableCollection<FocusRegionItemViewModel> Regions { get; } = [];

    /// <summary>The photo at preview resolution, with its adjustments applied.</summary>
    [ObservableProperty]
    private BitmapSource? _preview;

    /// <summary>True while the preview decode is in flight.</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>What just happened, for the editor's status line.</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>The selected region; the surface binds this two-way.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    [NotifyPropertyChangedFor(nameof(SelectedWeight))]
    [NotifyPropertyChangedFor(nameof(SelectionTitle))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleDisabledCommand))]
    private IFocusRegionShape? _selectedRegion;

    /// <summary>True when a region is selected.</summary>
    public bool HasSelection => SelectedRegion is not null;

    /// <summary>True when the selection is a user region, the only kind that can be deleted.</summary>
    public bool CanDelete => SelectedRegion is { IsEditable: true };

    /// <summary>The heading above the weight slider.</summary>
    public string SelectionTitle => SelectedRegion is FocusRegionItemViewModel item
        ? $"{item.Label} · {item.KindLabel}"
        : "No focus area selected";

    /// <summary>True when this photo has at least one user region or a disabled detected one.</summary>
    public bool HasUserEdits =>
        Photo is not null &&
        Photo.FocusRegions.Any(r => r.Kind == FocusKind.User || r.Weight <= 0);

    /// <summary>True when some placement kept its crop because its page is Pinned or Detached.</summary>
    public bool HasStaleCrops => _stale.Count > 0;

    /// <summary>The "crop may be stale" sentence, naming the pages the user must decide about.</summary>
    public string StaleCropMessage => _stale.Count == 0
        ? string.Empty
        : $"{_stale.Count} placement{(_stale.Count == 1 ? "" : "s")} on pinned or hand-built page" +
          $"{(_stale.Count == 1 ? "" : "s")} ({string.Join(", ", _stale.Select(s => $"p. {s.PageNumber}").Distinct())}) " +
          "kept their crops. Re-crop them too?";

    /// <summary>
    /// The selected region's weight. Written live while the slider is dragged and committed as one
    /// undo entry at thumb-release (doc 09 §2.2 "drags coalesce into one undo entry").
    /// </summary>
    public double SelectedWeight
    {
        get => SelectedRegion?.Weight ?? 0;
        set
        {
            if (SelectedRegion is not FocusRegionItemViewModel item || Math.Abs(item.Weight - value) < 0.0005)
            {
                return;
            }

            if (_draggingWeight)
            {
                // Live, outside the undo system: the entry is written once, at thumb-release.
                item.Region.Weight = Math.Clamp(value, 0, 1);
                item.Refresh();
                OnPropertyChanged();
                return;
            }

            CommitWeight(item, item.Weight, value);
        }
    }

    /// <summary>Points the editor at a photo, loading its preview and regions.</summary>
    /// <param name="photo">The photo to edit, or null to clear.</param>
    public void Attach(Photo? photo)
    {
        Photo = photo;
        _stale = [];
        StatusMessage = string.Empty;
        RebuildRegions();
        SelectedRegion = null;
        OnPropertyChanged(nameof(HasUserEdits));
        OnPropertyChanged(nameof(HasStaleCrops));
        OnPropertyChanged(nameof(StaleCropMessage));
        _ = LoadPreviewAsync();
    }

    /// <summary>Re-renders the preview — call after an adjustment changed the pixels.</summary>
    public async Task LoadPreviewAsync()
    {
        var photo = Photo;
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;

        if (photo is null)
        {
            Preview = null;
            return;
        }

        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        IsLoading = true;

        try
        {
            var bitmap = await _previews.RenderAsync(photo, photo.Adjustments, cancellation.Token)
                .ConfigureAwait(true);
            if (!cancellation.IsCancellationRequested)
            {
                Preview = bitmap;
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request; the newer one owns the preview.
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open this photo: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
            {
                IsLoading = false;
            }
        }
    }

    // ================================================================= surface gestures

    /// <summary>A new user region was drawn; adds it and re-crops in one composite entry.</summary>
    /// <param name="rect">The new region in normalized image coordinates.</param>
    public void AddRegion(CoreRect rect)
    {
        if (Photo is not { } photo)
        {
            return;
        }

        FocusRegion? added = null;
        using (_editor.Undo.BeginBatch("Add focus area"))
        {
            added = _editor.AddUserRegion(photo, rect);
            Recrop(photo);
        }

        SelectedRegion = Regions.FirstOrDefault(r => ReferenceEquals(r.Region, added));
        StatusMessage = "Focus area added — smart crop now keeps it in frame.";
    }

    /// <summary>
    /// A move or resize finished. The whole gesture becomes one entry here rather than one per
    /// pointer frame, which is why nothing was written while the pointer was down.
    /// </summary>
    /// <param name="region">The region that moved.</param>
    /// <param name="start">Its rect when the gesture began.</param>
    /// <param name="rect">Its rect now.</param>
    /// <param name="canceled">True when the user pressed Esc — nothing is committed.</param>
    public void CommitRegionRect(IFocusRegionShape region, CoreRect start, CoreRect rect, bool canceled)
    {
        if (canceled || Photo is not { } photo || region is not FocusRegionItemViewModel item || rect == start)
        {
            return;
        }

        using (_editor.Undo.BeginBatch("Move focus area"))
        {
            _editor.SetRegionRect(photo, item.Region, start, rect);
            Recrop(photo);
        }

        StatusMessage = "Focus area updated.";
    }

    /// <summary>Called from the view at slider thumb-press, so the drag becomes one entry.</summary>
    public void BeginWeightDrag()
    {
        _draggingWeight = true;
        _weightAtDragStart = SelectedRegion?.Weight ?? 0;
    }

    /// <summary>Called from the view at slider thumb-release; writes the one undo entry.</summary>
    public void EndWeightDrag()
    {
        _draggingWeight = false;
        if (SelectedRegion is FocusRegionItemViewModel item && Math.Abs(item.Weight - _weightAtDragStart) > 0.0005)
        {
            var final = item.Weight;
            item.Region.Weight = _weightAtDragStart;
            CommitWeight(item, _weightAtDragStart, final);
        }
    }

    private void CommitWeight(FocusRegionItemViewModel item, double before, double after)
    {
        if (Photo is not { } photo)
        {
            return;
        }

        using (_editor.Undo.BeginBatch("Focus weight"))
        {
            _editor.SetRegionWeight(photo, item.Region, before, after);
            Recrop(photo);
        }

        OnPropertyChanged(nameof(SelectedWeight));
        OnPropertyChanged(nameof(HasUserEdits));
    }

    // ================================================================= commands

    /// <summary>Deletes the selected user region (detected regions are disabled, never deleted).</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void DeleteSelected()
    {
        if (Photo is not { } photo || SelectedRegion is not FocusRegionItemViewModel item)
        {
            return;
        }

        using (_editor.Undo.BeginBatch("Delete focus area"))
        {
            _editor.RemoveRegion(photo, item.Region);
            Recrop(photo);
        }

        SelectedRegion = null;
        StatusMessage = "Focus area deleted.";
    }

    /// <summary>Disables a detected region (weight 0) or restores it — kept either way.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ToggleDisabled()
    {
        if (Photo is not { } photo || SelectedRegion is not FocusRegionItemViewModel item)
        {
            return;
        }

        var wasDisabled = item.IsDisabled;
        using (_editor.Undo.BeginBatch(wasDisabled ? "Enable focus area" : "Disable focus area"))
        {
            _editor.ToggleRegionDisabled(photo, item.Region);
            Recrop(photo);
        }

        StatusMessage = wasDisabled
            ? "Focus area back in play."
            : "Focus area disabled — it is kept, but smart crop ignores it.";
    }

    /// <summary>Selects a region from the list beside the surface.</summary>
    /// <param name="item">The row that was clicked.</param>
    [RelayCommand]
    private void Select(FocusRegionItemViewModel? item) => SelectedRegion = item;

    /// <summary>Throws away every user edit and returns to what analysis said.</summary>
    [RelayCommand]
    private void ResetToAutomatic()
    {
        if (Photo is not { } photo || !HasUserEdits)
        {
            return;
        }

        using (_editor.Undo.BeginBatch("Reset focus areas"))
        {
            _editor.ResetFocusToAutomatic(photo);
            Recrop(photo);
        }

        SelectedRegion = null;
        StatusMessage = "Focus areas reset to what analysis found.";
    }

    /// <summary>Re-crops the placements that were skipped because their pages are Pinned or Detached.</summary>
    [RelayCommand]
    private void RecropPinned()
    {
        if (Photo is not { } photo)
        {
            return;
        }

        var outcome = _editor.RecropPlacements(photo, includePinned: true);
        _stale = outcome.Stale;
        OnPropertyChanged(nameof(HasStaleCrops));
        OnPropertyChanged(nameof(StaleCropMessage));
        StatusMessage = outcome.Updated == 0
            ? "Nothing to re-crop."
            : $"Re-cropped {outcome.Updated} placement{(outcome.Updated == 1 ? "" : "s")}.";
    }

    // ================================================================= plumbing

    private void Recrop(Photo photo)
    {
        var outcome = _editor.RecropPlacements(photo);
        _stale = outcome.Stale;
        OnPropertyChanged(nameof(HasStaleCrops));
        OnPropertyChanged(nameof(StaleCropMessage));
    }

    private void OnPhotoChanged(Photo photo)
    {
        if (_refreshing || !ReferenceEquals(photo, Photo))
        {
            return;
        }

        _refreshing = true;
        try
        {
            if (photo.FocusRegions.Count != Regions.Count ||
                Regions.Where((item, i) => !ReferenceEquals(item.Region, photo.FocusRegions[i])).Any())
            {
                RebuildRegions();
            }
            else
            {
                foreach (var item in Regions)
                {
                    item.Refresh();
                }
            }

            OnPropertyChanged(nameof(SelectedWeight));
            OnPropertyChanged(nameof(HasUserEdits));
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RebuildRegions()
    {
        var selected = (SelectedRegion as FocusRegionItemViewModel)?.Region;

        Regions.Clear();
        foreach (var region in Photo?.FocusRegions ?? [])
        {
            Regions.Add(new FocusRegionItemViewModel(region));
        }

        if (selected is not null)
        {
            SelectedRegion = Regions.FirstOrDefault(r => ReferenceEquals(r.Region, selected));
        }
    }
}
