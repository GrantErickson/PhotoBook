using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoBook.App.Services;
using PhotoBook.Core.Model;
using PhotoBook.Rendering;

namespace PhotoBook.App.ViewModels.Export;

/// <summary>One month in the export dialog's chapter picker.</summary>
/// <param name="Month">1..12.</param>
/// <param name="Name">The localized month name.</param>
/// <param name="PageCount">How many pages that chapter holds.</param>
public sealed record ExportChapterOption(int Month, string Name, int PageCount)
{
    /// <summary>The combo row's text.</summary>
    public string Label => PageCount == 1 ? $"{Name} · 1 page" : $"{Name} · {PageCount} pages";

    /// <summary>A month with no pages cannot be exported on its own.</summary>
    public bool HasPages => PageCount > 0;
}

/// <summary>
/// The export flow of doc 12: scope (whole book / this chapter / page range), destination, the
/// preflight gate in front of it, per-sheet progress on the job queue, and an honest result.
/// <para>
/// Errors disable <em>Export PDF</em> with no override; warnings need the single acknowledgement.
/// <em>Draft PDF</em> stays enabled throughout — it is doc 12's escape hatch (150 DPI, q75, DRAFT
/// watermark) for proofing on screen, and it is labelled as never-for-upload.
/// </para>
/// </summary>
public sealed partial class ExportViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly JobQueue _jobs;
    private bool _initializing;

    /// <summary>Creates the export model over the open session.</summary>
    public ExportViewModel(ProjectSession session, JobQueue jobs)
    {
        _session = session;
        _jobs = jobs;
        Preflight = new PreflightViewModel(session, jobs);
        Preflight.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PreflightViewModel.IsSatisfied) or nameof(PreflightViewModel.CanExport))
            {
                ExportCommand.NotifyCanExecuteChanged();
            }
        };
    }

    /// <summary>The gate that runs in front of every final export.</summary>
    public PreflightViewModel Preflight { get; }

    /// <summary>Raised when the panel asks to be dismissed.</summary>
    public event Action? CloseRequested;

    /// <summary>The chapters that can be exported on their own.</summary>
    public ObservableCollection<ExportChapterOption> Chapters { get; } = [];

    /// <summary>Which of doc 12's three scopes is selected.</summary>
    [ObservableProperty]
    private ExportScopeKind _scopeKind = ExportScopeKind.Book;

    /// <summary>The chapter for <see cref="ExportScopeKind.Chapter"/>.</summary>
    [ObservableProperty]
    private ExportChapterOption? _selectedChapter;

    /// <summary>First book page number for <see cref="ExportScopeKind.PageRange"/>, inclusive.</summary>
    [ObservableProperty]
    private int _firstPage = 1;

    /// <summary>Last book page number, inclusive.</summary>
    [ObservableProperty]
    private int _lastPage = 1;

    /// <summary>How many pages the whole book has — the range's upper bound.</summary>
    [ObservableProperty]
    private int _totalPages;

    /// <summary>Where the PDF goes.</summary>
    [ObservableProperty]
    private string _outputPath = string.Empty;

    /// <summary>True while a PDF is being written.</summary>
    [ObservableProperty]
    private bool _isExporting;

    /// <summary>Completion 0..100 for the progress bar.</summary>
    [ObservableProperty]
    private double _progress;

    /// <summary>"Sheet 12 of 24" while exporting.</summary>
    [ObservableProperty]
    private string _progressText = string.Empty;

    /// <summary>The outcome sentence, success or failure.</summary>
    [ObservableProperty]
    private string _resultMessage = string.Empty;

    /// <summary>True when <see cref="ResultMessage"/> describes a success.</summary>
    [ObservableProperty]
    private bool _resultIsSuccess;

    /// <summary>Extra lines worth reading after an export — font substitutions and render warnings.</summary>
    public ObservableCollection<string> ResultNotes { get; } = [];

    /// <summary>True once an export has finished, successfully or not.</summary>
    public bool HasResult => !string.IsNullOrEmpty(ResultMessage);

    /// <summary>True when the chapter picker applies.</summary>
    public bool IsChapterScope => ScopeKind == ExportScopeKind.Chapter;

    /// <summary>True when the page-range inputs apply.</summary>
    public bool IsPageRangeScope => ScopeKind == ExportScopeKind.PageRange;

    /// <summary>The scope the two buttons will use.</summary>
    public ExportScope CurrentScope => ScopeKind switch
    {
        ExportScopeKind.Chapter when SelectedChapter is { } chapter => ExportScope.Chapter(chapter.Month),
        ExportScopeKind.PageRange => ExportScope.PageRange(
            Math.Clamp(FirstPage, 1, Math.Max(1, TotalPages)),
            Math.Clamp(Math.Max(LastPage, FirstPage), 1, Math.Max(1, TotalPages))),
        _ => ExportScope.WholeBook,
    };

    /// <summary>A human sentence describing what is about to be written.</summary>
    public string ScopeSummary => ScopeKind switch
    {
        ExportScopeKind.Chapter when SelectedChapter is { } chapter =>
            $"{chapter.Name}, including its month-title page — chapters stand alone.",
        ExportScopeKind.PageRange =>
            $"Book pages {FirstPage}–{Math.Max(LastPage, FirstPage)} of {TotalPages}, for proofing a section.",
        _ => $"Every chapter in order — {TotalPages} page{(TotalPages == 1 ? string.Empty : "s")}.",
    };

    /// <summary>
    /// Doc 12: profile page-count rules apply to a whole-book export only. Partial exports are
    /// proofs, not uploads, and the panel says so rather than pretending they are print-ready.
    /// </summary>
    public bool IsProofOnly => ScopeKind != ExportScopeKind.Book;

    /// <summary>True when a destination has been chosen.</summary>
    public bool HasDestination => !string.IsNullOrWhiteSpace(OutputPath);

    /// <summary>The file name alone, for the destination row.</summary>
    public string OutputFileName => HasDestination ? Path.GetFileName(OutputPath) : "Choose a destination…";

    /// <summary>The folder alone.</summary>
    public string OutputFolder => HasDestination ? Path.GetDirectoryName(OutputPath) ?? string.Empty : string.Empty;

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Reloads the chapter list and page count, then runs preflight for the current scope.</summary>
    public async Task InitializeAsync(int? preferredMonth = null)
    {
        Chapters.Clear();
        if (!_session.IsOpen || _session.Book is null)
        {
            return;
        }

        // The scope properties settle in several steps; re-running the gate after each would only
        // race itself, so it runs once at the end.
        _initializing = true;
        foreach (var chapter in _session.Chapters.OrderBy(c => c.Month))
        {
            Chapters.Add(new ExportChapterOption(
                chapter.Month,
                CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(chapter.Month),
                chapter.Pages.Count));
        }

        TotalPages = BookPagination.Paginate(_session.Chapters).Count;
        FirstPage = 1;
        LastPage = Math.Max(1, TotalPages);

        SelectedChapter =
            Chapters.FirstOrDefault(c => c.Month == preferredMonth && c.HasPages)
            ?? Chapters.FirstOrDefault(c => c.HasPages)
            ?? Chapters.FirstOrDefault();

        SuggestDestination();
        _initializing = false;
        SyncScope();
        await Preflight.RunAsync().ConfigureAwait(true);
    }

    partial void OnScopeKindChanged(ExportScopeKind value)
    {
        OnPropertyChanged(nameof(IsChapterScope));
        OnPropertyChanged(nameof(IsPageRangeScope));
        OnPropertyChanged(nameof(IsProofOnly));
        SuggestDestination();
        SyncScope();
    }

    partial void OnSelectedChapterChanged(ExportChapterOption? value)
    {
        SuggestDestination();
        SyncScope();
    }

    partial void OnFirstPageChanged(int value)
    {
        if (LastPage < value)
        {
            LastPage = value;
        }

        SuggestDestination();
        SyncScope();
    }

    partial void OnLastPageChanged(int value)
    {
        SuggestDestination();
        SyncScope();
    }

    partial void OnOutputPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasDestination));
        OnPropertyChanged(nameof(OutputFileName));
        OnPropertyChanged(nameof(OutputFolder));
        ExportCommand.NotifyCanExecuteChanged();
        ExportDraftCommand.NotifyCanExecuteChanged();
    }

    partial void OnResultMessageChanged(string value) => OnPropertyChanged(nameof(HasResult));

    partial void OnIsExportingChanged(bool value)
    {
        ExportCommand.NotifyCanExecuteChanged();
        ExportDraftCommand.NotifyCanExecuteChanged();
    }

    private void SyncScope()
    {
        OnPropertyChanged(nameof(CurrentScope));
        OnPropertyChanged(nameof(ScopeSummary));

        var scope = CurrentScope;
        var changed = Preflight.Scope != scope;
        Preflight.Scope = scope;

        // A stale gate is worse than no gate: changing the scope invalidates the report, so run the
        // checks again straight away rather than leaving Export enabled on the wrong pages.
        if (changed && !_initializing)
        {
            _ = Preflight.RunAsync();
        }
    }

    /// <summary>Fills the destination with doc 12's prescribed file name beside the project folder.</summary>
    private void SuggestDestination()
    {
        if (_session.Book is not { } book)
        {
            return;
        }

        var folder = HasDestination
            ? Path.GetDirectoryName(OutputPath)
            : _session.Paths?.Root;

        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        OutputPath = Path.Combine(folder, CurrentScope.SuggestedFileName(book));
    }

    // ------------------------------------------------------------------ commands

    /// <summary>Re-runs the gate without leaving the panel (doc 12's <em>Re-check</em>).</summary>
    [RelayCommand]
    private Task RecheckAsync() => Preflight.RunAsync();

    /// <summary>Picks the destination file.</summary>
    [RelayCommand]
    private void Browse()
    {
        if (_session.Book is not { } book)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export PDF",
            Filter = "PDF (*.pdf)|*.pdf",
            FileName = HasDestination ? Path.GetFileName(OutputPath) : CurrentScope.SuggestedFileName(book),
            InitialDirectory = HasDestination ? Path.GetDirectoryName(OutputPath) : _session.Paths?.Root,
            OverwritePrompt = true,
            AddExtension = true,
            DefaultExt = ".pdf",
        };

        if (dialog.ShowDialog() == true)
        {
            OutputPath = dialog.FileName;
        }
    }

    /// <summary>The gated, print-ready export.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportAsync() => WriteAsync(draft: false);

    private bool CanExport() => !IsExporting && HasDestination && Preflight.IsSatisfied;

    /// <summary>
    /// Doc 12's draft escape hatch: always enabled, skips the gate, renders at 150 DPI with a DRAFT
    /// watermark. For proofing on screen or a home printer, never for upload.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportDraft))]
    private Task ExportDraftAsync() => WriteAsync(draft: true);

    private bool CanExportDraft() => !IsExporting && HasDestination;

    /// <summary>Opens the exported file in the system PDF viewer.</summary>
    [RelayCommand]
    private void OpenResult() => Launch(OutputPath);

    /// <summary>Reveals the exported file in Explorer.</summary>
    [RelayCommand]
    private void ShowInFolder()
    {
        if (File.Exists(OutputPath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{OutputPath}\"") { UseShellExecute = true });
        }
        else
        {
            Launch(OutputFolder);
        }
    }

    /// <summary>Dismisses the panel.</summary>
    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    private static void Launch(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Nothing to do: the file is written either way, and a missing shell association is
            // not worth an error dialog.
        }
    }

    private async Task WriteAsync(bool draft)
    {
        if (!_session.IsOpen || _session.Book is not { } book || IsExporting)
        {
            return;
        }

        var scope = CurrentScope;
        var path = draft ? DraftPath(OutputPath) : OutputPath;

        // Everything the exporter reads is captured here, on the UI thread, so the background pass
        // never touches session state (kernel §8, single writer).
        var request = new PdfExportRequest
        {
            Project = _session.Snapshot(),
            Profile = BuiltInPrintProfiles.Generic,
            Templates = _session.FindTemplate,
            Images = _session.Images,
            OutputPath = path,
            Scope = scope,
            Draft = draft,
            Overwrite = true,
            Progress = new Progress<PdfExportProgress>(p => JobQueue.PostUi(() =>
            {
                Progress = p.Fraction * 100;
                ProgressText = $"Sheet {p.SheetIndex + 1} of {p.SheetCount}";
            })),
        };

        IsExporting = true;
        Progress = 0;
        ProgressText = "Preparing…";
        ResultMessage = string.Empty;
        ResultNotes.Clear();

        try
        {
            PdfExportResult? result = null;
            await _jobs.RunAsync(draft ? "Exporting draft PDF" : "Exporting PDF", job => Task.Run(
                () => result = new PdfExporter().Export(request, job.Cancellation.Token),
                job.Cancellation.Token)).ConfigureAwait(true);

            if (result is null)
            {
                return;
            }

            OutputPath = result.OutputPath;
            ResultIsSuccess = true;
            ResultMessage =
                $"{(draft ? "Draft written" : "Exported")}: {result.PageCount} page" +
                $"{(result.PageCount == 1 ? string.Empty : "s")} to {Path.GetFileName(result.OutputPath)} " +
                $"in {result.Duration.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture)} s.";

            foreach (var font in result.Fonts.Where(f => !f.IsExactMatch))
            {
                ResultNotes.Add(font.Describe());
            }

            foreach (var diagnostic in result.Diagnostics
                         .Where(d => d.Severity != RenderSeverity.Info)
                         .Select(d => d.Message)
                         .Distinct()
                         .Take(5))
            {
                ResultNotes.Add(diagnostic);
            }

            if (draft)
            {
                ResultNotes.Add("Draft output is 150 DPI with a DRAFT watermark — proof with it, never upload it.");
            }
        }
        catch (OperationCanceledException)
        {
            ResultIsSuccess = false;
            ResultMessage = "Export cancelled. No file was written.";
        }
        catch (Exception ex)
        {
            ResultIsSuccess = false;
            ResultMessage = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsExporting = false;
            Progress = 0;
            ProgressText = string.Empty;
        }
    }

    /// <summary>Draft output never overwrites the real one; doc 12 names it with a <c>_draft</c> suffix.</summary>
    private static string DraftPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var folder = Path.GetDirectoryName(path) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(path);
        return Path.Combine(folder, stem.EndsWith("_draft", StringComparison.Ordinal) ? $"{stem}.pdf" : $"{stem}_draft.pdf");
    }
}
