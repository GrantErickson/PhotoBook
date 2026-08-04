using PhotoBook.Core.Model;

namespace PhotoBook.Core.Abstractions;

/// <summary>
/// What an <see cref="IImageAnalyzer"/> is given for one photo (doc 06). All of it is data the caller
/// already has: an analyzer never reads the catalog or the project folder.
/// </summary>
/// <param name="ContentHash">Full SHA-256 of the original bytes; the cache key and the logging id.</param>
/// <param name="AnalysisCopyPath">
/// Absolute path of the pre-adjustment, EXIF-oriented, sRGB analysis copy
/// (<c>cache/thumbs/1024a/{hash}.jpg</c>, long edge 1024 px). Analysis reads this and nothing else, so
/// tone edits never silently re-tier a photo.
/// </param>
/// <param name="PixelWidth">Width of the <em>original</em> image in pixels; regions are normalized anyway.</param>
/// <param name="PixelHeight">Height of the <em>original</em> image in pixels.</param>
/// <param name="PersonTags">
/// People tags pulled during ingestion; empty for local-folder sources. Analyzers may use them as
/// hints, but injecting <see cref="FocusKind.Person"/> regions is fusion's job, not theirs.
/// </param>
public sealed record AnalysisInput(
    string ContentHash,
    string AnalysisCopyPath,
    int PixelWidth,
    int PixelHeight,
    IReadOnlyList<PersonTag> PersonTags);
