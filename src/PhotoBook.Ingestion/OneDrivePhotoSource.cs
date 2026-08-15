using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Graph.Models;
using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;
using PhotoBook.Ingestion.Probing;

namespace PhotoBook.Ingestion;

// The source sits beside FolderPhotoSource because it is the peer implementation of IPhotoSource; the
// Graph, MSAL and people-tag plumbing it drives lives in the PhotoBook.Ingestion.OneDrive namespace.

/// <summary>
/// The OneDrive photo source (ADR-0011, doc 05): an album (the "Album + in-app refine" default) or a
/// folder, enumerated through Microsoft Graph and handed to the same
/// <see cref="PhotoImporter"/> as the local-folder path — folder-as-source and album-as-source are
/// the same pipeline after enumeration.
/// <para>
/// Everything that makes OneDrive different from a folder is expressed as richer
/// <see cref="SourcePhoto"/> fields: a stable <c>driveItem</c> id, the pre-computed
/// <c>file.hashes.sha256Hash</c> (which lets the importer skip a download entirely),
/// <c>photo.takenDateTime</c> for the date chain, and people tags when Graph exposes them.
/// </para>
/// <para>
/// It is created through <see cref="OneDrive.OneDriveClient.CreateFromConfiguration"/>, which throws
/// <see cref="OneDrive.OneDriveNotConfiguredException"/> — naming <c>SETUP.md</c> — until the user
/// has created their Entra app registration. Nothing here crashes and nothing silently does nothing.
/// </para>
/// </summary>
public sealed class OneDrivePhotoSource : IPhotoSource, ISkipReportingPhotoSource
{
    private readonly OneDrive.IOneDriveClient _client;
    private readonly OneDrive.IPeopleTagProvider _peopleTags;
    private readonly ConcurrentDictionary<string, DriveItem> _items = new(StringComparer.Ordinal);
    private readonly List<SkippedSourceFile> _skipped = [];
    private readonly Lock _skippedGate = new();
    private readonly string? _itemId;
    private readonly bool _recursive;

