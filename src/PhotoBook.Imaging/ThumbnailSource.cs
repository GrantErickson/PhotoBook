using PhotoBook.Core.Model;
using PhotoBook.Core.Persistence;

namespace PhotoBook.Imaging;

/// <summary>
/// Everything <see cref="ThumbnailCache"/> needs to (re)build a photo's derived pixels: where the
/// immutable original lives, and the edits to apply. Both come from <c>photos.json</c> — the cache
/// itself stores no user intent (kernel §5), it only reads it.
/// </summary>
/// <param name="OriginalPath">Absolute path of the archived original under <c>originals/</c>.</param>
/// <param name="Adjustments">The photo's current non-destructive edits.</param>
public sealed record ThumbnailSource(string OriginalPath, ImageAdjustments Adjustments);

/// <summary>
/// How the cache turns a content hash back into a photo. Supplied by the caller because the imaging
/// layer does not read the catalog — it is handed what it needs, exactly like an
/// <see cref="Core.Abstractions.IImageAnalyzer"/>.
/// <para>
/// Returns null when the hash is unknown, which is how <see cref="ThumbnailCache.CollectOrphansAsync"/>
/// tells a stale artifact from a live one.
/// </para>
/// </summary>
/// <param name="contentHash">Full SHA-256 of the photo's original bytes, lowercase.</param>
public delegate ThumbnailSource? ThumbnailSourceResolver(string contentHash);

/// <summary>Ready-made <see cref="ThumbnailSourceResolver"/>s for the common wiring.</summary>
public static class ThumbnailSources
{
    /// <summary>
    /// Resolves against a live photo catalog. The catalog is fetched through a delegate rather than
    /// captured, so the resolver keeps working across a reload and always sees the photo's
    /// <em>current</em> adjustments — which is precisely what makes an edit change the cache key.
    /// </summary>
    /// <param name="paths">The project's path set.</param>
    /// <param name="catalog">Supplies the current catalog; may return null before a project is open.</param>
    public static ThumbnailSourceResolver FromCatalog(ProjectPaths paths, Func<PhotoCatalog?> catalog)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(catalog);

        return contentHash =>
        {
            var photo = catalog()?.FindByContentHash(contentHash);
            if (photo is null || string.IsNullOrEmpty(photo.OriginalPath)) return null;
            return new ThumbnailSource(
                paths.OriginalFile(photo.OriginalPath),
                ImageAdjustments.From(photo.Adjustments));
        };
    }
}
