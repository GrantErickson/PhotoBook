using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoBook.App.Services;
using PhotoBook.Core.Model;
using PhotoBook.Engine;
using PhotoBook.Rendering;

namespace PhotoBook.App.ViewModels;

/// <summary>A month in the chapter rail.</summary>
public sealed partial class ChapterItemViewModel : ObservableObject
{
    public ChapterItemViewModel(int month) => Month = month;

    public int Month { get; }

    public string Name => CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(Month);

    public string ShortName => CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedMonthName(Month);

    [ObservableProperty]
    private int _photoCount;

    [ObservableProperty]
    private int _pageCount;

    [ObservableProperty]
    private bool _isSelected;

    public bool HasPhotos => PhotoCount > 0;

    public void Update(int photos, int pages)
    {
        PhotoCount = photos;
        PageCount = pages;
        OnPropertyChanged(nameof(HasPhotos));
    }
}

/// <summary>The open book: the chapter rail, the Photos grid, the Pages editor, and the commands.</summary>
public sealed partial class BookViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly JobQueue _jobs;
    private readonly ThumbnailProvider _thumbnails;

    public BookViewModel(ProjectSession session, JobQueue jobs, ThumbnailProvider thumbnails)
    {
        _session = session;
        _jobs = jobs;
        _thumbnails = thumbnails;

        for (var month = 1; month <= 12; month++)
        {
            Chapters.Add(new ChapterItemViewModel(month));
        }
    }

    public ProjectSession Session => _session;

    public ObservableCollection<ChapterItemViewModel> Chapters { get; } = [];

    public ObservableCollection<PhotoItemViewModel> Photos { get; } = [];

    public ObservableCollection<PageItemViewModel> Pages { get; } = [];

    public string BookTitle => _session.Book?.Title ?? string.Empty;

    public int Year => _session.Book?.Year ?? DateTime.Now.Year;

    [ObservableProperty]
    private ChapterItemViewModel? _selectedChapter;

    [ObservableProperty]
    private PhotoItemViewModel? _selectedPhoto;

    [ObservableProperty]
    private PageItemViewModel? _selectedPage;

    [ObservableProperty]
    private bool _isPagesTab;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Photos of the month that no page uses — the Unplaced bin (R10, R13).</summary>
    public ObservableCollection<PhotoItemViewModel> UnplacedBin { get; } = [];

    /// <summary>
    /// Raised for messages too long or too important for the status line — setup instructions and
    /// sign-in failures. The shell shows these in the dismissible toast, which wraps.
    /// </summary>
    public event Action<string>? ErrorRaised;

    public bool HasPhotos => Photos.Count > 0;

    public bool HasPages => Pages.Count > 0;

    // ---------------------------------------------------------------- loading

    /// <summary>Rebuilds the rail counts from the model.</summary>
    public void RefreshChapters()
    {
        var book = _session.Book;
        if (book is null)
        {
            return;
        }

        foreach (var chapter in Chapters)
        {
            var photos = _session.Catalog.InChapter(book.Year, chapter.Month).Count(p => !p.Excluded);
            var pages = _session.Chapters.FirstOrDefault(c => c.Month == chapter.Month)?.Pages.Count ?? 0;
            chapter.Update(photos, pages);
        }

        OnPropertyChanged(nameof(BookTitle));
        OnPropertyChanged(nameof(Year));
        OnPropertyChanged(nameof(HasOneDriveSource));
        OnPropertyChanged(nameof(OneDriveSourceLabel));
    }

    partial void OnSelectedChapterChanged(ChapterItemViewModel? value)
    {
        foreach (var chapter in Chapters)
        {
            chapter.IsSelected = ReferenceEquals(chapter, value);
        }

        if (value is not null)
        {
            LoadChapter(value.Month);
        }
    }

    /// <summary>Populates the grid and page list for a month.</summary>
    public void LoadChapter(int month)
    {
        var book = _session.Book;
        if (book is null)
        {
            return;
        }

        Photos.Clear();
        foreach (var photo in _session.Catalog.InChapter(book.Year, month).OrderBy(p => p.TakenAt))
        {
            Photos.Add(new PhotoItemViewModel(photo, _thumbnails));
        }

        RebuildPages(month);
        SelectedPhoto = Photos.FirstOrDefault();
        SelectedPage = Pages.FirstOrDefault();

        // A month that is already laid out is more useful opened on its pages than its grid.
        IsPagesTab = Pages.Count > 0;

        OnPropertyChanged(nameof(HasPhotos));
        OnPropertyChanged(nameof(HasPages));
        _ = LoadThumbnailsAsync();

        if (IsPagesTab)
        {
            _ = RenderVisiblePagesAsync();
        }
    }

    private void RebuildPages(int month)
    {
        Pages.Clear();
        var chapter = _session.Chapters.FirstOrDefault(c => c.Month == month);
        if (chapter is not null)
        {
            for (var i = 0; i < chapter.Pages.Count; i++)
            {
                Pages.Add(new PageItemViewModel(chapter.Pages[i], i + 1));
            }
        }

        RebuildUnplacedBin(month);
        OnPropertyChanged(nameof(HasPages));
    }

    private void RebuildUnplacedBin(int month)
    {
        UnplacedBin.Clear();
        var chapter = _session.Chapters.FirstOrDefault(c => c.Month == month);
        if (chapter is null)
        {
            return;
        }

        var placed = chapter.Pages
            .SelectMany(p => p.Placements)
            .Select(p => p.PhotoId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var item in Photos.Where(p => !p.Excluded && !placed.Contains(p.Photo.Id)))
        {
            UnplacedBin.Add(item);
        }
    }

    private async Task LoadThumbnailsAsync()
    {
        foreach (var item in Photos.ToList())
        {
            try
            {
                await item.EnsureThumbnailAsync().ConfigureAwait(false);
            }
            catch
            {
                // A single unreadable file must not stop the grid filling in.
            }
        }
    }

    // ---------------------------------------------------------------- commands

    [RelayCommand]
    private void SelectChapter(ChapterItemViewModel? chapter)
    {
        if (chapter is not null)
        {
            SelectedChapter = chapter;
        }
    }

    [RelayCommand]
    private void ShowPhotos() => IsPagesTab = false;

    [RelayCommand]
    private void ShowPages()
    {
        IsPagesTab = true;
        _ = RenderVisiblePagesAsync();
    }

    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a folder of photos to import",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;
        var photosBefore = _session.PhotoCount;
        await _jobs.RunAsync("Importing photos", async job =>
        {
            job.Status = "Counting photos…";
            var expected = CountSupportedFiles(dialog.FolderName, job.Cancellation.Token);

            var progress = new Progress<Ingestion.PhotoImportProgress>(p =>
                ReportImportProgress(job, p, expected));

            var report = await _session
                .ImportFolderAsync(dialog.FolderName, progress, job.Cancellation.Token)
                .ConfigureAwait(false);

            var added = report.Entries.Count(e => e.Outcome == Ingestion.PhotoImportOutcome.Added);
            var skipped = report.Entries.Count - added;

            JobQueue.PostUi(() =>
            {
                StatusMessage = skipped > 0
                    ? $"Imported {added} photo{(added == 1 ? "" : "s")}; skipped {skipped} already in the book."
                    : $"Imported {added} photo{(added == 1 ? "" : "s")}.";

                if (_session.ReconcileYearAfterImport(photosBefore) is { } note)
                {
                    ErrorRaised?.Invoke(note);
                }

                RefreshChapters();
                JumpToFirstMonthWithPhotos();
            });
        }).ConfigureAwait(true);

        IsBusy = false;
        await AnalyzeAsync().ConfigureAwait(true);
        await SaveAsync().ConfigureAwait(true);
    }

    private void JumpToFirstMonthWithPhotos()
    {
        var target = Chapters.FirstOrDefault(c => c.PhotoCount > 0);
        if (target is not null && SelectedChapter?.PhotoCount is null or 0)
        {
            SelectedChapter = target;
        }
        else if (SelectedChapter is not null)
        {
            LoadChapter(SelectedChapter.Month);
        }
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        var pending = _session.Catalog.Photos.Where(p => !p.Excluded && p.Quality is null).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        IsBusy = true;
        await _jobs.RunAsync("Analyzing photos", async job =>
        {
            job.IsIndeterminate = false;
            var done = 0;
            var progress = new Progress<Analysis.Running.AnalysisProgress>(_ =>
            {
                done++;
                job.Progress = Math.Min(100, done * 100.0 / pending.Count);
                job.Status = $"{done} of {pending.Count}";
            });

            await _session.AnalyzeAsync(pending, progress, job.Cancellation.Token).ConfigureAwait(false);

            JobQueue.PostUi(() =>
            {
                StatusMessage = $"Analyzed {pending.Count} photo{(pending.Count == 1 ? "" : "s")}.";
                foreach (var item in Photos)
                {
                    item.Refresh();
                }
            });
        }).ConfigureAwait(true);

        IsBusy = false;
    }

    /// <summary>True once this book is bound to a OneDrive album or folder, enabling "Sync now".</summary>
    public bool HasOneDriveSource => _session.OneDriveSource is not null;

    /// <summary>What this book syncs from, for the button's tooltip.</summary>
    public string OneDriveSourceLabel => _session.OneDriveSource?.Path ?? string.Empty;

    /// <summary>
    /// Signs in through the WAM broker, lets the user pick an album or folder, then downloads it
    /// into the project exactly as a folder import would. The choice is remembered on the book so
    /// later syncs skip the picker. Before an app registration exists this surfaces the actionable
    /// setup message rather than an exception.
    /// </summary>
    [RelayCommand]
    private Task SyncOneDriveAsync() => ConnectOneDriveAsync(reuseStoredSource: false);

    /// <summary>Re-syncs the remembered album or folder, picking up anything added since.</summary>
    [RelayCommand]
    private Task ResyncOneDriveAsync() => ConnectOneDriveAsync(reuseStoredSource: true);

    private async Task ConnectOneDriveAsync(bool reuseStoredSource)
    {
        Ingestion.OneDrive.OneDriveClient? client = null;
        try
        {
            client = Ingestion.OneDrive.OneDriveClient.CreateFromConfiguration(
                configurationFilePath: null, parentWindow: WindowHandles.Main);

            // Sign in first: the picker cannot list anything without a token, and the WAM prompt
            // must not appear from underneath a modal dialog.
            StatusMessage = "Signing in to OneDrive…";
            await client.Authenticator.GetAccessTokenAsync().ConfigureAwait(true);

            var source = reuseStoredSource ? _session.OneDriveSource : null;
            int? expectedItems;

            if (source is null)
            {
                var picker = new OneDrivePickerViewModel(client, client.Authenticator.SignedInAccount);
                var window = new Views.OneDrivePickerWindow(picker)
                {
                    Owner = System.Windows.Application.Current?.MainWindow,
                };

                if (window.ShowDialog() != true || picker.Result is null)
                {
                    StatusMessage = "OneDrive sync cancelled.";
                    return;
                }

                source = picker.Result;
                expectedItems = picker.ResultItemCount;
            }
            else
            {
                // "Sync now" skipped the picker, so re-read the album's size for the progress bar.
                expectedItems = await CountItemsAsync(client, source).ConfigureAwait(true);
            }

            await ImportFromOneDriveAsync(client, source, expectedItems).ConfigureAwait(true);
        }
        catch (Ingestion.OneDrive.OneDriveNotConfiguredException ex)
        {
            ErrorRaised?.Invoke(ex.Message);
        }
        catch (Ingestion.OneDrive.OneDriveSignInException ex)
        {
            ErrorRaised?.Invoke(ex.Message);
        }
        finally
        {
            client?.Dispose();
        }
    }

    /// <summary>
    /// How many items the stored album holds, for the progress bar. One cheap Graph call; a folder
    /// source has no equivalent, and a failure here must never stop the sync.
    /// </summary>
    private static async Task<int?> CountItemsAsync(
        Ingestion.OneDrive.IOneDriveClient client, BookSource source)
    {
        if (source.Kind != BookSourceKind.OneDriveAlbum || source.Id is null)
        {
            return null;
        }

        try
        {
            var albums = await client.ListAlbumsAsync().ConfigureAwait(true);
            return albums.FirstOrDefault(a => a.Id == source.Id)?.ItemCount;
        }
        catch
        {
            return null;
        }
    }

    private async Task ImportFromOneDriveAsync(
        Ingestion.OneDrive.IOneDriveClient client, BookSource source, int? expectedItems)
    {
        IsBusy = true;
        var photosBefore = _session.PhotoCount;
        try
        {
            await _jobs.RunAsync($"Syncing “{source.Path}”", async job =>
            {
                var progress = new Progress<Ingestion.PhotoImportProgress>(p =>
                    ReportImportProgress(job, p, expectedItems));

                var report = await _session
                    .ImportOneDriveAsync(client, source, progress, job.Cancellation.Token)
                    .ConfigureAwait(false);

                var added = report.Entries.Count(e => e.Outcome == Ingestion.PhotoImportOutcome.Added);
                var skipped = report.Entries.Count - added;

                JobQueue.PostUi(() =>
                {
                    StatusMessage = added == 0
                        ? $"“{source.Path}” is already up to date ({skipped} item(s) skipped)."
                        : $"Downloaded {added} photo{(added == 1 ? "" : "s")} from “{source.Path}”" +
                          (skipped > 0 ? $"; skipped {skipped} (already imported, or not a photo)." : ".");

                    // A book covers one year, so photos outside it vanish into the tray unless we say so.
                    if (_session.ReconcileYearAfterImport(photosBefore) is { } note)
                    {
                        ErrorRaised?.Invoke(note);
                    }

                    OnPropertyChanged(nameof(HasOneDriveSource));
                    OnPropertyChanged(nameof(OneDriveSourceLabel));
                    RefreshChapters();
                    JumpToFirstMonthWithPhotos();
                });
            }).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }

        await AnalyzeAsync().ConfigureAwait(true);
        await SaveAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ImportJournalAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a Word journal",
            Filter = "Word documents (*.docx)|*.docx",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await _jobs.RunAsync("Reading journal", async job =>
        {
            var report = await _session
                .ImportJournalAsync(dialog.FileName, job.Cancellation.Token)
                .ConfigureAwait(false);

            JobQueue.PostUi(() =>
                StatusMessage = $"Journal: {report.Matched} entries dated, " +
                                $"{report.Unmatched} needing a date.");
        }).ConfigureAwait(true);

        await SaveAsync().ConfigureAwait(true);
    }

    /// <summary>Lays out the selected month from scratch (R7).</summary>
    [RelayCommand]
    private async Task LayoutChapterAsync()
    {
        if (SelectedChapter is null)
        {
            return;
        }

        var month = SelectedChapter.Month;
        IsBusy = true;

        await _jobs.RunAsync($"Laying out {SelectedChapter.Name}", job => Task.Run(() =>
        {
            var result = _session.Layout(month);
            JobQueue.PostUi(() =>
            {
                _session.ApplyLayout(month, result);
                RebuildPages(month);
                SelectedPage = Pages.FirstOrDefault();
                RefreshChapters();
                IsPagesTab = true;
                StatusMessage = $"Laid out {result.Pages.Count} pages.";
                _ = RenderVisiblePagesAsync();
            });
        })).ConfigureAwait(true);

        IsBusy = false;
        await SaveAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Counts the importable files in a folder so the bar can fill. A directory walk is far cheaper
    /// than the decode-and-copy that follows, and returning null on any failure just falls back to
    /// an indeterminate bar rather than failing the import.
    /// </summary>
    private static int? CountSupportedFiles(string folder, CancellationToken ct)
    {
        try
        {
            var count = 0;
            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (Imaging.ImageFormats.IsSupported(path))
                {
                    count++;
                }
            }

            return count;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Drives the job strip during an import. With a known total the bar fills and the status reads
    /// "12 of 47"; without one it stays indeterminate rather than inventing a percentage.
    /// <paramref name="expected"/> counts every item in the source, including videos and duplicates
    /// that get skipped, so it is matched against items *seen* rather than photos added.
    /// </summary>
    private static void ReportImportProgress(Job job, Ingestion.PhotoImportProgress p, int? expected)
    {
        if (expected is > 0)
        {
            job.IsIndeterminate = false;
            job.Progress = Math.Clamp(100.0 * p.ItemsSeen / expected.Value, 0, 100);
            job.Status = $"{p.ItemsSeen} of {expected} · {p.CurrentFileName}";
        }
        else
        {
            job.IsIndeterminate = true;
            job.Status = p.ItemsSeen > 0
                ? $"{p.ItemsSeen} seen · {p.CurrentFileName}"
                : p.CurrentFileName;
        }
    }

    /// <summary>Renders previews for the chapter's pages.</summary>
    public async Task RenderVisiblePagesAsync()
    {
        var chapterModel = _session.Chapters.FirstOrDefault(c => c.Month == SelectedChapter?.Month);
        if (chapterModel is null)
        {
            return;
        }

        foreach (var item in Pages.ToList())
        {
            var page = item.Page;
            try
            {
                var preview = await Task.Run(() => _session.RenderPage(chapterModel, page, 700, 541))
                    .ConfigureAwait(false);
                var bitmap = PixelBridge.ToBitmap(preview.Image);
                JobQueue.PostUi(() => item.Preview = bitmap);
            }
            catch
            {
                // A page that cannot render must not take the whole strip down.
            }
        }
    }

    [RelayCommand]
    private async Task ExportPdfAsync()
    {
        if (_session.Book is null || SelectedChapter is null)
        {
            return;
        }

        var scope = ExportScope.Chapter(SelectedChapter.Month);
        var report = _session.Preflight(scope);
        if (!report.CanExport)
        {
            StatusMessage = "Export blocked: " + string.Join("; ", report.Errors.Take(3).Select(e => e.Title));
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export PDF",
            Filter = "PDF (*.pdf)|*.pdf",
            FileName = $"{_session.Book.Title}-{SelectedChapter.ShortName}.pdf",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var path = dialog.FileName;
        await _jobs.RunAsync("Exporting PDF", _ => Task.Run(() =>
        {
            var result = _session.Export(path, scope);
            JobQueue.PostUi(() => StatusMessage = $"Exported {result.PageCount} pages to {Path.GetFileName(path)}.");
        })).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!_session.IsOpen)
        {
            return;
        }

        await _jobs.RunAsync("Saving", async _ =>
        {
            await _session.SaveAsync().ConfigureAwait(false);
            JobQueue.PostUi(() => StatusMessage = "Saved.");
        }).ConfigureAwait(true);
    }

    // ---------------------------------------------------------------- photo edits

    [RelayCommand]
    private void Promote() => ShiftTier(-1);

    [RelayCommand]
    private void Demote() => ShiftTier(1);

    private void ShiftTier(int delta)
    {
        if (SelectedPhoto is null)
        {
            return;
        }

        var order = new[] { Core.Model.Tier.S, Core.Model.Tier.A, Core.Model.Tier.B, Core.Model.Tier.C };
        var current = Array.IndexOf(order, SelectedPhoto.EffectiveTier);
        var next = Math.Clamp(current + delta, 0, order.Length - 1);

        SelectedPhoto.Photo.UserTierOverride = order[next];
        SelectedPhoto.Refresh();
        _session.MarkDirty();
        StatusMessage = $"{SelectedPhoto.FileName} set to tier {order[next]}.";
    }

    [RelayCommand]
    private void ResetTier()
    {
        if (SelectedPhoto is null)
        {
            return;
        }

        SelectedPhoto.Photo.UserTierOverride = null;
        SelectedPhoto.Refresh();
        _session.MarkDirty();
    }

    /// <summary>Excluding removes the photo from the book but never from disk (R17).</summary>
    [RelayCommand]
    private void ToggleExclude()
    {
        if (SelectedPhoto is null)
        {
            return;
        }

        SelectedPhoto.Photo.Excluded = !SelectedPhoto.Photo.Excluded;
        SelectedPhoto.Refresh();
        _session.MarkDirty();
        RefreshChapters();
        StatusMessage = SelectedPhoto.Excluded
            ? $"{SelectedPhoto.FileName} excluded from the book. The original file is untouched."
            : $"{SelectedPhoto.FileName} is back in the book.";
    }
}
