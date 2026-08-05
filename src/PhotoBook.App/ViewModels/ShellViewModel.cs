using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoBook.App.Services;
using PhotoBook.Core.Persistence;

namespace PhotoBook.App.ViewModels;

/// <summary>A book the user opened before, offered on the dashboard.</summary>
public sealed record RecentBook(string Path, string Title, int Year, DateTime OpenedUtc)
{
    public string Folder => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));

    public bool Exists => ProjectPaths.IsProjectFolder(Path);
}

/// <summary>Owns navigation between the dashboard and the open book, plus the recent-book list.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private static readonly string RecentFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhotoBook", "recent.json");

    private readonly ProjectSession _session;
    private readonly JobQueue _jobs;

    public ShellViewModel(ProjectSession session, JobQueue jobs, BookViewModel book)
    {
        _session = session;
        _jobs = jobs;
        Book = book;

        _jobs.JobFailed += (title, ex) =>
            JobQueue.PostUi(() => ErrorMessage = $"{title} failed: {ex.Message}");

        Book.ErrorRaised += message => JobQueue.PostUi(() => ErrorMessage = message);

        // The title carries the book's name and year, and an import can retarget both.
        _session.Changed += () => JobQueue.PostUi(() => OnPropertyChanged(nameof(WindowTitle)));

        LoadRecent();
    }

    public BookViewModel Book { get; }

    public JobQueue Jobs => _jobs;

    public ObservableCollection<RecentBook> Recent { get; } = [];

    public bool HasRecent => Recent.Count > 0;

    [ObservableProperty]
    private bool _isBookOpen;

    [ObservableProperty]
    private string? _errorMessage;

    public string WindowTitle => IsBookOpen && _session.Book is not null
        ? $"{_session.Book.Title} — PhotoBook"
        : "PhotoBook";

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    // ---------------------------------------------------------------- open / create

    [RelayCommand]
    private async Task NewBookAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose an empty folder for the new book",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var folder = dialog.FolderName;
        var year = DateTime.Now.Year;
        var title = $"Family {year}";

        try
        {
            await _session.CreateAsync(folder, year, title).ConfigureAwait(true);
            AfterOpen(folder);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not create the book: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task OpenBookAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a PhotoBook project folder",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await OpenPathAsync(dialog.FolderName).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task OpenRecentAsync(RecentBook? recent)
    {
        if (recent is not null)
        {
            await OpenPathAsync(recent.Path).ConfigureAwait(true);
        }
    }

    /// <summary>Opens a project folder, or creates one if the folder holds photos but no book.</summary>
    public async Task OpenPathAsync(string folder)
    {
        try
        {
            if (!ProjectPaths.IsProjectFolder(folder))
            {
                var year = DateTime.Now.Year;
                await _session.CreateAsync(folder, year, $"Family {year}").ConfigureAwait(true);
            }
            else
            {
                await _session.OpenAsync(folder).ConfigureAwait(true);
            }

            AfterOpen(folder);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not open that folder: {ex.Message}";
        }
    }

    private void AfterOpen(string folder)
    {
        IsBookOpen = true;
        Book.RefreshChapters();
        Book.SelectedChapter = Book.Chapters.FirstOrDefault(c => c.PhotoCount > 0) ?? Book.Chapters[0];
        RememberRecent(folder);
        OnPropertyChanged(nameof(WindowTitle));
    }

    [RelayCommand]
    private async Task CloseBookAsync()
    {
        if (_session.IsDirty)
        {
            await _session.SaveAsync().ConfigureAwait(true);
        }

        _session.Close();
        IsBookOpen = false;
        OnPropertyChanged(nameof(WindowTitle));
    }

    // ---------------------------------------------------------------- recent list

    private void RememberRecent(string folder)
    {
        if (_session.Book is null)
        {
            return;
        }

        var entry = new RecentBook(folder, _session.Book.Title, _session.Book.Year, DateTime.UtcNow);
        for (var i = Recent.Count - 1; i >= 0; i--)
        {
            if (string.Equals(Recent[i].Path, folder, StringComparison.OrdinalIgnoreCase))
            {
                Recent.RemoveAt(i);
            }
        }

        Recent.Insert(0, entry);
        while (Recent.Count > 8)
        {
            Recent.RemoveAt(Recent.Count - 1);
        }

        OnPropertyChanged(nameof(HasRecent));
        SaveRecent();
    }

    private void LoadRecent()
    {
        try
        {
            if (!File.Exists(RecentFile))
            {
                return;
            }

            var json = File.ReadAllText(RecentFile);
            var items = JsonSerializer.Deserialize<List<RecentBook>>(json) ?? [];
            foreach (var item in items.Where(i => i.Exists))
            {
                Recent.Add(item);
            }

            OnPropertyChanged(nameof(HasRecent));
        }
        catch
        {
            // A corrupt recent list is not worth bothering the user about.
        }
    }

    private void SaveRecent()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(RecentFile)!);
            File.WriteAllText(RecentFile, JsonSerializer.Serialize(Recent.ToList()));
        }
        catch
        {
            // Best effort.
        }
    }
}
