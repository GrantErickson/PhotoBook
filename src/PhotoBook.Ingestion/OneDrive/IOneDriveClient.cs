using Microsoft.Graph.Models;

namespace PhotoBook.Ingestion.OneDrive;

/// <summary>
/// The Microsoft Graph surface the connector actually uses (ADR-0011): list albums, page an album's
/// members, walk a folder, delta-sync a folder, and stream an item's bytes. Keeping it this small
/// means the Graph people-tag spike, unit tests, and any future source (Google Photos, iCloud) all
/// substitute one class rather than reworking the pipeline.
/// </summary>
public interface IOneDriveClient
{
    /// <summary>The signed-in user's drive id, fetched once and cached.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<string> GetDriveIdAsync(CancellationToken ct = default);

    /// <summary>
    /// Lists the user's albums — OneDrive <b>bundles</b> with an album facet
    /// (<c>GET /me/drive/bundles?$filter=bundle/album ne null</c>).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<OneDriveAlbum>> ListAlbumsAsync(CancellationToken ct = default);

    /// <summary>Lists the child folders of a folder, or of the drive root when <paramref name="folderId"/> is null.</summary>
    /// <param name="folderId">The parent folder's item id; null means the drive root.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<OneDriveFolderEntry>> ListFoldersAsync(string? folderId = null, CancellationToken ct = default);

    /// <summary>
    /// Streams an album's members, 200 per page. Albums are re-listed in full on every sync: bundles
    /// do not support delta, and a pre-culled album is a handful of requests (doc 05).
    /// </summary>
    /// <param name="albumId">The bundle id.</param>
    /// <param name="ct">Cancellation token.</param>
    IAsyncEnumerable<DriveItem> EnumerateAlbumItemsAsync(string albumId, CancellationToken ct = default);

    /// <summary>Streams a folder's items, optionally descending into subfolders (recursive by default in the picker).</summary>
    /// <param name="folderId">The folder's item id; null means the drive root.</param>
    /// <param name="recursive">Descend into subfolders.</param>
    /// <param name="ct">Cancellation token.</param>
    IAsyncEnumerable<DriveItem> EnumerateFolderItemsAsync(string? folderId, bool recursive, CancellationToken ct = default);

    /// <summary>Runs a delta query over a folder subtree, following <c>@odata.nextLink</c> to the end.</summary>
    /// <param name="folderId">The folder's item id; null means the drive root.</param>
    /// <param name="deltaLink">The token stored from the previous sync; null does a full delta enumeration.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<OneDriveDeltaResult> GetDeltaAsync(string? folderId, string? deltaLink, CancellationToken ct = default);

    /// <summary>Fetches one item by id, e.g. to refresh a short-lived download URL.</summary>
    /// <param name="itemId">The <c>driveItem</c> id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DriveItem?> GetItemAsync(string itemId, CancellationToken ct = default);

    /// <summary>
    /// Opens an item's bytes for reading, preferring the pre-authenticated
    /// <c>@microsoft.graph.downloadUrl</c> and falling back to <c>/content</c>. Honors
    /// <c>Retry-After</c> on 429/503 and supports resuming with an HTTP <c>Range</c> offset.
    /// </summary>
    /// <param name="item">The item to download.</param>
    /// <param name="startOffset">Byte offset to resume from; 0 downloads the whole file.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Stream> OpenItemContentAsync(DriveItem item, long startOffset = 0, CancellationToken ct = default);
}
