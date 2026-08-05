using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoBook.App.Services;
using PhotoBook.Core.Model;

namespace PhotoBook.App.ViewModels.Photos;

/// <summary>How a slider's number reads: the sliders are stored −1..1 but shown the way doc 05 writes them.</summary>
public enum AdjustmentFormat
{
    /// <summary>−100 … +100, the scale doc 05 uses for the tone and colour sliders.</summary>
    Scaled,

    /// <summary>Stops, e.g. <c>+0.65 EV</c>.</summary>
    Exposure,

    /// <summary>Degrees, e.g. <c>−2.5°</c>.</summary>
    Degrees,
}

/// <summary>
/// One parameter of the <see cref="AdjustmentStack"/> as a slider row: its range, its readout, its
/// own reset, and the undo behaviour of a drag.
/// </summary>
public sealed partial class AdjustmentSliderViewModel : ObservableObject
{
    private readonly AdjustmentsViewModel _owner;
    private readonly Func<AdjustmentStack, double> _read;
    private readonly Action<AdjustmentStack, double> _write;

    /// <param name="owner">The panel that owns the photo and the undo plumbing.</param>
    /// <param name="id">Stable id; part of the coalescing key, so two sliders never merge.</param>
    /// <param name="label">The row's name.</param>
    /// <param name="hint">One line of "what this does", shown as the row's tooltip.</param>
    /// <param name="minimum">Lowest value.</param>
    /// <param name="maximum">Highest value.</param>
    /// <param name="read">Reads the parameter from a stack.</param>
    /// <param name="write">Writes the parameter into a stack.</param>
    /// <param name="format">How the readout is spelled.</param>
    /// <param name="isBipolar">True for a −/0/+ parameter, which gets the centre-detented track.</param>
    public AdjustmentSliderViewModel(
        AdjustmentsViewModel owner,
        string id,
        string label,
        string hint,
        double minimum,
        double maximum,
        Func<AdjustmentStack, double> read,
        Action<AdjustmentStack, double> write,
        AdjustmentFormat format = AdjustmentFormat.Scaled,
        bool isBipolar = true)
    {
        _owner = owner;
        _read = read;
        _write = write;
        Id = id;
        Label = label;
        Hint = hint;
        Minimum = minimum;
        Maximum = maximum;
        Format = format;
        IsBipolar = isBipolar;
    }

    /// <summary>Stable id, used in the undo coalescing key.</summary>
    public string Id { get; }

    /// <summary>The row's name.</summary>
    public string Label { get; }

    /// <summary>Tooltip text.</summary>
    public string Hint { get; }

    /// <summary>Lowest value.</summary>
    public double Minimum { get; }

    /// <summary>Highest value.</summary>
    public double Maximum { get; }

    /// <summary>How the readout is spelled.</summary>
    public AdjustmentFormat Format { get; }

    /// <summary>True for a parameter that runs negative through zero to positive.</summary>
    public bool IsBipolar { get; }

    /// <summary>How far one arrow-key press moves the value.</summary>
    public double SmallStep => Format == AdjustmentFormat.Degrees ? 0.1 : 0.01;

    /// <summary>How far Page Up/Down moves the value.</summary>
    public double LargeStep => Format == AdjustmentFormat.Degrees ? 1.0 : 0.10;

    /// <summary>
    /// The live value. Reading goes straight to the model, which is what makes undo, redo and the
    /// reset buttons show up on the slider with no extra bookkeeping.
    /// </summary>
    public double Value
    {
        get => _owner.Stack is { } stack ? _read(stack) : 0;
        set => _owner.SetParameter(this, value);
    }

    /// <summary>The number beside the label.</summary>
    public string ValueLabel => Format switch
    {
        AdjustmentFormat.Exposure => Value.ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture) + " EV",
        AdjustmentFormat.Degrees => Value.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture) + "°",
        _ => (Value * 100).ToString("+0;-0;0", CultureInfo.CurrentCulture),
    };

    /// <summary>True when this parameter is off its neutral value, so the reset dot can appear.</summary>
    public bool IsModified => Math.Abs(Value) > 1e-9;

    /// <summary>Writes this parameter into a stack — used by the panel to build the next stack.</summary>
    /// <param name="stack">The stack to write into.</param>
    /// <param name="value">The value to write.</param>
    public void Write(AdjustmentStack stack, double value) => _write(stack, value);

    /// <summary>Returns just this parameter to neutral, as its own undo entry.</summary>
    [RelayCommand]
    private void Reset()
    {
        if (IsModified)
        {
            _owner.SetParameter(this, 0, $"Reset {Label.ToLowerInvariant()}");
        }
    }

    /// <summary>Re-reads the value from the model after a command ran.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(ValueLabel));
        OnPropertyChanged(nameof(IsModified));
    }
}

