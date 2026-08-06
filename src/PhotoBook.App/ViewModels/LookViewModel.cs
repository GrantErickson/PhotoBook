using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoBook.App.Services;
using PhotoBook.Core.Model;
using PhotoBook.Imaging.Auto;

namespace PhotoBook.App.ViewModels;

/// <summary>
/// The book's look settings — the guidance auto-adjust applies on top of what it measured, so a year
/// of photos taken on different phones in different light comes out as one book rather than as a
/// hundred independently-corrected pictures.
///
/// <para>
/// Unlike the page size and the print profile beside it, these apply the moment they are moved and
/// have no apply/revert cycle. There is nothing to stage: changing a value does not alter a single
/// photo until <em>Auto-adjust all</em> is next run, and the counts shown there say exactly how many
/// photos the change has put out of date.
/// </para>
/// </summary>
public sealed partial class LookViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly AutoAdjustRunner _autoAdjust;
    private bool _loading;

    /// <summary>Creates the panel over the open session.</summary>
    /// <param name="session">The single writer.</param>
    /// <param name="autoAdjust">The runner, for the "how many photos would change" readout.</param>
    public LookViewModel(ProjectSession session, AutoAdjustRunner autoAdjust)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(autoAdjust);
        _session = session;
        _autoAdjust = autoAdjust;
        _session.Changed += Load;
        Load();
    }

    /// <summary>True once a book is open and the settings mean something.</summary>
    public bool IsAvailable => _session.Book is not null;

    [ObservableProperty]
    private double _strength = LookProfile.DefaultStrength;

    [ObservableProperty]
    private double _brightness;

    [ObservableProperty]
    private double _warmth;

    [ObservableProperty]
    private double _contrast;

    [ObservableProperty]
    private double _saturation;

    [ObservableProperty]
    private bool _straighten = true;

    [ObservableProperty]
    private bool _adjustOnImport;

    /// <summary>"Subtle", "Normal" or "Strong" — the three stops the dial is really made of.</summary>
    public string StrengthLabel => Strength switch
    {
        <= 0.01 => "Off",
        < 0.63 => "Subtle",
        < 0.88 => "Normal",
        _ => "Strong",
    };

    /// <summary>How many photos are on auto and how many the user owns, in one sentence.</summary>
    public string Summary
    {
        get
        {
            if (_session.Book is null) return string.Empty;

            var plan = _autoAdjust.Describe();
            var onAuto = plan.Automatic;
            var mine = plan.Manual;
            var waiting = plan.Untouched;

            var parts = new List<string>(3);
            if (onAuto > 0) parts.Add($"{onAuto} on auto");
            if (mine > 0) parts.Add($"{mine} edited by hand");
            if (waiting > 0) parts.Add($"{waiting} not adjusted yet");

            return parts.Count == 0 ? "No photos yet." : string.Join(" · ", parts);
        }
    }

    /// <summary>How many automatic photos these settings have put out of date.</summary>
    public string StaleSummary
    {
        get
        {
            if (_session.Book is null) return string.Empty;

            var plan = _autoAdjust.Describe();
            var stale = plan.Automatic - plan.UpToDate;
            return stale <= 0
                ? "Every automatic photo matches these settings."
                : $"{stale} automatic photo{(stale == 1 ? "" : "s")} would change if you ran auto-adjust now.";
        }
    }

    /// <summary>Returns every setting to its default.</summary>
    [RelayCommand]
    private void Reset()
    {
        var defaults = new LookProfile();
        _loading = true;
        try
        {
            Strength = defaults.Strength;
            Brightness = defaults.Brightness;
            Warmth = defaults.Warmth;
            Contrast = defaults.Contrast;
            Saturation = defaults.Saturation;
            Straighten = defaults.Straighten;
            AdjustOnImport = defaults.AdjustOnImport;
        }
        finally
        {
            _loading = false;
        }

        Commit();
    }

    /// <summary>Re-reads the open book, e.g. after one is opened or closed.</summary>
    public void Load()
    {
        var look = _session.Book?.Look ?? new LookProfile();

        _loading = true;
        try
        {
            Strength = look.Strength;
            Brightness = look.Brightness;
            Warmth = look.Warmth;
            Contrast = look.Contrast;
            Saturation = look.Saturation;
            Straighten = look.Straighten;
            AdjustOnImport = look.AdjustOnImport;
        }
        finally
        {
            _loading = false;
        }

        Announce();
    }

    partial void OnStrengthChanged(double value) => Commit();

    partial void OnBrightnessChanged(double value) => Commit();

    partial void OnWarmthChanged(double value) => Commit();

    partial void OnContrastChanged(double value) => Commit();

    partial void OnSaturationChanged(double value) => Commit();

    partial void OnStraightenChanged(bool value) => Commit();

    partial void OnAdjustOnImportChanged(bool value) => Commit();

    /// <summary>
    /// Writes straight to the book. Deliberately not undoable: these are settings, not an edit to the
    /// work, and nothing in the book has changed yet — the undo entry that matters is the auto-adjust
    /// run they eventually drive, which is already one composite step.
    /// </summary>
    private void Commit()
    {
        if (_loading || _session.Book is not { } book) return;

        book.Look = new LookProfile
        {
            Strength = Math.Clamp(Strength, 0, 1),
            Brightness = Math.Clamp(Brightness, -1, 1),
            Warmth = Math.Clamp(Warmth, -1, 1),
            Contrast = Math.Clamp(Contrast, -1, 1),
            Saturation = Math.Clamp(Saturation, -1, 1),
            Straighten = Straighten,
            AdjustOnImport = AdjustOnImport,
        };

        _session.MarkDirty();
        Announce();
    }

    private void Announce()
    {
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(StrengthLabel));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(StaleSummary));
    }
}
