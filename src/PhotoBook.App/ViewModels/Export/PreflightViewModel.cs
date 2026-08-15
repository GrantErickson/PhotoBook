using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoBook.App.Services;
using PhotoBook.Rendering;

namespace PhotoBook.App.ViewModels.Export;

/// <summary>
/// One preflight finding as a row. Doc 12 requires every row to be a hyperlink to the offending
/// page, slot or photo, so the row carries its own navigation command and the label that says where
/// it lands.
/// </summary>
public sealed partial class PreflightFindingViewModel : ObservableObject
{
    private readonly Action<PreflightFinding> _navigate;

    /// <summary>Wraps one finding.</summary>
    /// <param name="finding">The finding from <see cref="PreflightChecker"/>.</param>
    /// <param name="where">The pre-resolved location label, e.g. "Page 12 · slot s3".</param>
    /// <param name="navigate">Invoked when the row is clicked.</param>
    public PreflightFindingViewModel(PreflightFinding finding, string where, Action<PreflightFinding> navigate)
    {
        ArgumentNullException.ThrowIfNull(finding);
        Finding = finding;
        Where = where;
        _navigate = navigate;
    }

    /// <summary>The underlying finding.</summary>
    public PreflightFinding Finding { get; }

    /// <summary>Where clicking this row goes; empty for a book-wide finding.</summary>
    public string Where { get; }

    /// <summary>The short label, e.g. "Empty image slot".</summary>
    public string Title => Finding.Title;

    /// <summary>The sentence the user can act on.</summary>
    public string Detail => Finding.Detail;

    /// <summary>Whether this blocks export or merely needs acknowledging.</summary>
    public PreflightSeverity Severity => Finding.Severity;

    /// <summary>True when the row has somewhere to go.</summary>
    public bool HasTarget => Finding.BookPageNumber is not null || Finding.PhotoId is not null;

    /// <summary>Which gate produced it — the group key.</summary>
    public PreflightCheck Check => Finding.Check;

    /// <summary>Closes the panel and selects the offending page, slot or photo (doc 12).</summary>
    [RelayCommand(CanExecute = nameof(HasTarget))]
    private void Navigate() => _navigate(Finding);
}