/// <summary>A titled run of sliders — Light, Colour, Detail.</summary>
/// <param name="Name">The group heading.</param>
/// <param name="Sliders">Its rows, in order.</param>
public sealed record AdjustmentGroupViewModel(string Name, IReadOnlyList<AdjustmentSliderViewModel> Sliders);

/// <summary>
/// The inspector's <b>Adjust</b> section of doc 09 §2.4 (R11): the whole non-destructive
/// <see cref="AdjustmentStack"/> — exposure, brightness, contrast, highlights, shadows, temperature,
/// tint, saturation, vibrance, sharpen, vignette, straighten, rotate, flip and black &amp; white —
/// with a live preview, per-slider and whole-stack resets, and a before/after toggle.
///
/// <para>
/// Three properties this panel is built to keep. <b>Nothing is destructive</b>: a slider writes a
/// number onto the catalog entry and the archived original is never opened for writing (kernel §5).
/// <b>Nothing heavy runs on the dispatcher</b>: the preview is rendered by
/// <see cref="PhotoPreviewService"/> at the 1024 px tier on a background thread, latest-request-wins,
/// so a fast drag abandons the frames it has overtaken instead of queueing them (doc 09 §6).
/// And <b>a drag is one undo entry</b>: the view opens an undo gesture at thumb-press and closes it at
/// thumb-release, so every frame between folds into a single entry regardless of the clock
/// (doc 09 §4).
/// </para>
/// </summary>
public sealed partial class AdjustmentsViewModel : ObservableObject
{
    private readonly PhotoEditor _editor;
    private readonly PhotoPreviewService _previews;

    private CancellationTokenSource? _renderCancellation;
    private BitmapSource? _originalPreview;
    private bool _refreshing;

    /// <param name="editor">The undoable edit service.</param>
    /// <param name="previews">The off-thread preview renderer.</param>
    public AdjustmentsViewModel(PhotoEditor editor, PhotoPreviewService previews)
    {
        _editor = editor;
        _previews = previews;
        _editor.PhotoChanged += OnPhotoChanged;

        Groups =
        [
            new AdjustmentGroupViewModel("Light",
            [
                Slider("exposure", "Exposure", "Overall light, in camera stops.", -2, 2,
                    s => s.ExposureEv, (s, v) => s.ExposureEv = v, AdjustmentFormat.Exposure),
                Slider("brightness", "Brightness", "Lifts the midtones without crushing either end.", -1, 1,
                    s => s.Brightness, (s, v) => s.Brightness = v),
                Slider("contrast", "Contrast", "An S-curve about mid-grey; extremes compress rather than clip.", -1, 1,
                    s => s.Contrast, (s, v) => s.Contrast = v),
                Slider("highlights", "Highlights", "Recovers or lifts the bright end only.", -1, 1,
                    s => s.Highlights, (s, v) => s.Highlights = v),
                Slider("shadows", "Shadows", "Opens up or deepens the dark end only.", -1, 1,
                    s => s.Shadows, (s, v) => s.Shadows = v),
            ]),
            new AdjustmentGroupViewModel("Colour",
            [
                Slider("temperature", "Temperature", "Cool blue through to warm amber.", -1, 1,
                    s => s.Temperature, (s, v) => s.Temperature = v),
                Slider("tint", "Tint", "Green through to magenta.", -1, 1,
                    s => s.Tint, (s, v) => s.Tint = v),
                Slider("saturation", "Saturation", "Every colour, equally.", -1, 1,
                    s => s.Saturation, (s, v) => s.Saturation = v),
                Slider("vibrance", "Vibrance", "Muted colours move; skin tones stay put.", -1, 1,
                    s => s.Vibrance, (s, v) => s.Vibrance = v),
            ]),
            new AdjustmentGroupViewModel("Detail",
            [
                Slider("sharpen", "Sharpen", "Unsharp mask, kept off flat areas so skies stay clean.", 0, 1,
                    s => s.Sharpness, (s, v) => s.Sharpness = v, AdjustmentFormat.Scaled, isBipolar: false),
                Slider("vignette", "Vignette", "Darkens the corners.", 0, 1,
                    s => s.Vignette, (s, v) => s.Vignette = v, AdjustmentFormat.Scaled, isBipolar: false),
                Slider("straighten", "Straighten", "Levels a tilted horizon; the rotated wedge is cropped away.",
                    -15, 15, s => s.Straighten, (s, v) => s.Straighten = v, AdjustmentFormat.Degrees),
            ]),
        ];

        AllSliders = [.. Groups.SelectMany(g => g.Sliders)];
    }

