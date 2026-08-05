using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoBook.App.Services;
using PhotoBook.Core.Model;
using PhotoBook.Core.Persistence;
using PhotoBook.Core.Templates;
using PhotoBook.Engine;

namespace PhotoBook.App.ViewModels;

/// <summary>
/// One trim on offer, with what the shipped template library can actually build at it (R19). The
/// count is the whole point of the row: page size is data everywhere except the template library,
/// which is authored per size and never stretched, so a size can be perfectly printable and still
/// have nothing to lay pages out with.
/// </summary>
public sealed partial class PageSizeOptionViewModel : ObservableObject
{
    /// <summary>Wraps one measured size.</summary>
    public PageSizeOptionViewModel(PageSizeAvailability availability) => Availability = availability;

    /// <summary>What the library holds at this size.</summary>
    public PageSizeAvailability Availability { get; }

    /// <summary>The size id stored in <c>book.json</c>.</summary>
    public string Id => Availability.PageSizeId;

    /// <summary>"11 × 8.5 in landscape".</summary>
    public string DisplayName => Availability.Size.DisplayName;

    /// <summary>"60 layouts · up to 8 photos a page", or "No layouts authored yet".</summary>
    public string Summary => Availability.Summary;

    /// <summary>True when a whole chapter could be laid out here.</summary>
    public bool IsUsable => Availability.CanLayOutChapter;

    /// <summary>True for the size the book is on right now.</summary>
    [ObservableProperty]
    private bool _isCurrent;
}

/// <summary>One print profile on offer. Profiles are data (doc 12); this row summarises one.</summary>
/// <param name="Profile">The profile itself.</param>
public sealed record PrintProfileOptionViewModel(PrintProfile Profile)
{
    /// <summary>The profile id stored in <c>book.json</c>.</summary>
    public string Id => Profile.Id;

    /// <summary>Its display name.</summary>
    public string Name => Profile.Name;

    /// <summary>"4 page sizes · 0.125 in bleed · 300 DPI" — the numbers that decide a print order.</summary>
    public string Summary =>
        $"{Profile.PageSizes.Count} page size{(Profile.PageSizes.Count == 1 ? "" : "s")} · " +
        $"{Profile.BleedIn.ToString("0.###", CultureInfo.CurrentCulture)} in bleed · " +
        $"{Profile.ImageDpi} DPI · {Profile.ColorIntent}";
}

/// <summary>
/// The open book's own settings: title, year, page size, print profile and the layout seed (R3, R19,
/// kernel §7).
/// <para>
/// It is a staging form, not a live panel. Three of these five fields have consequences that reach
/// past the field — a year move takes the chapters with it and strands photos in the Outside-book
/// tray, a page-size move invalidates every template a page is built on, a new seed rearranges
/// everything — so the panel shows what each change <em>would</em> do while the user is still
/// deciding, and nothing touches the model until Apply. Apply commits the lot as a single undo
/// entry, including the layout run if the user accepted the offer of one, because taking back the
/// setting has to take back the pages it forced.
/// </para>
/// </summary>
public sealed partial class BookSettingsViewModel : ObservableObject
{
    /// <summary>The earliest year a book may cover — photography predates it, family albums do not.</summary>
    public const int MinYear = 1900;

    /// <summary>The latest year a book may cover.</summary>
    public const int MaxYear = 2200;

    private readonly ProjectSession _session;
    private readonly UndoStack _undo;
    private readonly JobQueue _jobs;
    private readonly ChapterLayoutRunner _runner;

    private string _originalTitle = string.Empty;
    private int _originalYear;
    private string _originalPageSizeId = PageGeometry.DefaultPageSizeId;
    private string _originalProfileId = PrintProfile.GenericId;
    private ulong _originalSeed;
    private ulong _seed;

