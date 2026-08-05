using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoBook.App.Services;
using PhotoBook.Core.Model;

namespace PhotoBook.App.ViewModels.Photos;

/// <summary>One cell of the month grid in the re-date dialog.</summary>
public sealed partial class CalendarDayViewModel : ObservableObject
{
    /// <param name="date">The day this cell stands for.</param>
    /// <param name="isCurrentMonth">False for the leading/trailing days of the neighbouring months.</param>
    public CalendarDayViewModel(DateOnly date, bool isCurrentMonth)
    {
        Date = date;
        IsCurrentMonth = isCurrentMonth;
    }

    /// <summary>The day this cell stands for.</summary>
    public DateOnly Date { get; }

    /// <summary>The number drawn in the cell.</summary>
    public string Label => Date.Day.ToString(CultureInfo.CurrentCulture);

    /// <summary>False for the padding days either side of the month, which are dimmed.</summary>
    public bool IsCurrentMonth { get; }

    /// <summary>True for today, which gets a quiet marker.</summary>
    public bool IsToday => Date == DateOnly.FromDateTime(DateTime.Now);

    /// <summary>True for the day the dialog is currently set to.</summary>
    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// The <em>Change date…</em> dialog of doc 09 §2.1 (R6): the photo's current date and where it came
/// from, a calendar and a time field, and — the part that matters — a live sentence saying exactly
/// what the new date will do.
///
/// <para>
/// Re-dating is the one edit whose consequence is invisible in the grid it is performed from: a date
/// in another month moves the photo to that Chapter's Unplaced bin, and a date outside the book's
/// year moves it to the Outside-book tray, where a user who was not told would reasonably conclude it
/// had been deleted. So the outcome is computed by <see cref="PhotoEditor.Preview"/> on every
/// keystroke and shown before <em>Change date</em> is ever pressed, including how many Slots will
/// become empty amber holes.
/// </para>
///
/// <para>Setting a date by hand always clears <c>dateUncertain</c> — the user has spoken (doc 09 §2.1).</para>
/// </summary>
public sealed partial class ChangeDateViewModel : ObservableObject
{
    private readonly PhotoEditor _editor;
    private DateTime _value;

