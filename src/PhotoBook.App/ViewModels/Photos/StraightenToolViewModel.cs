using System.Globalization;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoBook.App.Services;
using PhotoBook.Core.Model;

namespace PhotoBook.App.ViewModels.Photos;

/// <summary>
/// The straightening tool of R6/R11: a large preview on which the user <b>drags a line along something
/// that should be level or plumb</b>, and the rotation follows from it.
///
/// <para>
/// The numeric slider in the inspector still exists and still works — it is the same value, read from
/// and written to <see cref="AdjustmentStack.Straighten"/> through the same
/// <see cref="PhotoEditor"/> — but a slider is the wrong instrument for this job: nobody knows that a
/// horizon is off by 1.8°, they only know it is off. Tracing the horizon states the intent directly
/// and the arithmetic (<see cref="StraightenMath"/>) turns it into the number.
/// </para>
///
/// <para>
/// Three properties this holds to. The angle a traced line yields is a <b>delta</b> on the angle
/// already applied, because the preview the user drew on is already straightened — so successive
/// corrections converge instead of fighting. The rotated wedge is <b>auto-cropped</b> by the imaging
/// pipeline, so no gesture here can leave transparent corners. And each adjustment — a traced line, a
/// slider drag, a reset — is exactly <b>one undo entry</b>, coalesced on the photo and the parameter.
/// </para>
/// </summary>
public sealed partial class StraightenToolViewModel : ObservableObject
{
    private readonly PhotoEditor _editor;
    private readonly PhotoPreviewService _previews;

    private CancellationTokenSource? _renderCancellation;
    private IDisposable? _gesture;
    private bool _refreshing;

    /// <param name="editor">The undoable edit service.</param>
    /// <param name="previews">The off-thread preview renderer.</param>
    public StraightenToolViewModel(PhotoEditor editor, PhotoPreviewService previews)
    {
        _editor = editor;
        _previews = previews;
        _editor.PhotoChanged += OnPhotoChanged;
    }

    /// <summary>The photo being levelled, or null.</summary>
    public Photo? Photo { get; private set; }

    /// <summary>The preview the user traces on: the photo with every current edit, straightening included.</summary>
    [ObservableProperty]
    private BitmapSource? _preview;

    /// <summary>True while a preview render is in flight.</summary>
    [ObservableProperty]
    private bool _isRendering;

    /// <summary>Whether the traced line should become level, plumb, or whichever it is closer to.</summary>
    [ObservableProperty]
    private StraightenReference _reference = StraightenReference.Auto;

    /// <summary>True while the grid should be visible: during a drag, or whenever the user pins it on.</summary>
    [ObservableProperty]
    private bool _showGrid = true;

    /// <summary>The live angle readout while a line is being dragged, or the empty string.</summary>
    [ObservableProperty]
    private string _pendingLabel = string.Empty;

    /// <summary>What just happened, for the tool's own status line.</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>
    /// The photo's straighten angle in degrees. This is the very value the inspector's slider binds to
    /// — one number, two instruments.
    /// </summary>
    public double Angle
    {
        get => Photo?.Adjustments.Straighten ?? 0;
        set => SetAngle(value, "Straighten");
    }