    /// <summary>Creates the panel over the open session.</summary>
    /// <param name="session">The single writer.</param>
    /// <param name="undo">The book's undo history.</param>
    /// <param name="jobs">The background queue the optional layout run uses.</param>
    public BookSettingsViewModel(ProjectSession session, UndoStack undo, JobQueue jobs)
    {
        _session = session;
        _undo = undo;
        _jobs = jobs;
        _runner = new ChapterLayoutRunner(session);
        Reload();
    }

    /// <summary>Raised when the dialog should close; true when settings were applied.</summary>
    public event Action<bool>? CloseRequested;

    /// <summary>Raised after a chapter's pages were rebuilt, so the shell can refresh that chapter.</summary>
    public event Action<int>? ChapterChanged;

    /// <summary>Raised after Apply, so the shell can re-read the title, year and page previews.</summary>
    public event Action? Applied;

    // ------------------------------------------------------------------ the draft

    /// <summary>The book's display title.</summary>
    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>The calendar year the book covers (R3).</summary>
    [ObservableProperty]
    private int _year;

    /// <summary>The trim on offer, one row per size the print profile prints.</summary>
    public ObservableCollection<PageSizeOptionViewModel> PageSizes { get; } = [];

    /// <summary>The profiles shipped with the app.</summary>
    public ObservableCollection<PrintProfileOptionViewModel> Profiles { get; } = [];

    /// <summary>The drafted page size.</summary>
    [ObservableProperty]
    private PageSizeOptionViewModel? _selectedPageSize;

    /// <summary>The drafted print profile.</summary>
    [ObservableProperty]
    private PrintProfileOptionViewModel? _selectedProfile;

    /// <summary>True while Apply is running the engine.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>What Apply is doing, or the last thing it did.</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>
    /// Whether Apply should follow the settings change with a layout run. Offered — never assumed —
    /// because a re-layout replaces pages the user may have arranged deliberately (R16, doc 09 §3.8).
    /// </summary>
    [ObservableProperty]
    private bool _relayoutAfterApply;

    /// <summary>
    /// Whether that run may also replace Pinned pages. A page-size move breaks a pinned page exactly
    /// as badly as an unpinned one, so this defaults on for a size change and off for a re-seed.
    /// Detached pages are never included, whatever this says (R15).
    /// </summary>
    [ObservableProperty]
    private bool _relayoutIncludesPinned;

