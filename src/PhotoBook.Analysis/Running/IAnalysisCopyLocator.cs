using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;

namespace PhotoBook.Analysis.Running;

/// <summary>
/// Finds (and if necessary builds) the analysis copy for a photo — <c>cache/thumbs/1024a/{hash}.jpg</c>,
/// pre-adjustment, EXIF-oriented, sRGB. Analysis reads that copy and nothing else, which is why
/// brightening a photo never silently re-tiers it (doc 05's invalidation matrix).
/// </summary>
public interface IAnalysisCopyLocator
{
    /// <summary>Returns the absolute path of the photo's analysis copy, building it if needed.</summary>
    /// <param name="photo">The catalog record.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string> GetAnalysisCopyPathAsync(Photo photo, CancellationToken ct = default);
}

/// <summary>
/// The production locator: Core's thumbnail cache, asked for
/// <see cref="ThumbnailTier.Analysis1024"/>. Keeping this an adapter is what lets Analysis stay
/// free of any dependency on the imaging project (doc 02 §2).
/// </summary>
public sealed class ThumbnailCacheAnalysisCopyLocator : IAnalysisCopyLocator
{
    private readonly IThumbnailCache _cache;

    /// <summary>Creates the adapter.</summary>
    /// <param name="cache">The project's thumbnail cache.</param>
    public ThumbnailCacheAnalysisCopyLocator(IThumbnailCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    /// <inheritdoc/>
    public Task<string> GetAnalysisCopyPathAsync(Photo photo, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(photo);
        return _cache.GetOrCreateAsync(photo.ContentHash, ThumbnailTier.Analysis1024, ct);
    }
}

/// <summary>A locator built from a delegate — for tests and for hosts that already know the paths.</summary>
public sealed class DelegateAnalysisCopyLocator : IAnalysisCopyLocator
{
    private readonly Func<Photo, CancellationToken, Task<string>> _resolve;

    /// <summary>Creates a locator from an async delegate.</summary>
    public DelegateAnalysisCopyLocator(Func<Photo, CancellationToken, Task<string>> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        _resolve = resolve;
    }

    /// <summary>Creates a locator from a synchronous delegate.</summary>
    public DelegateAnalysisCopyLocator(Func<Photo, string> resolve)
        : this((photo, _) => Task.FromResult((resolve ?? throw new ArgumentNullException(nameof(resolve)))(photo)))
    {
    }

    /// <inheritdoc/>
    public Task<string> GetAnalysisCopyPathAsync(Photo photo, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(photo);
        return _resolve(photo, ct);
    }
}
