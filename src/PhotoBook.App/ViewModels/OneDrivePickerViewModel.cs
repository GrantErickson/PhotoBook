using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoBook.Core.Model;
using PhotoBook.Ingestion.OneDrive;

namespace PhotoBook.App.ViewModels;

/// <summary>One row in the album list.</summary>
public sealed record AlbumRow(string Id, string Name, int? ItemCount, DateTime? LastModifiedUtc)
{
    public string CountLabel => ItemCount is { } n ? $"{n} item{(n == 1 ? "" : "s")}" : "—";

    public string WhenLabel => LastModifiedUtc is { } d ? d.ToLocalTime().ToString("d MMM yyyy") : string.Empty;
}

/// <summary>One row in the folder browser.</summary>
public sealed record FolderRow(string? Id, string Name, string? Path, int? ChildCount)
{
    public string CountLabel => ChildCount is { } n ? $"{n} item{(n == 1 ? "" : "s")}" : string.Empty;
}

/// <summary>
/// Choosing what a book syncs from: a OneDrive album, or a folder for people who would rather not
/// use albums (doc 05). Album lists are dominated by OneDrive's auto-generated albums — the measured
/// account had 47, nearly all automatic — so the list is searchable and sorted newest-first, or a
/// deliberately curated album is impossible to find.
/// </summary>
public sealed partial class OneDrivePickerViewModel : ObservableObject
{
    private readonly IOneDriveClient _client;
    private readonly List<AlbumRow> _allAlbums = [];
    private readonly Stack<(string? Id, string Name)> _breadcrumb = new();

    public OneDrivePickerViewModel(IOneDriveClient client, string? accountName)
    {
        _client = client;
        AccountName = accountName ?? string.Empty;
    }

    public string AccountName { get; }

    public ObservableCollection<AlbumRow> Albums { get; } = [];

    public ObservableCollection<FolderRow> Folders { get; } = [];

    /// <summary>The chosen source, or null while the user has not committed.</summary>
    public BookSource? Result { get; private set; }

    /// <summary>
    /// How many items the chosen album holds, so the download can show real progress rather than a
    /// barber pole. Null for folders, where Graph gives no cheap count without walking the tree.
    /// </summary>
    public int? ResultItemCount { get; private set; }

    /// <summary>Raised when the dialog should close; true when a source was chosen.</summary>
    public event Action<bool>? CloseRequested;

    [ObservableProperty]
    private bool _isFolderMode;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private string _search = string.Empty;

    [ObservableProperty]
    private AlbumRow? _selectedAlbum;

    [ObservableProperty]
    private FolderRow? _selectedFolder;

    [ObservableProperty]
    private string _folderPath = "OneDrive";

    [ObservableProperty]
    private bool _includeSubfolders = true;

    public bool CanChoose => IsFolderMode ? true : SelectedAlbum is not null;

    partial void OnSearchChanged(string value) => ApplyFilter();

    partial void OnSelectedAlbumChanged(AlbumRow? value) => OnPropertyChanged(nameof(CanChoose));

    partial void OnIsFolderModeChanged(bool value)
    {
        OnPropertyChanged(nameof(CanChoose));
        if (value && Folders.Count == 0)
        {
            _ = LoadFoldersAsync(null, "OneDrive");
        }
    }

    // ---------------------------------------------------------------- loading

    /// <summary>Loads the album list. Called once when the dialog opens.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        Status = "Reading your albums…";
        try
        {
            var albums = await _client.ListAlbumsAsync(ct).ConfigureAwait(true);

            _allAlbums.Clear();
            _allAlbums.AddRange(albums
                .Select(a => new AlbumRow(a.Id, a.Name, a.ItemCount, a.LastModifiedUtc))
                // Newest first: a freshly curated "Book 2024" should be at the top, not buried
                // among years of automatic albums.
                .OrderByDescending(a => a.LastModifiedUtc ?? DateTime.MinValue)
                .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase));

            ApplyFilter();
            Status = _allAlbums.Count == 0
                ? "No albums found. You can pick a folder instead."
                : $"{_allAlbums.Count} album{(_allAlbums.Count == 1 ? "" : "s")}.";
        }
        catch (Exception ex)
        {
            Status = "Could not read albums: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        var term = Search?.Trim();
        Albums.Clear();

        foreach (var album in _allAlbums)
        {
            if (string.IsNullOrEmpty(term)
                || album.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase))
            {
                Albums.Add(album);
            }
        }

        if (!string.IsNullOrEmpty(term))
        {
            Status = $"{Albums.Count} of {_allAlbums.Count} albums match “{term}”.";
        }
    }

    private async Task LoadFoldersAsync(string? folderId, string displayName)
    {
        IsBusy = true;
        Status = "Reading folders…";
        try
        {
            var folders = await _client.ListFoldersAsync(folderId).ConfigureAwait(true);
            Folders.Clear();
            foreach (var f in folders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Folders.Add(new FolderRow(f.Id, f.Name, f.Path, f.ChildCount));
            }

            FolderPath = displayName;
            Status = Folders.Count == 0
                ? "No subfolders here. Choose this folder to use it."
                : $"{Folders.Count} subfolder{(Folders.Count == 1 ? "" : "s")}.";
        }
        catch (Exception ex)
        {
            Status = "Could not read folders: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------------------------------------------------------------- commands

    [RelayCommand]
    private void ShowAlbums() => IsFolderMode = false;

    [RelayCommand]
    private void ShowFolders() => IsFolderMode = true;

    [RelayCommand]
    private async Task OpenFolderAsync(FolderRow? folder)
    {
        if (folder is null)
        {
            return;
        }

        _breadcrumb.Push((CurrentFolderId, FolderPath));
        CurrentFolderId = folder.Id;
        await LoadFoldersAsync(folder.Id, folder.Name).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task GoUpAsync()
    {
        if (_breadcrumb.Count == 0)
        {
            return;
        }

        var (id, name) = _breadcrumb.Pop();
        CurrentFolderId = id;
        await LoadFoldersAsync(id, name).ConfigureAwait(true);
    }

    public bool CanGoUp => _breadcrumb.Count > 0;

    private string? CurrentFolderId { get; set; }

    [RelayCommand]
    private void Choose()
    {
        Result = IsFolderMode
            ? new BookSource
            {
                Kind = BookSourceKind.OneDriveFolder,
                Id = CurrentFolderId,
                Path = FolderPath,
            }
            : SelectedAlbum is { } album
                ? new BookSource
                {
                    Kind = BookSourceKind.OneDriveAlbum,
                    Id = album.Id,
                    Path = album.Name,
                }
                : null;

        ResultItemCount = IsFolderMode ? null : SelectedAlbum?.ItemCount;

        if (Result is not null)
        {
            CloseRequested?.Invoke(true);
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);
}
