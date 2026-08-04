using PhotoBook.Core.Model;

namespace PhotoBook.Core.Abstractions;

/// <summary>
/// One candidate photo as the source system describes it, before anything is downloaded (doc 05).
/// Everything except <see cref="SourceId"/> and <see cref="FileName"/> is optional, because a plain
/// folder knows far less than Microsoft Graph does.
/// </summary>
/// <param name="SourceId">
/// Stable id within the source: the Graph <c>driveItem</c> id, or the absolute path for a local
/// folder. Ingestion matches re-syncs on this first, then on content hash.
/// </param>
/// <param name="FileName">The file name as the source knows it, extension included.</param>
/// <param name="SizeBytes">Size in bytes when known.</param>
/// <param name="LastModifiedUtc">Source last-modified time; the last resort of the date chain.</param>
/// <param name="TakenDateTimeUtc">
/// The source's own capture timestamp (Graph <c>photo.takenDateTime</c>) when it supplies one. EXIF
/// still wins over this in the date chain (kernel §10).
/// </param>
/// <param name="Sha256Hash">
/// The source's precomputed SHA-256 (consumer OneDrive supplies <c>file.hashes.sha256Hash</c>). When
/// present and already in the catalog, the bytes are never fetched at all.
/// </param>
/// <param name="PersonTags">People tags the source exposes; empty when it exposes none.</param>
public sealed record SourcePhoto(
    string SourceId,
    string FileName,
    long? SizeBytes = null,
    DateTime? LastModifiedUtc = null,
    DateTime? TakenDateTimeUtc = null,
    string? Sha256Hash = null,
    IReadOnlyList<PersonTag>? PersonTags = null);