    /// <summary>The seed as text, so a known seed can be typed back in to reproduce a book (kernel §7).</summary>
    public string SeedText
    {
        get => _seed.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                parsed != _seed)
            {
                _seed = parsed;
                OnPropertyChanged();
                OnDraftChanged();
            }
        }
    }

    /// <summary>The drafted seed.</summary>
    public ulong Seed => _seed;

    // ------------------------------------------------------------------ derived state

    /// <summary>True when a project is open and there is something to edit.</summary>
    public bool IsOpen => _session.IsOpen;

    /// <summary>True when the draft differs from the book.</summary>
    public bool HasChanges =>
        TitleChanged || YearChanged || PageSizeChanged || ProfileChanged || SeedChanged;

    /// <summary>True when the title has been edited.</summary>
    public bool TitleChanged => !string.Equals(Title.Trim(), _originalTitle, StringComparison.Ordinal);

    /// <summary>True when the year has been edited.</summary>
    public bool YearChanged => Year != _originalYear;

    /// <summary>True when the page size has been edited.</summary>
    public bool PageSizeChanged =>
        SelectedPageSize is { } size && !string.Equals(size.Id, _originalPageSizeId, StringComparison.Ordinal);

    /// <summary>True when the print profile has been edited.</summary>
    public bool ProfileChanged =>
        SelectedProfile is { } profile && !string.Equals(profile.Id, _originalProfileId, StringComparison.Ordinal);

    /// <summary>True when the seed has been re-rolled or typed over.</summary>
    public bool SeedChanged => _seed != _originalSeed;

    /// <summary>Why Apply is disabled, or null when it is fine.</summary>
    public string? Blocker
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Title))
            {
                return "A book needs a title.";
            }

            if (Year is < MinYear or > MaxYear)
            {
                return $"A book covers one calendar year between {MinYear} and {MaxYear} (R3).";
            }

            // Short on purpose: the full explanation of why a trim has no layouts is already on
            // screen under the page-size list, and repeating it in the footer buries the buttons.
            return PageSizeChanged && SelectedPageSize is { IsUsable: false } size
                ? $"{size.DisplayName} has no page layouts yet, so no book can be built at that size."
                : null;
        }
    }

    /// <summary>True when there is a valid change to commit.</summary>
    public bool CanApply => IsOpen && !IsBusy && HasChanges && Blocker is null;

    /// <summary>
    /// What the drafted year does to the book: the chapters move with it, and photos dated outside it
    /// go to the Outside-book tray rather than vanishing (R6).
    /// </summary>
    public string YearNotice
    {
        get
        {
            if (!YearChanged)
            {
                var stranded = _session.OutsideYearNote(_originalYear);
                return stranded ?? $"Every month of {_originalYear} is a chapter of this book (R3).";
            }

            var text = $"All twelve chapters move to {Year}. ";
            var outside = _session.OutsideYearNote(Year);
            text += outside
                    ?? (_session.PhotoCount == 0
                        ? "There are no photos yet, so nothing is stranded."
                        : $"Every photo in the book is dated inside {Year}, so nothing is stranded.");

            var reclaimed = _session.PhotosOutside(_originalYear) - _session.PhotosOutside(Year);
            if (reclaimed > 0)
            {
                text += $" {reclaimed} photo{(reclaimed == 1 ? "" : "s")} currently in the " +
                        "Outside-book tray would come back into the book.";
            }

            return text;
        }
    }

    /// <summary>What the drafted page size costs, measured against the pages that already exist (R19).</summary>
    public PageSizeChange PageSizeImpact => PageSizeChange.Inspect(
        _session.Chapters,
        _session.FindTemplate,
        _originalPageSizeId,
        SelectedPageSize?.Id ?? _originalPageSizeId,
        SelectedPageSize?.Availability ?? CurrentAvailability());

    /// <summary>The sentence under the page-size list; empty when the size has not moved.</summary>
    public string PageSizeNotice
    {
        get
        {
            var impact = PageSizeImpact;
            if (impact.IsChange)
            {
                return impact.Warning;
            }

            return impact.Availability.Blocker
                   ?? $"{impact.Availability.TemplateCount} layouts are authored for this trim. " +
                      "Other sizes print fine but need their own layouts — templates are never " +
                      "stretched across page shapes (doc 07).";
        }
    }

    /// <summary>The sentence under the seed field.</summary>
    public string SeedNotice => SeedChanged
        ? "A different seed rearranges the same photos into different pages. Nothing changes until " +
          "the layout is run again."
        : "The seed is the only randomness in the layout engine: same photos, same seed, same book " +
          "(kernel §7). Shuffle it to ask for a different arrangement.";

    /// <summary>True when the drafted change leaves pages that ought to be laid out again.</summary>
    public bool NeedsRelayout =>
        (PageSizeChanged && PageSizeImpact.NeedsRelayout) ||
        ((SeedChanged || YearChanged) && _session.Chapters.Any(c => c.Pages.Count > 0));

    /// <summary>Why the layout offer is being made, in one line.</summary>
    public string RelayoutReason
    {
        get
        {
            if (!NeedsRelayout)
            {
                return string.Empty;
            }

            var reasons = new List<string>(3);
            if (PageSizeChanged) reasons.Add("the page size moved");
            if (YearChanged) reasons.Add("the year moved");
            if (SeedChanged) reasons.Add("the seed changed");

            var detached = PageSizeChanged ? PageSizeImpact.MismatchedDetachedPageCount : 0;
            var note = detached > 0
                ? $" {detached} hand-built page{(detached == 1 ? " is" : "s are")} never re-laid out " +
                  "and will still need rebuilding by hand (R15)."
                : string.Empty;

            return $"Because {Join(reasons)}, the pages in this book no longer match it. " +
                   "Re-run the layout now?" + note;
        }
    }

    // ------------------------------------------------------------------ loading

    /// <summary>Re-reads every field from the open book, discarding an unapplied draft.</summary>
    public void Reload()
    {
        PageSizes.Clear();
        Profiles.Clear();

        var book = _session.Book;
        if (book is null)
        {
            OnDraftChanged();
            return;
        }

        _originalTitle = book.Title;
        _originalYear = book.Year;
        _originalPageSizeId = book.PageSize;
        _originalProfileId = book.PrintProfileRef;
        _originalSeed = book.Seed;
        _seed = book.Seed;

        foreach (var profile in BuiltInPrintProfiles.All)
        {
            Profiles.Add(new PrintProfileOptionViewModel(profile));
        }

        foreach (var availability in PageSizeAvailability.ForProfile(TemplateLibrary.Default, _session.Profile))
        {
            PageSizes.Add(new PageSizeOptionViewModel(availability)
            {
                IsCurrent = string.Equals(availability.PageSizeId, book.PageSize, StringComparison.Ordinal),
            });
        }

        Title = book.Title;
        Year = book.Year;
        SelectedProfile = Profiles.FirstOrDefault(p =>
            string.Equals(p.Id, book.PrintProfileRef, StringComparison.Ordinal)) ?? Profiles.FirstOrDefault();
        SelectedPageSize = PageSizes.FirstOrDefault(s =>
            string.Equals(s.Id, book.PageSize, StringComparison.Ordinal)) ?? PageSizes.FirstOrDefault();

        _relayoutAnswered = false;
        _suggestingRelayout = true;
        RelayoutAfterApply = false;
        RelayoutIncludesPinned = false;
        _suggestingRelayout = false;

        Status = string.Empty;
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(SeedText));
        OnDraftChanged();
    }

    private PageSizeAvailability CurrentAvailability() =>
        PageSizes.FirstOrDefault(s => string.Equals(s.Id, _originalPageSizeId, StringComparison.Ordinal))
            ?.Availability
        ?? PageSizeAvailability.For(TemplateLibrary.Default, _session.PageSize);

    partial void OnTitleChanged(string value) => OnDraftChanged();

    partial void OnYearChanged(int value) => OnDraftChanged();

    partial void OnSelectedPageSizeChanged(PageSizeOptionViewModel? value) => OnDraftChanged();

    partial void OnSelectedProfileChanged(PrintProfileOptionViewModel? value)
    {
        // The profile owns the list of trims, so switching it re-offers the sizes rather than leaving
        // the old profile's sizes on screen. A size the new profile does not print falls back to its
        // first, which is what PdfExporter would do anyway (doc 12).
        if (value is null)
        {
            return;
        }

        var wanted = SelectedPageSize?.Id ?? _originalPageSizeId;
        PageSizes.Clear();
        foreach (var availability in PageSizeAvailability.ForProfile(TemplateLibrary.Default, value.Profile))
        {
            PageSizes.Add(new PageSizeOptionViewModel(availability)
            {
                IsCurrent = string.Equals(availability.PageSizeId, _originalPageSizeId, StringComparison.Ordinal),
            });
        }

        SelectedPageSize = PageSizes.FirstOrDefault(s => string.Equals(s.Id, wanted, StringComparison.Ordinal))
                           ?? PageSizes.FirstOrDefault();

        OnDraftChanged();
    }

    partial void OnIsBusyChanged(bool value) => ApplyCommand.NotifyCanExecuteChanged();

    /// <summary>Recomputes everything the form's guidance and its Apply button depend on.</summary>
    private void OnDraftChanged()
    {
        var needsRelayout = NeedsRelayout;
        _suggestingRelayout = true;
        try
        {
            if (!needsRelayout)
            {
                // The offer is gone, so the answer to it goes too — an Apply that silently carried a
                // tick from a change the user backed out of would re-lay out the book for nothing.
                RelayoutAfterApply = false;
                RelayoutIncludesPinned = false;
                _relayoutAnswered = false;
            }
            else if (!_relayoutAnswered)
            {
                // Suggested, not imposed: the checkbox and its reason are both on screen and Apply's
                // own label says whether the layout run is part of what the button does.
                RelayoutAfterApply = true;
                RelayoutIncludesPinned = PageSizeChanged || YearChanged;
            }
        }
        finally
        {
            _suggestingRelayout = false;
        }

        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(TitleChanged));
        OnPropertyChanged(nameof(YearChanged));
        OnPropertyChanged(nameof(PageSizeChanged));
        OnPropertyChanged(nameof(ProfileChanged));
        OnPropertyChanged(nameof(SeedChanged));
        OnPropertyChanged(nameof(Blocker));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(YearNotice));
        OnPropertyChanged(nameof(PageSizeImpact));
        OnPropertyChanged(nameof(PageSizeNotice));
        OnPropertyChanged(nameof(SeedNotice));
        OnPropertyChanged(nameof(NeedsRelayout));
        OnPropertyChanged(nameof(RelayoutReason));
        OnPropertyChanged(nameof(ApplyLabel));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>True while the panel itself is setting the checkbox, so that is not read as an answer.</summary>
    private bool _suggestingRelayout;

    /// <summary>True once the user has ticked or cleared the offer themselves; their answer then sticks.</summary>
    private bool _relayoutAnswered;

    partial void OnRelayoutAfterApplyChanged(bool value)
    {
        if (!_suggestingRelayout)
        {
            _relayoutAnswered = true;
        }

        OnPropertyChanged(nameof(ApplyLabel));
    }

    /// <summary>The Apply button's label, which names the size of what is about to happen.</summary>
    public string ApplyLabel => RelayoutAfterApply && NeedsRelayout
        ? "Apply and re-lay out the book"
        : "Apply";

    // ------------------------------------------------------------------ commands

    /// <summary>Re-rolls the seed — the "give me a different arrangement" gesture (kernel §7).</summary>
    [RelayCommand]
    private void ShuffleSeed()
    {
        var next = ProjectStore.NewSeed();

        // Vanishingly unlikely, but a shuffle that changed nothing would be a broken button.
        while (next == _seed)
        {
            next = ProjectStore.NewSeed();
        }

        _seed = next;
        OnPropertyChanged(nameof(SeedText));
        OnDraftChanged();
    }

    /// <summary>Puts the seed back to the book's, for a shuffle the user thought better of.</summary>
    [RelayCommand]
    private void RestoreSeed()
    {
        _seed = _originalSeed;
        OnPropertyChanged(nameof(SeedText));
        OnDraftChanged();
    }

    /// <summary>Steps the year one back — the common case is off by one.</summary>
    [RelayCommand]
    private void PreviousYear() => Year = Math.Max(MinYear, Year - 1);

    /// <summary>Steps the year one forward.</summary>
    [RelayCommand]
    private void NextYear() => Year = Math.Min(MaxYear, Year + 1);

    /// <summary>Throws the draft away and closes.</summary>
    [RelayCommand]
    private void Cancel()
    {
        Reload();
        CloseRequested?.Invoke(false);
    }

    /// <summary>
    /// Commits the draft as one undo entry — the settings and, when the user accepted the offer, the
    /// layout run they made necessary. They belong together: undoing a page-size change that left the
    /// pages rebuilt for the new size would put the book back in a state it was never in.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        var book = _session.Book;
        if (book is null || !CanApply)
        {
            return;
        }

        var relayout = RelayoutAfterApply && NeedsRelayout;
        var includePinned = RelayoutIncludesPinned;
        var summary = ChangeSummary();

        IsBusy = true;
        try
        {
            using (_undo.BeginBatch(summary))
            {
                CommitFields(book);

                if (relayout)
                {
                    await RelayoutAsync(includePinned).ConfigureAwait(true);
                }
            }

            _session.RequestSave();
            Applied?.Invoke();
            Reload();
            CloseRequested?.Invoke(true);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled. The settings were applied; the layout was not re-run.";
        }
        catch (Exception ex)
        {
            Status = "Could not apply the settings: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Writes the changed fields, each as its own step inside the open batch.</summary>
    private void CommitFields(Book book)
    {
        if (YearChanged)
        {
            // The year owns the chapters as well as itself, so it is captured and restored whole
            // rather than assigned; see BookYear.
            var before = BookYear.Capture(book, _session.Chapters);
            var target = Year;
            _undo.Execute(new EditCommand(
                $"Move the book to {target}",
                () => _session.SetBookYear(target),
                () => _session.RestoreBookYear(before)));
        }

        if (TitleChanged)
        {
            // After the year move, so an explicitly typed title wins over the year substitution
            // BookYear.MoveTo performs on a default title like "Family 2025".
            _undo.ExecuteValue(
                $"Rename the book to “{Title.Trim()}”",
                book.Title,
                Title.Trim(),
                _session.SetBookTitle);
        }

        if (ProfileChanged && SelectedProfile is { } profile)
        {
            _undo.ExecuteValue(
                $"Print with “{profile.Name}”",
                book.PrintProfileRef,
                profile.Id,
                _session.SetPrintProfile);
        }

        if (PageSizeChanged && SelectedPageSize is { } size)
        {
            _undo.ExecuteValue(
                $"Change the page size to {size.DisplayName}",
                book.PageSize,
                size.Id,
                _session.SetPageSize);
        }

        if (SeedChanged)
        {
            _undo.ExecuteValue("Shuffle the layout seed", book.Seed, _seed, _session.SetSeed);
        }
    }

    /// <summary>
    /// Re-runs the engine over every chapter that has something to lay out, on the job queue, one
    /// chapter at a time. Each chapter commits through <see cref="ChapterLayoutRunner.Apply"/>, so its
    /// pages are restored exactly on undo; the open batch folds all twelve into one entry.
    /// </summary>
    private async Task RelayoutAsync(bool includePinned)
    {
        var relaid = 0;
        foreach (var month in _session.Chapters.Select(c => c.Month).OrderBy(m => m).ToList())
        {
            var chapter = _runner.ChapterFor(month);
            var hasPhotos = _session.Book is { } b && _session.Catalog.InChapter(b.Year, month).Any(p => !p.Excluded);
            if (chapter is null || (chapter.Pages.Count == 0 && !hasPhotos))
            {
                continue;
            }

            var name = CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(month);
            Status = $"Laying out {name}…";

            LayoutResult? result = null;
            await _jobs.RunAsync(
                $"Laying out {name}",
                job => Task.Run(
                    () => result = _runner.Run(month, LayoutScope.WholeChapter, includePinned, dryRun: false),
                    job.Cancellation.Token)).ConfigureAwait(true);

            if (result is null)
            {
                continue;
            }

            if (_runner.Apply(month, result, _undo, $"Re-lay out {name}", () => ChapterChanged?.Invoke(month)))
            {
                relaid++;
            }
        }

        Status = relaid == 0
            ? "Nothing needed laying out."
            : $"Re-laid out {relaid} chapter{(relaid == 1 ? "" : "s")}.";
    }

    /// <summary>The undo entry's name — what actually changed, not "book settings" every time.</summary>
    private string ChangeSummary()
    {
        var parts = new List<string>(5);
        if (TitleChanged) parts.Add("title");
        if (YearChanged) parts.Add("year");
        if (PageSizeChanged) parts.Add("page size");
        if (ProfileChanged) parts.Add("print profile");
        if (SeedChanged) parts.Add("seed");

        return parts.Count == 0 ? "Change book settings" : "Change the book's " + Join(parts);
    }

    private static string Join(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => string.Empty,
        1 => parts[0],
        2 => $"{parts[0]} and {parts[1]}",
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
    };
}
