using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoBook.App.Services;
using PhotoBook.App.Views.Photos;
using PhotoBook.Core.Model;

namespace PhotoBook.App.ViewModels.Photos;

/// <summary>
/// The Photos-tab inspector sections of doc 09 §2 that edit the photo itself: <b>Date</b> (§2.1, R6),
/// <b>Focus</b> (§2.2, R25) and <b>Adjust</b> (§2.4, R11), plus the controller the grid uses for
/// drag-reorder (§2.5, R6). Tier and Exclude already live in the shell's own inspector and are
/// deliberately not duplicated here.
///
/// <para>
/// This view model owns nothing but composition: each section is its own view model, every edit goes
/// through <see cref="PhotoEditor"/> and is undoable, and the two surfaces that need room — the
/// re-date dialog and the Focus Region editor — open as their own styled windows, because both are
/// precision tasks that a 320 px rail cannot do honestly.
/// </para>
/// </summary>
public sealed partial class PhotoInspectorViewModel : ObservableObject
{
    private readonly PhotoEditor _editor;

    /// <param name="editor">The undoable edit service.</param>
    /// <param name="adjustments">The Adjust section.</param>
    /// <param name="focus">The Focus Region editor.</param>
    /// <param name="reorder">The grid's reorder controller.</param>
    public PhotoInspectorViewModel(
        PhotoEditor editor,
        AdjustmentsViewModel adjustments,
        FocusRegionEditorViewModel focus,
        PhotoReorderController reorder)
    {
        _editor = editor;
        Adjustments = adjustments;
        Focus = focus;
        Reorder = reorder;

        Adjustments.PreviewRendered += OnPreviewRendered;
        Reorder.Reordered += outcome => StatusMessage = outcome.Message;
        _editor.PhotoChanged += OnPhotoChanged;
    }

    /// <summary>Raised for the shell's status line and toasts.</summary>
    public event Action<string>? StatusRaised;

    /// <summary>
    /// Raised when a photo left its Chapter, so the grid, the rail counts and the pages reload. The
    /// re-date is already committed and undoable when this fires.
    /// </summary>
    public event Action<DateChangeOutcome>? PhotoMoved;

    /// <summary>The Adjust section (R11).</summary>
    public AdjustmentsViewModel Adjustments { get; }

    /// <summary>The Focus Region editor (R25).</summary>
    public FocusRegionEditorViewModel Focus { get; }

    /// <summary>The grid's drag-reorder controller (R6, §2.5).</summary>
    public PhotoReorderController Reorder { get; }

    /// <summary>The selected grid tile, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPhoto))]
    [NotifyPropertyChangedFor(nameof(DateLabel))]
    [NotifyPropertyChangedFor(nameof(ProvenanceLabel))]
    [NotifyPropertyChangedFor(nameof(IsDateUncertain))]
    [NotifyPropertyChangedFor(nameof(FocusSummary))]
    [NotifyPropertyChangedFor(nameof(HasUserFocus))]
    [NotifyCanExecuteChangedFor(nameof(ChangeDateCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditFocusCommand))]
    private PhotoItemViewModel? _item;

    /// <summary>What just happened, for the inspector's own status line.</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>The selected photo's catalog record.</summary>
    public Photo? Photo => Item?.Photo;

    /// <summary>True when a photo is selected.</summary>
    public bool HasPhoto => Item is not null;

    /// <summary>The photo's date, spelled out.</summary>
    public string DateLabel => Photo is null
        ? string.Empty
        : Photo.TakenAt.ToString("dddd, d MMMM yyyy · h:mm tt", CultureInfo.CurrentCulture);

    /// <summary>Where that date came from (kernel §10).</summary>
    public string ProvenanceLabel => Photo?.DateSource switch
    {
        DateSource.Exif => "EXIF capture time",
        DateSource.Graph => "OneDrive capture time",
        DateSource.User => "Set by you",
        DateSource.FileMtime => "File modified time",
        _ => string.Empty,
    };