/// <summary>
/// The preflight gate (doc 12) as a panel rather than a message box: findings grouped by severity,
/// every row clickable through to what it is about, a <em>Re-check</em> that re-runs the scan in
/// place, and the two questions export actually asks — <see cref="CanExport"/> (errors block, no
/// override) and <see cref="RequiresAcknowledgement"/> (warnings need one explicit tick).
/// </summary>
public sealed partial class PreflightViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly JobQueue _jobs;

    /// <summary>Creates the panel model over the open session.</summary>
    public PreflightViewModel(ProjectSession session, JobQueue jobs)
    {
        _session = session;
        _jobs = jobs;
    }

    /// <summary>Raised when a row is clicked, so the shell can select the page, slot or photo.</summary>
    public event Action<PreflightFinding>? NavigationRequested;

    /// <summary>The blocking findings, in doc 12's display order.</summary>
    public ObservableCollection<PreflightFindingViewModel> Errors { get; } = [];

    /// <summary>The findings covered by the single acknowledgement.</summary>
    public ObservableCollection<PreflightFindingViewModel> Warnings { get; } = [];

    /// <summary>The scope being checked; setting it invalidates the last report.</summary>
    [ObservableProperty]
    private ExportScope _scope = ExportScope.WholeBook;

    /// <summary>True while the scan is running.</summary>
    [ObservableProperty]
    private bool _isRunning;

    /// <summary>True once a scan has produced a report.</summary>
    [ObservableProperty]
    private bool _hasReport;

    /// <summary>The doc 12 acknowledgement covering every warning at once.</summary>
    [ObservableProperty]
    private bool _acknowledged;

    /// <summary>The one-line summary shown in the header.</summary>
    [ObservableProperty]
    private string _summary = "Preflight has not run yet.";

    /// <summary>How many pages were in scope.</summary>
    [ObservableProperty]
    private int _pageCount;

    /// <summary>The last report, or null before the first run.</summary>
    public PreflightReport? Report { get; private set; }

    /// <summary>True when nothing blocks export. Errors disable the button with no override (doc 12).</summary>
    public bool CanExport => Report is { CanExport: true };

    /// <summary>True when the acknowledgement checkbox must be shown.</summary>
    public bool RequiresAcknowledgement => Report is { RequiresAcknowledgement: true };

    /// <summary>True when the book is clean — no errors and no warnings.</summary>
    public bool IsClean => Report is { IsClean: true };

    /// <summary>True when the gate is satisfied: no errors, and warnings acknowledged if there are any.</summary>
    public bool IsSatisfied => CanExport && (!RequiresAcknowledgement || Acknowledged);

    /// <summary>Errors first, then warnings — the count badge in the header.</summary>
    public int ErrorCount => Errors.Count;

    /// <summary>How many warnings need the one acknowledgement.</summary>
    public int WarningCount => Warnings.Count;

    partial void OnScopeChanged(ExportScope value) => Invalidate();

    partial void OnAcknowledgedChanged(bool value) => OnPropertyChanged(nameof(IsSatisfied));

    /// <summary>Drops the last report — the scope changed under it, so it no longer describes anything.</summary>
    public void Invalidate()
    {
        Report = null;
        HasReport = false;
        Acknowledged = false;
        Errors.Clear();
        Warnings.Clear();
        Summary = "Preflight has not run for this scope yet.";
        RaiseGateProperties();
    }

    /// <summary>Runs the kernel §11 checks over the current scope, off the UI thread.</summary>
    [RelayCommand]
    public async Task RunAsync()
    {
        if (!_session.IsOpen || IsRunning)
        {
            return;
        }

        IsRunning = true;
        var scope = Scope;
        try
        {
            PreflightReport? report = null;
            await _jobs.RunAsync("Preflight", job => Task.Run(
                () => report = _session.Preflight(scope), job.Cancellation.Token)).ConfigureAwait(true);

            if (report is null)
            {
                return;
            }

            Apply(report);
        }
        catch (OperationCanceledException)
        {
            // A cancelled scan simply leaves the previous report in place.
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void Apply(PreflightReport report)
    {
        Report = report;
        Errors.Clear();
        Warnings.Clear();

        foreach (var finding in report.Findings)
        {
            var row = new PreflightFindingViewModel(finding, WhereOf(finding), f => NavigationRequested?.Invoke(f));
            (finding.Severity == PreflightSeverity.Error ? Errors : Warnings).Add(row);
        }

        PageCount = report.PageCount;
        Acknowledged = false;
        HasReport = true;
        Summary = report.IsClean
            ? $"Preflight passed — {Count(report.PageCount, "page")} in scope, nothing to report."
            : $"{Count(Errors.Count, "error")} and {Count(Warnings.Count, "warning")} " +
              $"across {Count(report.PageCount, "page")}.";

        RaiseGateProperties();
    }

    private void RaiseGateProperties()
    {
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(RequiresAcknowledgement));
        OnPropertyChanged(nameof(IsClean));
        OnPropertyChanged(nameof(IsSatisfied));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(WarningCount));
    }

    private string WhereOf(PreflightFinding finding)
    {
        var parts = new List<string>(3);

        if (finding.BookPageNumber is { } page)
        {
            parts.Add($"Page {page.ToString(CultureInfo.CurrentCulture)}");
        }
        else if (finding.ChapterMonth is { } month and >= 1 and <= 12)
        {
            parts.Add(CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(month));
        }

        if (finding.SlotId is { Length: > 0 } slot)
        {
            parts.Add($"slot {slot}");
        }

        if (finding.PhotoId is { Length: > 0 } photoId)
        {
            var photo = _session.Catalog.Find(photoId);
            parts.Add(photo is null ? "photo" : photo.OriginalFileName);
        }

        return string.Join(" · ", parts);
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? string.Empty : "s")}";
}