    /// <summary>The angle as the user reads it, e.g. <c>−2.4°</c>.</summary>
    public string AngleLabel =>
        Angle.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture) + "°";

    /// <summary>Lowest angle the model accepts.</summary>
    public static double Minimum => -StraightenMath.MaxDegrees;

    /// <summary>Highest angle the model accepts.</summary>
    public static double Maximum => StraightenMath.MaxDegrees;

    /// <summary>True when the photo is straightened at all, so Reset can offer itself.</summary>
    public bool IsStraightened => Math.Abs(Angle) > 1e-9;

    /// <summary>Points the tool at a photo and renders its first preview.</summary>
    /// <param name="photo">The photo to level, or null to clear.</param>
    public void Attach(Photo? photo)
    {
        Photo = photo;
        PendingLabel = string.Empty;
        StatusMessage = photo is null
            ? string.Empty
            : "Drag a line along something that should be level — a horizon, a door frame, the edge of a table.";
        RefreshAll();
        _ = RenderAsync();
    }

    /// <summary>
    /// A line the user is still dragging: nothing is committed, the readout just previews what
    /// releasing would do.
    /// </summary>
    /// <param name="delta">Degrees the line would add to the current angle.</param>
    /// <param name="isUsable">False while the drag is too short to mean anything.</param>
    public void PreviewLine(double delta, bool isUsable)
    {
        if (Photo is null) return;

        PendingLabel = isUsable
            ? StraightenMath.Combine(Angle, delta).ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture) + "°"
            : string.Empty;
    }

    /// <summary>
    /// Commits a traced line as one undo entry. The measured angle is <em>added</em> to what is already
    /// applied, because the preview it was measured on already carries it.
    /// </summary>
    /// <param name="delta">Degrees the line adds to the current angle.</param>
    /// <param name="reference">Which axis the line was levelled against, for the status line.</param>
    public void ApplyLine(double delta, StraightenReference reference)
    {
        PendingLabel = string.Empty;
        if (Photo is null || Math.Abs(delta) < 1e-6) return;

        var target = StraightenMath.Combine(Angle, delta);
        var clamped = Math.Abs(target - (Angle + delta)) > 1e-6;

        SetAngle(target, "Straighten");

        var axis = reference == StraightenReference.Vertical ? "plumb" : "level";
        StatusMessage = clamped
            ? $"Straightened to {AngleLabel} — the limit is ±{StraightenMath.MaxDegrees:0}°. " +
              "Past that, use Rotate for a quarter turn."
            : $"Levelled to {AngleLabel} against the line you traced ({axis}). The rotated corners are cropped away.";
    }

    /// <summary>Clears the pending readout after an abandoned drag.</summary>
    public void CancelLine() => PendingLabel = string.Empty;

    /// <summary>Opens the coalescing scope for a slider drag, so the whole drag is one undo entry.</summary>
    public void BeginSliderGesture()
    {
        EndSliderGesture();
        if (Photo is { } photo)
        {
            _gesture = _editor.BeginAdjustmentGesture(photo, "straighten");
        }
    }

    /// <summary>Closes the slider's coalescing scope.</summary>
    public void EndSliderGesture()
    {
        _gesture?.Dispose();
        _gesture = null;
    }

    /// <summary>Returns the photo to unrotated, as its own undo entry.</summary>
    [RelayCommand]
    private void Reset()
    {
        if (!IsStraightened) return;
        SetAngle(0, "Reset straighten");
        StatusMessage = "Straightening removed.";
    }

    /// <summary>Nudges the angle by a tenth of a degree — the keyboard route to the same value.</summary>
    /// <param name="steps">Signed tenths of a degree.</param>
    public void Nudge(int steps) => SetAngle(Angle + (steps * 0.1), "Straighten");

    private void SetAngle(double degrees, string description)
    {
        if (_refreshing || Photo is not { } photo) return;

        var clamped = StraightenMath.Clamp(degrees);
        if (Math.Abs(clamped - photo.Adjustments.Straighten) < 1e-9) return;

        _editor.SetAdjustments(
            photo,
            photo.Adjustments with { Straighten = clamped },
            description,
            $"adjust:{photo.Id}:straighten");
    }

    private async Task RenderAsync()
    {
        var photo = Photo;
        _renderCancellation?.Cancel();
        _renderCancellation?.Dispose();
        _renderCancellation = null;

        if (photo is null)
        {
            Preview = null;
            return;
        }

        var cancellation = new CancellationTokenSource();
        _renderCancellation = cancellation;
        IsRendering = true;

        try
        {
            // The same settle the inspector uses: a fast slider drag abandons the frames it overtook
            // instead of queueing a pipeline pass per pointer move (doc 09 §6).
            await Task.Delay(40, cancellation.Token).ConfigureAwait(true);

            var bitmap = await _previews.RenderAsync(photo, photo.Adjustments, cancellation.Token)
                .ConfigureAwait(true);
            if (cancellation.IsCancellationRequested) return;

            Preview = bitmap;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request, which owns the preview now.
        }
        catch (Exception ex)
        {
            StatusMessage = $"Preview failed: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_renderCancellation, cancellation))
            {
                IsRendering = false;
            }
        }
    }

    private void OnPhotoChanged(Photo photo)
    {
        if (!ReferenceEquals(photo, Photo)) return;
        RefreshAll();
        _ = RenderAsync();
    }

    private void RefreshAll()
    {
        // Undo, redo and the inspector's own slider all write straight into the model, so this re-reads
        // rather than trying to guess who moved the value. The flag keeps the two-way binding from
        // writing the number it was just handed back into a fresh command.
        _refreshing = true;
        try
        {
            OnPropertyChanged(nameof(Angle));
            OnPropertyChanged(nameof(AngleLabel));
            OnPropertyChanged(nameof(IsStraightened));
        }
        finally
        {
            _refreshing = false;
        }
    }
}
