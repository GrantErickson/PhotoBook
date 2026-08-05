using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoBook.App.Services;
using PhotoBook.Core.Model;
using PhotoBook.Engine;

namespace PhotoBook.App.ViewModels.Pages;

/// <summary>
/// What the warning dialog is asked to confirm: the plan as it stands, plus — when the scope holds
/// Pinned pages — the plan the optional "include pinned pages" checkbox would produce. Both are
/// computed up front so ticking the box redraws instantly instead of waiting on the engine.
/// </summary>
/// <param name="Plan">The default plan: Pinned pages are left alone.</param>
/// <param name="WithPinned">The same run with Pinned pages unpinned first, or null when there are none.</param>
public sealed record LayoutConfirmationRequest(LayoutPlan Plan, LayoutPlan? WithPinned);

/// <summary>
/// The three R16 auto-layout commands (doc 09 §3.8): <em>Re-layout this day</em>,
/// <em>Auto-layout rest of chapter</em> and <em>Insert pages for unplaced photos</em>.
/// <para>
/// All three run the engine on the job queue and none of them touches the model until the user has
/// agreed to a plan that names concrete page numbers: every command runs
/// <see cref="LayoutRequest.DryRun"/> first, and cancelling really is a no-op. Each commits as one
/// undo entry (<see cref="ChapterPagesCommand"/>), so <c>Ctrl+Z</c> puts the whole chapter back.
/// </para>
/// </summary>
public sealed partial class LayoutCommandsViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly JobQueue _jobs;
    private readonly UndoStack _undo;
    private readonly ChapterLayoutRunner _runner;

    /// <summary>Creates the command set over the open session.</summary>
    public LayoutCommandsViewModel(ProjectSession session, JobQueue jobs, UndoStack undo)
    {
        _session = session;
        _jobs = jobs;
        _undo = undo;
        _runner = new ChapterLayoutRunner(session);
    }

    /// <summary>The engine runner, shared with anything else that needs a dry run.</summary>
    public ChapterLayoutRunner Runner => _runner;

    /// <summary>
    /// Shows the warning modal and returns the plan the user accepted, or null if they cancelled.
    /// The view supplies this; a null hook means "never confirm", which cancels every command that
    /// needs confirmation rather than silently re-laying out the chapter.
    /// </summary>
    public Func<LayoutConfirmationRequest, LayoutPlan?>? ConfirmationRequested { get; set; }

    /// <summary>Raised after the chapter's pages change — including on undo and redo — so the shell rebuilds.</summary>
    public event Action<int>? ChapterChanged;

    /// <summary>Raised for anything too long for the status line.</summary>
    public event Action<string>? ErrorRaised;

    /// <summary>The chapter being edited, 1..12.</summary>
    [ObservableProperty]
    private int _month = 1;

    /// <summary>The page the Pages tab is showing — the anchor for "rest of chapter" and "this day".</summary>
    [ObservableProperty]
    private Page? _currentPage;

    /// <summary>Its 1-based number within the chapter.</summary>
    [ObservableProperty]
    private int _currentPageNumber = 1;

    /// <summary>Progress text for the toolbar.</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>True while an engine run is in flight; the buttons disable rather than queue up.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>How many of the chapter's photos are sitting in the Unplaced bin.</summary>
    public int UnplacedCount => _session.IsOpen ? _runner.UnplacedPhotos(Month).Count : 0;

    /// <summary>The Day Group of <see cref="CurrentPage"/>, or null when the page carries no date.</summary>
    public DateOnly? CurrentDay => _runner.DayOfPage(CurrentPage);

    /// <summary>The date under the "this day" button, e.g. "Tue 12 Mar".</summary>
    public string CurrentDayLabel =>
        CurrentDay?.ToString("ddd d MMM", System.Globalization.CultureInfo.CurrentCulture) ?? "—";

    /// <summary>True when there is a chapter with pages and no run in flight.</summary>
    public bool CanRun => !IsBusy && _session.IsOpen && _runner.ChapterFor(Month) is { Pages.Count: > 0 };

    /// <summary>Re-reads everything the buttons' enabled state depends on.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(UnplacedCount));
        OnPropertyChanged(nameof(CurrentDay));
        OnPropertyChanged(nameof(CurrentDayLabel));
        OnPropertyChanged(nameof(CanRun));
        LayoutDayCommand.NotifyCanExecuteChanged();
        LayoutRestOfChapterCommand.NotifyCanExecuteChanged();
        InsertUnplacedPagesCommand.NotifyCanExecuteChanged();
    }

    partial void OnMonthChanged(int value) => Refresh();

    partial void OnCurrentPageChanged(Page? value) => Refresh();

    partial void OnIsBusyChanged(bool value) => Refresh();

    // ------------------------------------------------------------------ commands

    /// <summary>
    /// "Re-layout this day" (doc 09 §3.8.2): re-runs the engine for the current Day Group only, so
    /// its pages may merge or split (R28). The warning modal appears only when the day holds Pinned
    /// or Detached pages — otherwise nothing is protected and there is nothing to warn about.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLayoutDay))]
    private Task LayoutDayAsync()
    {
        var day = CurrentDay;
        if (day is null)
        {
            StatusMessage = "This page has no dated photos or journal text, so there is no day to lay out.";
            return Task.CompletedTask;
        }

        return RunAsync(LayoutCommandKind.Day, LayoutScope.Day(day.Value), day, confirmAlways: false);
    }

    private bool CanLayoutDay() => CanRun && CurrentPage is not null;

    /// <summary>
    /// "Auto-layout rest of chapter" (doc 09 §3.8.1, R16): regenerates every unpinned page from the
    /// current page to the end of the chapter. The confirmation is not optional — the dialog lists
    /// the exact page numbers before a single page is replaced.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task LayoutRestOfChapterAsync() => RunAsync(
        LayoutCommandKind.RestOfChapter,
        LayoutScope.From(Math.Max(1, CurrentPageNumber)),
        date: null,
        confirmAlways: true);

    /// <summary>
    /// "Insert pages for unplaced photos" (doc 09 §3.8.3): builds new pages for the Unplaced bin and
    /// splices them into the chapter in date order. Existing pages are not modified, and the dialog
    /// previews the outcome first.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInsertUnplaced))]
    private Task InsertUnplacedPagesAsync() => RunAsync(
        LayoutCommandKind.InsertUnplaced,
        LayoutScope.Unplaced,
        date: null,
        confirmAlways: true);

    private bool CanInsertUnplaced() => CanRun && UnplacedCount > 0;

    // ------------------------------------------------------------------ the run

    private async Task RunAsync(LayoutCommandKind kind, LayoutScope scope, DateOnly? date, bool confirmAlways)
    {
        if (!_session.IsOpen || _runner.ChapterFor(Month) is null || IsBusy)
        {
            return;
        }

        var month = Month;
        IsBusy = true;
        try
        {
            // Phase 1 — the cheap dry run. Nothing is written; this is only the sentence the modal
            // shows. The second pass exists so the include-pinned checkbox is instant.
            LayoutPlan? plan = null;
            LayoutPlan? withPinned = null;

            await _jobs.RunAsync("Planning layout", job => Task.Run(
                () =>
                {
                    plan = _runner.DryRun(month, kind, scope, includePinnedPages: false, date);
                    if (kind != LayoutCommandKind.InsertUnplaced && plan.PinnedPages.Count > 0)
                    {
                        withPinned = _runner.DryRun(month, kind, scope, includePinnedPages: true, date);
                    }
                },
                job.Cancellation.Token)).ConfigureAwait(true);

            if (plan is null)
            {
                return;
            }

            if (!plan.HasWork && withPinned is null or { HasWork: false })
            {
                StatusMessage = plan.Headline;
                return;
            }

            // Phase 2 — consent. Cancel really is a no-op: the model has not been touched yet.
            var accepted = plan;
            if (confirmAlways || plan.HasProtectedPages)
            {
                accepted = ConfirmationRequested?.Invoke(new LayoutConfirmationRequest(plan, withPinned));
                if (accepted is null)
                {
                    StatusMessage = "Layout cancelled — nothing changed.";
                    return;
                }
            }

            if (!accepted.HasWork)
            {
                StatusMessage = accepted.Headline;
                return;
            }

            // Phase 3 — the real run, then one undo entry.
            LayoutResult? result = null;
            await _jobs.RunAsync(RunTitle(kind), job => Task.Run(
                () => result = _runner.Run(month, scope, accepted.IncludePinnedPages, dryRun: false),
                job.Cancellation.Token)).ConfigureAwait(true);

            if (result is null)
            {
                return;
            }

            var applied = _runner.Apply(
                month, result, _undo, accepted.UndoDescription, () => ChapterChanged?.Invoke(month));

            StatusMessage = applied
                ? Outcome(accepted, result)
                : "The engine produced no pages, so the chapter was left as it was.";

            Refresh();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Layout cancelled — nothing changed.";
        }
        catch (Exception ex)
        {
            ErrorRaised?.Invoke($"Layout failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string RunTitle(LayoutCommandKind kind) => kind switch
    {
        LayoutCommandKind.Day => "Laying out the day",
        LayoutCommandKind.InsertUnplaced => "Inserting pages",
        _ => "Laying out the chapter",
    };

    private static string Outcome(LayoutPlan plan, LayoutResult result)
    {
        var errors = result.Diagnostics.Count(d => d.Severity == LayoutSeverity.Error);
        var text = plan.Kind == LayoutCommandKind.InsertUnplaced
            ? $"Inserted {Count(plan.GeneratedPageCount, "page")} for the unplaced photos."
            : $"Re-laid out {Count(plan.AffectedPages.Count, "page")}.";

        if (result.UnplacedPhotoIds.Count > 0)
        {
            text += $" {Count(result.UnplacedPhotoIds.Count, "photo")} still in the Unplaced bin.";
        }

        return errors > 0 ? text + $" {Count(errors, "issue")} to review in preflight." : text;

        static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? string.Empty : "s")}";
    }
}
