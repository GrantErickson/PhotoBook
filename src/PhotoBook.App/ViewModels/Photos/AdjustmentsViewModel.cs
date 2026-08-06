using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoBook.App.Services;
using PhotoBook.Core.Model;
using PhotoBook.Imaging;

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

/// <summary>
/// A titled, collapsible run of sliders — Light, Colour, Detail. The whole stack is long enough that
/// showing every row at once buries the controls a photo usually needs, so a group can be folded
/// away and the panel remembers which are open for the session.
/// </summary>
public sealed partial class AdjustmentGroupViewModel : ObservableObject
{
    /// <summary>Creates a group.</summary>
    /// <param name="name">The group heading.</param>
    /// <param name="sliders">Its rows, in order.</param>
    /// <param name="isExpanded">Whether it starts open.</param>
    public AdjustmentGroupViewModel(
        string name, IReadOnlyList<AdjustmentSliderViewModel> sliders, bool isExpanded = true)
    {
        Name = name;
        Sliders = sliders;
        _isExpanded = isExpanded;
    }

    /// <summary>The group heading.</summary>
    public string Name { get; }

    /// <summary>Its rows, in order.</summary>
    public IReadOnlyList<AdjustmentSliderViewModel> Sliders { get; }

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>How many rows in this group are away from their default, for the collapsed summary.</summary>
    public int TouchedCount => Sliders.Count(s => Math.Abs(s.Value) > 1e-9);

    /// <summary>"2 changed" when the group is folded and holds edits, otherwise blank.</summary>
    public string TouchedLabel => TouchedCount == 0
        ? string.Empty
        : string.Create(CultureInfo.CurrentCulture, $"{TouchedCount} changed");