    private OneDrivePhotoSource(
        OneDrive.IOneDriveClient client,
        BookSourceKind kind,
        string? itemId,
        string displayName,
        bool recursive,
        string? deltaLink,
        OneDrive.IPeopleTagProvider? peopleTags)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _peopleTags = peopleTags ?? OneDrive.GraphPeopleTagProvider.Instance;
        _itemId = itemId;
        _recursive = recursive;
        Kind = kind;
        DisplayName = displayName;
        DeltaLink = deltaLink;
    }

    /// <summary>Binds the source to a OneDrive album (a bundle with an album facet).</summary>
    /// <param name="client">The Graph client.</param>
    /// <param name="albumId">The album's item id.</param>
    /// <param name="albumName">The album name, for the UI and the Import Report.</param>
    /// <param name="peopleTags">People-tag adapter; defaults to the Graph reader (see the doc 05 spike).</param>
    public static OneDrivePhotoSource ForAlbum(
        OneDrive.IOneDriveClient client, string albumId, string albumName, OneDrive.IPeopleTagProvider? peopleTags = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(albumId);
        return new OneDrivePhotoSource(client, BookSourceKind.OneDriveAlbum, albumId,
            string.IsNullOrWhiteSpace(albumName) ? albumId : albumName, recursive: false, deltaLink: null, peopleTags);
    }

    /// <summary>Binds the source to a OneDrive folder, recursive by default (doc 05).</summary>
    /// <param name="client">The Graph client.</param>
    /// <param name="folderId">The folder's item id; null means the drive root.</param>
    /// <param name="displayPath">A readable path for the UI and the Import Report.</param>
    /// <param name="recursive">Descend into subfolders.</param>
    /// <param name="deltaLink">
    /// The delta token stored in <see cref="BookSource.DeltaLink"/> from the previous sync; when present
    /// the source enumerates changes only and refreshes <see cref="DeltaLink"/> for the caller to persist.
    /// </param>
    /// <param name="peopleTags">People-tag adapter; defaults to the Graph reader.</param>
    public static OneDrivePhotoSource ForFolder(
        OneDrive.IOneDriveClient client,
        string? folderId,
        string displayPath,
        bool recursive = true,
        string? deltaLink = null,
        OneDrive.IPeopleTagProvider? peopleTags = null) =>
        new(client, BookSourceKind.OneDriveFolder, folderId,
            string.IsNullOrWhiteSpace(displayPath) ? "OneDrive" : displayPath, recursive, deltaLink, peopleTags);

    /// <summary>Binds the source from a book's stored <see cref="BookSource"/>.</summary>
    /// <param name="client">The Graph client.</param>
    /// <param name="source">The book's source binding; its kind must be a OneDrive kind.</param>
    /// <param name="peopleTags">People-tag adapter; defaults to the Graph reader.</param>
    public static OneDrivePhotoSource ForBookSource(
        OneDrive.IOneDriveClient client, BookSource source, OneDrive.IPeopleTagProvider? peopleTags = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Kind switch
        {
            BookSourceKind.OneDriveAlbum => ForAlbum(client, source.Id ?? throw new ArgumentException(
                "A OneDrive album source needs the album's item id.", nameof(source)), source.Path ?? "Album", peopleTags),
            BookSourceKind.OneDriveFolder => ForFolder(client, source.Id, source.Path ?? "OneDrive",
                recursive: true, source.DeltaLink, peopleTags),
            _ => throw new ArgumentException($"'{source.Kind}' is not a OneDrive source kind.", nameof(source)),
        };
    }

    /// <inheritdoc/>
    public BookSourceKind Kind { get; }

    /// <inheritdoc/>
    public string DisplayName { get; }

    /// <summary>
    /// The delta token to persist in <see cref="BookSource.DeltaLink"/> after a folder sync; null for
    /// album sources, which are always re-listed in full because bundles do not support delta (doc 05).
    /// </summary>
    public string? DeltaLink { get; private set; }

    /// <summary>
    /// Item ids Graph reported as deleted during the most recent delta enumeration. The catalog flags
    /// them <see cref="Photo.RemovedFromSource"/>; nothing is ever deleted from the book.
    /// </summary>
    public IReadOnlyList<string> DeletedSourceIds { get; private set; } = [];

    /// <inheritdoc/>
    public IReadOnlyList<SkippedSourceFile> SkippedFiles
    {
        get
        {
            lock (_skippedGate) return _skipped.ToArray();
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<SourcePhoto> EnumerateAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        lock (_skippedGate) _skipped.Clear();
        _items.Clear();
        DeletedSourceIds = [];

        await foreach (var item in EnumerateItemsAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            var name = item.Name ?? item.Id ?? "(unnamed item)";
            if (item.Id is null || item.Folder is not null) continue;

            if (item.File is null && item.Image is null && item.Photo is null)
            {
                lock (_skippedGate) _skipped.Add(new SkippedSourceFile(name, "Not a file."));
                continue;
            }

            if (!SupportedImageFormats.IsSupported(name))
            {
                lock (_skippedGate)
                    _skipped.Add(new SkippedSourceFile(name, "PhotoBook does not read this file type."));
                continue;
            }

            _items[item.Id] = item;

            var tags = await _peopleTags.GetPersonTagsAsync(item, ct).ConfigureAwait(false);
            yield return new SourcePhoto(
                SourceId: item.Id,
                FileName: name,
                SizeBytes: item.Size,
                LastModifiedUtc: item.LastModifiedDateTime?.UtcDateTime ??
                                 item.FileSystemInfo?.LastModifiedDateTime?.UtcDateTime,
                TakenDateTimeUtc: item.Photo?.TakenDateTime?.UtcDateTime,
                Sha256Hash: item.File?.Hashes?.Sha256Hash,
                PersonTags: tags.Count == 0 ? null : tags);
        }
    }

    /// <inheritdoc/>
    public async Task<Stream> OpenReadAsync(SourcePhoto photo, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(photo);

        if (!_items.TryGetValue(photo.SourceId, out var item))
        {
            item = await _client.GetItemAsync(photo.SourceId, ct).ConfigureAwait(false)
                   ?? throw new OneDrive.OneDriveSyncException(
                       $"OneDrive no longer has an item with id '{photo.SourceId}' ({photo.FileName}).");
            _items[photo.SourceId] = item;
        }

        return await _client.OpenItemContentAsync(item, startOffset: 0, ct).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<DriveItem> EnumerateItemsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (Kind == BookSourceKind.OneDriveAlbum)
        {
            // Bundles have no delta: album children are re-listed in full each sync, which is a handful
            // of 200-item pages for a pre-culled album (doc 05).
            await foreach (var item in _client.EnumerateAlbumItemsAsync(_itemId!, ct).ConfigureAwait(false))
                yield return item;
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(DeltaLink))
        {
            var delta = await _client.GetDeltaAsync(_itemId, DeltaLink, ct).ConfigureAwait(false);
            DeltaLink = delta.DeltaLink ?? DeltaLink;
            DeletedSourceIds = delta.DeletedIds;
            foreach (var item in delta.Items) yield return item;
            yield break;
        }

        await foreach (var item in _client.EnumerateFolderItemsAsync(_itemId, _recursive, ct).ConfigureAwait(false))
            yield return item;

        // Prime a delta token so the next sync of this folder is incremental.
        DeltaLink = await TryPrimeDeltaLinkAsync(ct).ConfigureAwait(false) ?? DeltaLink;
    }

    private async Task<string?> TryPrimeDeltaLinkAsync(CancellationToken ct)
    {
        try
        {
            var delta = await _client.GetDeltaAsync(_itemId, null, ct).ConfigureAwait(false);
            return delta.DeltaLink;
        }
        catch (Exception ex) when (ex is OneDrive.OneDriveSyncException or HttpRequestException)
        {
            // A missing delta token only costs a full re-list next time.
            return null;
        }
    }
}