    /// <summary>True when the date is a guess from the file's mtime.</summary>
    public bool IsDateUncertain => Photo?.DateUncertain ?? false;

    /// <summary>True when the user has drawn at least one focus area.</summary>
    public bool HasUserFocus => Photo?.FocusRegions.Any(r => r.Kind == FocusKind.User) ?? false;

    /// <summary>"2 found automatically · 1 of yours", or the empty-state line.</summary>
    public string FocusSummary
    {
        get
        {
            if (Photo is not { } photo)
            {
                return string.Empty;
            }

            var user = photo.FocusRegions.Count(r => r.Kind == FocusKind.User);
            var automatic = photo.FocusRegions.Count(r => r.Kind != FocusKind.User);

            return (user, automatic) switch
            {
                (0, 0) => "Nothing found yet — analysis has not run for this photo.",
                (0, 1) => "1 area found automatically.",
                (0, _) => $"{automatic} areas found automatically.",
                (_, 0) => $"{user} area{(user == 1 ? "" : "s")} drawn by you.",
                _ => $"{automatic} found automatically · {user} of yours, which win.",
            };
        }
    }

    /// <summary>Points every section at a photo. Call this from the grid's selection.</summary>
    /// <param name="item">The selected tile, or null.</param>
    public void Select(PhotoItemViewModel? item)
    {
        if (ReferenceEquals(item, Item))
        {
            return;
        }

        Item = item;
        Adjustments.Attach(item?.Photo);
        Focus.Attach(item?.Photo);
        StatusMessage = string.Empty;
    }

    /// <summary>Opens the re-date dialog (doc 09 §2.1, the <c>D</c> key).</summary>
    [RelayCommand(CanExecute = nameof(HasPhoto))]
    private void ChangeDate()
    {
        if (Photo is not { } photo)
        {
            return;
        }

        var model = new ChangeDateViewModel(_editor, photo);
        var window = new ChangeDateWindow(model)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };

        if (window.ShowDialog() != true || model.Outcome is not { } outcome)
        {
            return;
        }

        Item?.Refresh();
        Report(outcome.Message);
        RefreshDate();

        if (outcome.Scope != DateChangeScope.SameChapter)
        {
            PhotoMoved?.Invoke(outcome);
        }
    }

    /// <summary>Opens the Focus Region editor (doc 09 §2.2, the <c>F</c> key).</summary>
    [RelayCommand(CanExecute = nameof(HasPhoto))]
    private void EditFocus()
    {
        if (Photo is not { } photo)
        {
            return;
        }

        Focus.Attach(photo);
        var window = new FocusRegionWindow(Focus)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };

        window.ShowDialog();

        Item?.Refresh();
        OnPropertyChanged(nameof(FocusSummary));
        OnPropertyChanged(nameof(HasUserFocus));
        if (Focus.StatusMessage.Length > 0)
        {
            Report(Focus.StatusMessage);
        }
    }

    private void OnPhotoChanged(Photo photo)
    {
        if (!ReferenceEquals(photo, Photo))
        {
            return;
        }

        RefreshDate();
        OnPropertyChanged(nameof(FocusSummary));
        OnPropertyChanged(nameof(HasUserFocus));
        Item?.Refresh();
    }

    private void RefreshDate()
    {
        OnPropertyChanged(nameof(DateLabel));
        OnPropertyChanged(nameof(ProvenanceLabel));
        OnPropertyChanged(nameof(IsDateUncertain));
    }

    /// <summary>
    /// The tile follows the edit immediately: the on-disk thumbnail tier is keyed by the adjustment
    /// hash, so it will be rebuilt eventually, but the grid should not show yesterday's pixels while
    /// the user is still moving the slider.
    /// </summary>
    private void OnPreviewRendered(System.Windows.Media.Imaging.BitmapSource preview)
    {
        if (Item is { } item && !Adjustments.ShowOriginal)
        {
            item.Thumbnail = PhotoPreviewService.Downscale(preview);
        }
    }

    private void Report(string message)
    {
        StatusMessage = message;
        StatusRaised?.Invoke(message);
    }
}