    /// <param name="editor">The undoable edit service.</param>
    /// <param name="photo">The photo being re-dated.</param>
    public ChangeDateViewModel(PhotoEditor editor, Photo photo)
    {
        ArgumentNullException.ThrowIfNull(photo);

        _editor = editor;
        Photo = photo;
        _value = photo.TakenAt;
        _timeText = photo.TakenAt.ToString("h:mm tt", CultureInfo.CurrentCulture);
        DisplayMonth = new DateOnly(photo.TakenAt.Year, photo.TakenAt.Month, 1);

        WeekdayNames =
        [
            .. Enumerable.Range(0, 7).Select(i =>
                CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames[
                    ((int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek + i) % 7])
        ];

        BuildCalendar();
        UpdateOutcome();
    }

    /// <summary>Raised when the dialog is done; true means the date was changed.</summary>
    public event Action<bool>? CloseRequested;

    /// <summary>The photo being re-dated.</summary>
    public Photo Photo { get; }

    /// <summary>The file name, for the dialog's subtitle.</summary>
    public string FileName => Photo.OriginalFileName;

    /// <summary>The date the photo carries today.</summary>
    public string CurrentDateLabel => Photo.TakenAt.ToString("dddd, d MMMM yyyy · h:mm tt", CultureInfo.CurrentCulture);

    /// <summary>Where that date came from — EXIF, OneDrive, the file's mtime, or the user (kernel §10).</summary>
    public string ProvenanceLabel => Photo.DateSource switch
    {
        DateSource.Exif => "From the photo's own EXIF capture time.",
        DateSource.Graph => "From OneDrive's capture time for this item.",
        DateSource.User => "Set by you.",
        _ => "Guessed from the file's last-modified time — this is often the copy date, not the capture date.",
    };

    /// <summary>True when the current date is only a guess, so the warning chip shows.</summary>
    public bool IsUncertain => Photo.DateUncertain;

    /// <summary>The weekday headers, in the culture's own week order.</summary>
    public IReadOnlyList<string> WeekdayNames { get; }

    /// <summary>The 42 cells of the visible month.</summary>
    public ObservableCollection<CalendarDayViewModel> Days { get; } = [];

    /// <summary>The month the calendar is showing.</summary>
    public DateOnly DisplayMonth { get; private set; }

    /// <summary>That month's name, for the calendar header.</summary>
    public string DisplayMonthLabel => DisplayMonth.ToString("MMMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>The time of day, as typed. Accepts "9:30 am", "21:30" and "2130".</summary>
    [ObservableProperty]
    private string _timeText;

    /// <summary>True when <see cref="TimeText"/> could not be parsed; the field shows the error state.</summary>
    [ObservableProperty]
    private bool _isTimeInvalid;

    /// <summary>The full new date and time, as it stands.</summary>
    public DateTime Value => _value;

    /// <summary>The new date, spelled out.</summary>
    public string ValueLabel => _value.ToString("dddd, d MMMM yyyy · h:mm tt", CultureInfo.CurrentCulture);

    /// <summary>What this date will do — the whole point of the dialog (doc 09 §2.1).</summary>
    [ObservableProperty]
    private string _outcomeMessage = string.Empty;

    /// <summary>True when the new date leaves the Chapter, so the outcome reads as a warning.</summary>
    [ObservableProperty]
    private bool _outcomeIsMove;

    /// <summary>True when the new date leaves the book's year entirely.</summary>
    [ObservableProperty]
    private bool _outcomeLeavesBook;

    /// <summary>False while the time field cannot be read, or the date is unchanged.</summary>
    public bool CanApply => !IsTimeInvalid && _value != Photo.TakenAt;

    /// <summary>Shows the previous month.</summary>
    [RelayCommand]
    private void PreviousMonth() => ShowMonth(DisplayMonth.AddMonths(-1));

    /// <summary>Shows the next month.</summary>
    [RelayCommand]
    private void NextMonth() => ShowMonth(DisplayMonth.AddMonths(1));

    /// <summary>Jumps the calendar to today without selecting it.</summary>
    [RelayCommand]
    private void ShowToday() => ShowMonth(DateOnly.FromDateTime(DateTime.Now));

    /// <summary>Picks a day; the time of day is kept.</summary>
    /// <param name="day">The cell that was clicked.</param>
    [RelayCommand]
    private void SelectDay(CalendarDayViewModel? day)
    {
        if (day is null)
        {
            return;
        }

        _value = day.Date.ToDateTime(TimeOnly.FromDateTime(_value));
        if (day.Date.Month != DisplayMonth.Month || day.Date.Year != DisplayMonth.Year)
        {
            ShowMonth(day.Date);
            return;
        }

        SyncSelection();
        UpdateOutcome();
    }

    /// <summary>Applies the re-date and closes.</summary>
    [RelayCommand]
    private void Apply()
    {
        if (!CanApply)
        {
            return;
        }

        Outcome = _editor.ChangeDate(Photo, _value);
        CloseRequested?.Invoke(true);
    }

    /// <summary>Closes without changing anything.</summary>
    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    /// <summary>What the applied change actually did, for the caller's toast.</summary>
    public DateChangeOutcome? Outcome { get; private set; }

    partial void OnTimeTextChanged(string value)
    {
        if (TryParseTime(value, out var time))
        {
            IsTimeInvalid = false;
            _value = DateOnly.FromDateTime(_value).ToDateTime(time);
            UpdateOutcome();
        }
        else
        {
            IsTimeInvalid = true;
            OnPropertyChanged(nameof(CanApply));
        }
    }

    /// <summary>
    /// Tolerant time parsing: the field is a text box rather than three spinners, so it has to accept
    /// what people actually type.
    /// </summary>
    /// <param name="text">The typed text.</param>
    /// <param name="time">The parsed time of day.</param>
    public static bool TryParseTime(string? text, out TimeOnly time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (TimeOnly.TryParse(trimmed, CultureInfo.CurrentCulture, out time) ||
            TimeOnly.TryParse(trimmed, CultureInfo.InvariantCulture, out time))
        {
            return true;
        }

        // "2130" and "930" — a keypad habit worth honouring.
        if (trimmed.All(char.IsDigit) && trimmed.Length is 3 or 4)
        {
            var hours = int.Parse(trimmed[..^2], CultureInfo.InvariantCulture);
            var minutes = int.Parse(trimmed[^2..], CultureInfo.InvariantCulture);
            if (hours < 24 && minutes < 60)
            {
                time = new TimeOnly(hours, minutes);
                return true;
            }
        }

        return false;
    }

    private void ShowMonth(DateOnly month)
    {
        DisplayMonth = new DateOnly(month.Year, month.Month, 1);
        OnPropertyChanged(nameof(DisplayMonth));
        OnPropertyChanged(nameof(DisplayMonthLabel));
        BuildCalendar();
        UpdateOutcome();
    }

    private void BuildCalendar()
    {
        Days.Clear();

        var firstDayOfWeek = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var offset = (((int)DisplayMonth.DayOfWeek - firstDayOfWeek) % 7 + 7) % 7;
        var start = DisplayMonth.AddDays(-offset);

        for (var i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);
            Days.Add(new CalendarDayViewModel(date, date.Month == DisplayMonth.Month && date.Year == DisplayMonth.Year));
        }

        SyncSelection();
    }

    private void SyncSelection()
    {
        var selected = DateOnly.FromDateTime(_value);
        foreach (var day in Days)
        {
            day.IsSelected = day.Date == selected;
        }
    }

    private void UpdateOutcome()
    {
        var outcome = _editor.Preview(Photo, _value);
        OutcomeMessage = outcome.Message;
        OutcomeIsMove = outcome.Scope != DateChangeScope.SameChapter;
        OutcomeLeavesBook = outcome.Scope == DateChangeScope.OutsideBookYear;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(ValueLabel));
        OnPropertyChanged(nameof(CanApply));
    }
}
