using Microsoft.Graph.Models;

namespace PhotoBook.Ingestion.OneDrive;

/// <summary>
/// One OneDrive album, as listed by the bundles API
/// (<c>GET /me/drive/bundles?$filter=bundle/album ne null</c>). The "Album + in-app refine" workflow
/// of kernel §2 starts by picking one of these.
/// </summary>
/// <param name="Id">The bundle's <c>driveItem</c> id — stored in <see cref="PhotoBook.Core.Model.BookSource.Id"/>.</param>
/// <param name="Name">The album name the curator gave it, e.g. <c>Book 2024</c>.</param>
/// <param name="ItemCount">How many items the album holds, when Graph says.</param>
/// <param name="LastModifiedUtc">When the album last changed.</param>
public sealed record OneDriveAlbum(string Id, string Name, int? ItemCount = null, DateTime? LastModifiedUtc = null);

/// <summary>One folder in the drive tree, for the "pick a folder instead" fallback (doc 05).</summary>
/// <param name="Id">The folder's <c>driveItem</c> id.</param>
/// <param name="Name">The folder name.</param>
/// <param name="Path">A readable path when Graph supplies a parent reference.</param>
/// <param name="ChildCount">Number of children, when Graph says.</param>
public sealed record OneDriveFolderEntry(string Id, string Name, string? Path = null, int? ChildCount = null);

/// <summary>
/// The result of one delta page-through of a folder (doc 05: folder sources use
/// <c>GET …/items/{folderId}/delta</c> with the stored <c>deltaLink</c> when available).
/// </summary>
/// <param name="Items">Items created or changed since the delta token was issued.</param>
/// <param name="DeletedIds">Ids Graph reported as deleted; the catalog flags them <c>removedFromSource</c>.</param>
/// <param name="DeltaLink">The token to store in <see cref="PhotoBook.Core.Model.BookSource.DeltaLink"/> for next time.</param>
public sealed record OneDriveDeltaResult(
    IReadOnlyList<DriveItem> Items,
    IReadOnlyList<string> DeletedIds,
    string? DeltaLink);