    /// <summary>Raised whenever a fresh preview is ready, so the grid tile can follow the edit.</summary>
    public event Action<BitmapSource>? PreviewRendered;

    /// <summary>The slider groups, in panel order.</summary>
    public IReadOnlyList<AdjustmentGroupViewModel> Groups { get; }

    /// <summary>Every slider, flattened — for refreshes.</summary>
    public IReadOnlyList<AdjustmentSliderViewModel> AllSliders { get; }

    /// <summary>The photo being edited, or null.</summary>
    public Photo? Photo { get; private set; }

    /// <summary>The photo's current stack, or null when no photo is selected.</summary>
    public AdjustmentStack? Stack => Photo?.Adjustments;

    /// <summary>The live preview — the edited pixels, or the original while <see cref="ShowOriginal"/> is on.</summary>
    [ObservableProperty]
    private BitmapSource? _preview;

    /// <summary>True while a preview render is in flight.</summary>
    [ObservableProperty]
    private bool _isRendering;

    /// <summary>What just happened, for the panel's own status line.</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>The before/after toggle: while on, the preview shows the untouched original.</summary>
    [ObservableProperty]
    private bool _showOriginal;

    /// <summary>True when the photo carries any edit at all.</summary>
    public bool HasEdits => Photo is not null && !Photo.Adjustments.IsIdentity;

    /// <summary>True when this photo is being converted to black and white.</summary>
    public bool IsBlackAndWhite
    {
        get => Photo?.Adjustments.BlackAndWhite ?? false;
        set
        {
            if (Photo is not { } photo || photo.Adjustments.BlackAndWhite == value)
            {
                return;
            }

            var next = photo.Adjustments with { BlackAndWhite = value };
            _editor.SetAdjustments(photo, next, value ? "Black and white" : "Colour");
            AfterEdit();
        }
    }

    /// <summary>The extra quarter-turn beyond EXIF orientation, for the readout.</summary>
    public string RotationLabel => Photo is null || Photo.Adjustments.Rotate == 0
        ? "0°"
        : Photo.Adjustments.Rotate.ToString(CultureInfo.CurrentCulture) + "°";

    /// <summary>True when the photo is mirrored horizontally.</summary>
    public bool IsFlipped => Photo?.Adjustments.FlipHorizontal ?? false;

    /// <summary>Points the panel at a photo.</summary>
    /// <param name="photo">The photo to edit, or null to clear.</param>
    public void Attach(Photo? photo)
    {
        Photo = photo;
        _originalPreview = null;
        ShowOriginal = false;
        StatusMessage = string.Empty;
        RefreshAll();
        _ = RenderAsync();
    }

    /// <summary>
    /// Applies one parameter as an undoable edit. The coalescing key names both the photo and the
    /// parameter, so a drag folds into one entry while two different sliders stay two (doc 09 §4).
    /// </summary>
    /// <param name="slider">The parameter being written.</param>
    /// <param name="value">Its new value.</param>
    /// <param name="description">Edit-menu text; defaults to the slider's label.</param>
    public void SetParameter(AdjustmentSliderViewModel slider, double value, string? description = null)
    {
        if (_refreshing || Photo is not { } photo)
        {
            return;
        }

        var clamped = Math.Clamp(value, slider.Minimum, slider.Maximum);
        var next = photo.Adjustments with { };
        slider.Write(next, clamped);
        if (next == photo.Adjustments)
        {
            return;
        }

        _editor.SetAdjustments(
            photo, next, description ?? slider.Label, $"adjust:{photo.Id}:{slider.Id}");
        AfterEdit();
    }