    /// <summary>Re-reads the summary after the sliders were refreshed.</summary>
    public void RefreshSummary()
    {
        OnPropertyChanged(nameof(TouchedCount));
        OnPropertyChanged(nameof(TouchedLabel));
    }
}

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
                Slider("whites", "Whites", "The white point itself — how close the brightest tones come to clipping.",
                    -1, 1, s => s.Whites, (s, v) => s.Whites = v),
                Slider("blacks", "Blacks", "The black point itself — crush for depth, lift for a faded look.",
                    -1, 1, s => s.Blacks, (s, v) => s.Blacks = v),
            ]),
            // Light open, the rest folded: the panel should read as a short list you expand into,
            // not a wall of sliders. A folded group says how many of its rows carry edits.
            new AdjustmentGroupViewModel("Colour", isExpanded: false, sliders:
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
            new AdjustmentGroupViewModel("Detail", isExpanded: false, sliders:
            [
                Slider("clarity", "Clarity", "Mid-tone local contrast — depth for a flat, hazy frame; soften to flatter skin.",
                    -1, 1, s => s.Clarity, (s, v) => s.Clarity = v),
                Slider("noise", "Noise reduction", "Smooths the speckle of an indoor or evening shot. Runs before sharpening.",
                    0, 1, s => s.NoiseReduction, (s, v) => s.NoiseReduction = v, AdjustmentFormat.Scaled, isBipolar: false),
                Slider("sharpen", "Sharpen", "Unsharp mask, kept off flat areas so skies stay clean.", 0, 1,
                    s => s.Sharpness, (s, v) => s.Sharpness = v, AdjustmentFormat.Scaled, isBipolar: false),
                Slider("vignette", "Vignette", "Darkens the corners.", 0, 1,
                    s => s.Vignette, (s, v) => s.Vignette = v, AdjustmentFormat.Scaled, isBipolar: false),
                Slider("straighten", "Straighten", "Levels a tilted horizon; the rotated wedge is cropped away.",
                    -StraightenMath.MaxDegrees, StraightenMath.MaxDegrees,
                    s => s.Straighten, (s, v) => s.Straighten = v, AdjustmentFormat.Degrees),
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

    /// <summary>
    /// True while the eyedropper is armed: the next click on the preview names a neutral and sets
    /// temperature and tint from it.
    /// </summary>
    [ObservableProperty]
    private bool _isPickingWhiteBalance;

    /// <summary>True when temperature or tint is off neutral.</summary>
    public bool HasWhiteBalance =>
        Photo is { } photo && (photo.Adjustments.Temperature != 0 || photo.Adjustments.Tint != 0);

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
        IsPickingWhiteBalance = false;
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

    /// <summary>
    /// Arms or disarms the white-balance eyedropper (doc 05 "temperature/tint"). It is a toggle rather
    /// than a modal mode so that changing your mind costs the same click that started it.
    /// </summary>
    [RelayCommand]
    private void ToggleWhiteBalancePicker()
    {
        if (Photo is null)
        {
            return;
        }

        IsPickingWhiteBalance = !IsPickingWhiteBalance;
        StatusMessage = IsPickingWhiteBalance
            ? "Click something that should be neutral — a white wall, grey pavement, the white of an eye."
            : string.Empty;
    }

    /// <summary>
    /// Sets temperature and tint from a point the user says is neutral, as <b>one</b> undo entry.
    /// <para>
    /// The sample is read from the preview the user actually clicked, which already carries the current
    /// white balance; <see cref="WhiteBalance.FromNeutral"/> divides that back out, so the result is an
    /// absolute setting and clicking the same neutral twice changes nothing the second time. The
    /// average of a small neighbourhood is used rather than the single pixel under the cursor, because
    /// one pixel of JPEG noise is not a colour.
    /// </para>
    /// </summary>
    /// <param name="x">Horizontal position in the preview, normalized to <c>[0,1]</c>.</param>
    /// <param name="y">Vertical position in the preview, normalized to <c>[0,1]</c>.</param>
    public void PickWhiteBalanceAt(double x, double y)
    {
        IsPickingWhiteBalance = false;

        if (Photo is not { } photo || Preview is not { } preview)
        {
            return;
        }

        if (photo.Adjustments.BlackAndWhite)
        {
            StatusMessage = "This photo is black and white — turn colour back on before picking a neutral.";
            return;
        }

        if (SampleAverage(preview, x, y) is not { } sample)
        {
            StatusMessage = "Could not read that point.";
            return;
        }

        if (sample.Red < 12 || sample.Green < 12 || sample.Blue < 12 ||
            sample.Red > 250 || sample.Green > 250 || sample.Blue > 250)
        {
            StatusMessage = "That area is too dark or too blown out to read a colour from. Try a mid-grey.";
            return;
        }

        // ShowOriginal renders the untouched decode, so the sample carries no current gains to remove.
        var appliedTemperature = ShowOriginal ? 0 : photo.Adjustments.Temperature;
        var appliedTint = ShowOriginal ? 0 : photo.Adjustments.Tint;

        var (temperature, tint) = WhiteBalance.FromNeutral(
            sample.Red, sample.Green, sample.Blue, appliedTemperature, appliedTint);

        _editor.SetAdjustments(
            photo, photo.Adjustments with { Temperature = temperature, Tint = tint }, "White balance");
        AfterEdit();
        StatusMessage =
            $"White balance set from that point: temperature {temperature * 100:+0;-0;0}, tint {tint * 100:+0;-0;0}.";
    }

    /// <summary>Returns temperature and tint to neutral in one undoable step.</summary>
    [RelayCommand]
    private void ResetWhiteBalance()
    {
        if (Photo is not { } photo || !HasWhiteBalance)
        {
            return;
        }

        _editor.SetAdjustments(photo, photo.Adjustments with { Temperature = 0, Tint = 0 }, "Reset white balance");
        AfterEdit();
    }

    /// <summary>
    /// Opens the interactive straightening tool (R6/R11): a large preview where the angle comes from a
    /// line the user drags along something that should be level, instead of from guessing at a slider.
    /// The tool writes the same <c>Straighten</c> value the slider does.
    /// </summary>
    [RelayCommand]
    private void OpenStraightenTool()
    {
        if (Photo is not { } photo)
        {
            return;
        }

        var model = new StraightenToolViewModel(_editor, _previews);
        model.Attach(photo);

        var window = new Views.Photos.StraightenToolWindow(model)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };

        window.ShowDialog();

        RefreshAll();
        _ = RenderAsync();
        if (model.StatusMessage.Length > 0)
        {
            StatusMessage = model.StatusMessage;
        }
    }

    /// <summary>
    /// The mean colour of a small neighbourhood of the preview, or null when the point is outside it.
    /// Channels come back on the 0–255 scale the bitmap stores.
    /// </summary>
    private static (double Red, double Green, double Blue)? SampleAverage(BitmapSource preview, double x, double y)
    {
        if (preview.PixelWidth <= 0 || preview.PixelHeight <= 0 ||
            x is < 0 or > 1 || y is < 0 or > 1)
        {
            return null;
        }

        // Normalize the format once: the preview is Bgra32 today, but a converted bitmap makes the
        // stride arithmetic below true whatever the source turns out to be.
        var source = preview.Format == PixelFormats.Bgra32
            ? preview
            : new FormatConvertedBitmap(preview, PixelFormats.Bgra32, null, 0);

        const int Radius = 2;
        var centreX = (int)Math.Round(x * (source.PixelWidth - 1));
        var centreY = (int)Math.Round(y * (source.PixelHeight - 1));
        var left = Math.Clamp(centreX - Radius, 0, source.PixelWidth - 1);
        var top = Math.Clamp(centreY - Radius, 0, source.PixelHeight - 1);
        var width = Math.Min((2 * Radius) + 1, source.PixelWidth - left);
        var height = Math.Min((2 * Radius) + 1, source.PixelHeight - top);

        var stride = width * 4;
        var pixels = new byte[stride * height];
        try
        {
            source.CopyPixels(new Int32Rect(left, top, width, height), pixels, stride, 0);
        }
        catch (ArgumentException)
        {
            return null;
        }

        double red = 0, green = 0, blue = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            blue += pixels[i];
            green += pixels[i + 1];
            red += pixels[i + 2];
        }

        var count = pixels.Length / 4.0;
        return (red / count, green / count, blue / count);
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

            foreach (var group in Groups)
            {
                group.RefreshSummary();
            }

            OnPropertyChanged(nameof(Stack));
            OnPropertyChanged(nameof(HasEdits));
            OnPropertyChanged(nameof(HasWhiteBalance));
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