    /// <summary>Opens the undo gesture for a slider drag; the view calls this at thumb-press.</summary>
    /// <param name="slider">The slider being dragged.</param>
    public IDisposable? BeginDrag(AdjustmentSliderViewModel slider) =>
        Photo is { } photo ? _editor.BeginAdjustmentGesture(photo, slider.Id) : null;

    // ================================================================= commands

    /// <summary>Returns every parameter to neutral in one undoable step.</summary>
    [RelayCommand]
    private void ResetAll()
    {
        if (Photo is not { } photo || photo.Adjustments.IsIdentity)
        {
            return;
        }

        _editor.ResetAdjustments(photo);
        AfterEdit();
        StatusMessage = "Adjustments reset. The original file was never touched.";
    }

    /// <summary>Rotates a quarter turn anticlockwise, beyond the EXIF orientation.</summary>
    [RelayCommand]
    private void RotateLeft() => Rotate(-90);

    /// <summary>Rotates a quarter turn clockwise, beyond the EXIF orientation.</summary>
    [RelayCommand]
    private void RotateRight() => Rotate(90);

    /// <summary>Mirrors the photo horizontally.</summary>
    [RelayCommand]
    private void Flip()
    {
        if (Photo is not { } photo)
        {
            return;
        }

        _editor.SetAdjustments(
            photo, photo.Adjustments with { FlipHorizontal = !photo.Adjustments.FlipHorizontal }, "Flip");
        AfterEdit();
    }

    private void Rotate(int degrees)
    {
        if (Photo is not { } photo)
        {
            return;
        }

        var turns = ((photo.Adjustments.Rotate + degrees) / 90 % 4 + 4) % 4;
        _editor.SetAdjustments(photo, photo.Adjustments with { Rotate = turns * 90 }, "Rotate");
        AfterEdit();
    }

    // ================================================================= preview

    partial void OnShowOriginalChanged(bool value) => _ = RenderAsync();

    /// <summary>
    /// Renders the preview off the UI thread, cancelling whatever it supersedes. The identity render
    /// is kept, so flicking the before/after toggle is instant after the first time.
    /// </summary>
    public async Task RenderAsync()
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

        if (ShowOriginal && _originalPreview is not null)
        {
            Preview = _originalPreview;
            return;
        }

        var cancellation = new CancellationTokenSource();
        _renderCancellation = cancellation;
        IsRendering = true;

        try
        {
            // A short settle keeps a fast drag from starting a pipeline pass per pointer frame; the
            // cancellation above means the ones it does start are abandoned as soon as they are stale.
            await Task.Delay(40, cancellation.Token).ConfigureAwait(true);

            var bitmap = await _previews
                .RenderAsync(photo, ShowOriginal ? null : photo.Adjustments, cancellation.Token)
                .ConfigureAwait(true);

            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            if (ShowOriginal)
            {
                _originalPreview = bitmap;
            }

            Preview = bitmap;
            PreviewRendered?.Invoke(bitmap);
        }
        catch (OperationCanceledException)
        {
            // Superseded — the newer request owns the preview.
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

    private void AfterEdit()
    {
        ShowOriginal = false;
        _ = RenderAsync();
    }

    private void OnPhotoChanged(Photo photo)
    {
        if (!ReferenceEquals(photo, Photo))
        {
            return;
        }

        RefreshAll();
        _ = RenderAsync();
    }

    private void RefreshAll()
    {
        // Undo and redo write straight into the model, so the whole panel re-reads rather than
        // trying to guess which parameter moved. The flag keeps the sliders' two-way bindings from
        // writing the value they were just handed back into a new command.
        _refreshing = true;
        try
        {
            foreach (var slider in AllSliders)
            {
                slider.Refresh();
            }

            OnPropertyChanged(nameof(Stack));
            OnPropertyChanged(nameof(HasEdits));
            OnPropertyChanged(nameof(IsBlackAndWhite));
            OnPropertyChanged(nameof(IsFlipped));
            OnPropertyChanged(nameof(RotationLabel));
        }
        finally
        {
            _refreshing = false;
        }
    }

    private AdjustmentSliderViewModel Slider(
        string id,
        string label,
        string hint,
        double minimum,
        double maximum,
        Func<AdjustmentStack, double> read,
        Action<AdjustmentStack, double> write,
        AdjustmentFormat format = AdjustmentFormat.Scaled,
        bool isBipolar = true) =>
        new(this, id, label, hint, minimum, maximum, read, write, format, isBipolar);
}
