using System.Collections.Concurrent;
using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;
using PhotoBook.Core.Persistence;
using PhotoBook.Imaging;
using SkiaSharp;

namespace PhotoBook.Rendering;

/// <summary>
/// The production <see cref="IRenderImageSource"/>: an adapter over <c>PhotoBook.Imaging</c>'s
/// <see cref="IThumbnailCache"/> and <see cref="IImageDecoder"/>.
/// <para>
/// Screen rendering takes the 1024 px layout-preview tier straight from the thumbnail cache
/// (kernel §10) — fast, already adjustment-applied, and small enough that a whole spread stays in
/// memory. Export decodes the immutable original through the adjustment pipeline at the smallest
/// size that still meets the placement's 300 DPI target, never upsampling (doc 12 "Image handling").
/// </para>
/// <para>
/// Decoded images are cached by <c>(photoId, tier, adjustmentHash)</c> with a bounded, insertion-ordered eviction, so
/// the page loop's peak memory is roughly one full-resolution decode plus the current page's slots.
/// Call <see cref="Dispose"/> when the export or the preview session ends.
/// </para>
/// </summary>
public sealed class ThumbnailRenderImageSource : IRenderImageSource, IDisposable
{
    private const int PreviewTierPx = 1024;
    private const int GridTierPx = 256;

    private readonly ProjectPaths _paths;
    private readonly PhotoCatalog _catalog;
    private readonly IThumbnailCache? _thumbnails;
    private readonly IImageDecoder _decoder;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();
    private readonly ConcurrentDictionary<string, byte> _failed = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Creates a source over a project's originals, thumbnail cache and decoder.</summary>
    /// <param name="paths">The project folder layout — used to resolve <see cref="Photo.OriginalPath"/>.</param>
    /// <param name="catalog">The photo catalog, for content hashes, adjustments and original dimensions.</param>
    /// <param name="thumbnails">The thumbnail cache; when null, screen rendering decodes originals too.</param>
    /// <param name="decoder">The image decoder; defaults to a fresh <see cref="ImageDecoder"/>.</param>
    /// <param name="cacheCapacity">How many decoded images to keep resident.</param>
    public ThumbnailRenderImageSource(
        ProjectPaths paths,
        PhotoCatalog catalog,
        IThumbnailCache? thumbnails = null,
        IImageDecoder? decoder = null,
        int cacheCapacity = 16)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentOutOfRangeException.ThrowIfLessThan(cacheCapacity, 1);
        _paths = paths;
        _catalog = catalog;
        _thumbnails = thumbnails;
        _decoder = decoder ?? new ImageDecoder();
        _capacity = cacheCapacity;
    }

    /// <summary>Photo ids that failed to decode — the renderer's missing-image diagnostics agree with this set.</summary>
    public IReadOnlyCollection<string> FailedPhotoIds => _failed.Keys.ToList();

    /// <inheritdoc/>
    public RenderImage? GetImage(RenderImageRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrEmpty(request.PhotoId)) return null;

        var photo = _catalog.Find(request.PhotoId);
        if (photo is null || photo.DecodeFailed) return null;

        var tier = ChooseTier(photo, request);

        // The adjustment hash is part of the key, not just the photo and tier. Without it a photo
        // edited after it was first drawn keeps returning the pixels it had before the edit, so a
        // correction shows in the inspector's preview and never on the page.
        var edit = PhotoBook.Imaging.AdjustmentHash.Compute(photo.Adjustments);
        var key = $"{photo.Id}|{tier}|{edit}";

        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var hit))
            {
                Touch(key);
                return hit.Image;
            }
        }

        if (_failed.ContainsKey(photo.Id)) return null;

        RenderImage? loaded;
        try
        {
            loaded = Load(photo, tier, request.Target);
        }
        catch (Exception ex) when (ex is ImageDecodeException or IOException or UnauthorizedAccessException)
        {
            _failed.TryAdd(photo.Id, 0);
            return null;
        }

        if (loaded is null)
        {
            _failed.TryAdd(photo.Id, 0);
            return null;
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var raced))
            {
                loaded.Image.Dispose();
                Touch(key);
                return raced.Image;
            }

            // Drop the same photo's older edit states rather than letting them age out: a slider drag
            // mints a new hash per frame, and at this capacity those would evict the rest of the
            // spread and make every neighbouring page re-decode.
            DropOtherEditsOf(photo.Id, tier, key);

            _cache[key] = new CacheEntry(loaded);
            _order.AddLast(key);
            Evict();
            return loaded;
        }
    }

    /// <inheritdoc/>
    public async Task PrepareAsync(IEnumerable<RenderImageRequest> requests, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (_thumbnails is null) return;

        foreach (var request in requests)
        {
            ct.ThrowIfCancellationRequested();
            var photo = _catalog.Find(request.PhotoId);
            if (photo is null || string.IsNullOrEmpty(photo.ContentHash)) continue;

            var tier = ChooseTier(photo, request);
            if (tier is not (Tier.Preview1024 or Tier.Grid256)) continue;

            try
            {
                var thumbTier = tier == Tier.Grid256 ? ThumbnailTier.Grid256 : ThumbnailTier.Preview1024;
                await _thumbnails.GetOrCreateAsync(photo.ContentHash, thumbTier, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A thumbnail that will not build is handled at draw time as a missing image.
                _ = ex;
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            foreach (var entry in _cache.Values) entry.Image.Image.Dispose();
            _cache.Clear();
            _order.Clear();
        }
    }

    private Tier ChooseTier(Photo photo, RenderImageRequest request)
    {
        if (request.Target.IsExport()) return Tier.Full;
        if (request.MinLongEdgePx > 0 && request.MinLongEdgePx <= GridTierPx && _thumbnails is not null) return Tier.Grid256;
        var longEdge = Math.Max(photo.Width, photo.Height);
        // A photo smaller than the preview tier has no smaller tier worth building.
        return longEdge > 0 && longEdge <= GridTierPx && _thumbnails is not null ? Tier.Grid256 : Tier.Preview1024;
    }

    private RenderImage? Load(Photo photo, Tier tier, RenderTarget target)
    {
        if (tier != Tier.Full && _thumbnails is not null && !string.IsNullOrEmpty(photo.ContentHash))
        {
            var thumbTier = tier == Tier.Grid256 ? ThumbnailTier.Grid256 : ThumbnailTier.Preview1024;
            var path = _thumbnails.GetOrCreateAsync(photo.ContentHash, thumbTier).GetAwaiter().GetResult();
            var image = SKImage.FromEncodedData(path);
            if (image is not null) return new RenderImage(image, PhotoWidth(photo, image), PhotoHeight(photo, image));
        }

        var original = ResolveOriginal(photo);
        if (original is null || !File.Exists(original)) return null;

        // Export honors the adjustment stack at source resolution; originals stay immutable (kernel §2).
        var maxLongEdge = tier switch
        {
            Tier.Grid256 => GridTierPx,
            Tier.Preview1024 => PreviewTierPx,
            _ => 0,
        };

        var decoded = _decoder
            .DecodeAdjustedAsync(original, photo.Adjustments, maxLongEdge)
            .GetAwaiter().GetResult();

        var skImage = ToSKImage(decoded);
        return skImage is null ? null : new RenderImage(skImage, PhotoWidth(photo, skImage), PhotoHeight(photo, skImage));
    }

    private string? ResolveOriginal(Photo photo)
    {
        if (string.IsNullOrWhiteSpace(photo.OriginalPath)) return null;
        return Path.IsPathRooted(photo.OriginalPath) ? photo.OriginalPath : _paths.OriginalFile(photo.OriginalPath);
    }

    private static int PhotoWidth(Photo photo, SKImage image) => photo.Width > 0 ? photo.Width : image.Width;

    private static int PhotoHeight(Photo photo, SKImage image) => photo.Height > 0 ? photo.Height : image.Height;

    /// <summary>
    /// Wraps a <see cref="DecodedImage"/> — tightly packed BGRA8888, sRGB, already EXIF-oriented — as
    /// an <see cref="SKImage"/> without another copy of the pixel data than Skia demands.
    /// </summary>
    internal static SKImage? ToSKImage(DecodedImage decoded)
    {
        ArgumentNullException.ThrowIfNull(decoded);
        var alpha = decoded.IsOpaque
            ? SKAlphaType.Opaque
            : decoded.AlphaIsPremultiplied ? SKAlphaType.Premul : SKAlphaType.Unpremul;
        var info = new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Bgra8888, alpha, SKColorSpace.CreateSrgb());
        return SKImage.FromPixelCopy(info, decoded.Pixels, decoded.Stride);
    }

    private void Touch(string key)
    {
        var node = _order.Find(key);
        if (node is null) return;
        _order.Remove(node);
        _order.AddLast(node);
    }

    private void Evict()
    {
        while (_cache.Count > _capacity && _order.First is { } oldest)
        {
            _order.RemoveFirst();
            if (_cache.Remove(oldest.Value, out var entry)) entry.Image.Image.Dispose();
        }
    }

    /// <summary>
    /// Removes this photo's entries for the same tier that carry a different edit hash. One photo at
    /// one tier only ever needs its current appearance resident; keeping superseded ones would let a
    /// single slider drag flush the whole spread out of the cache.
    /// </summary>
    private void DropOtherEditsOf(string photoId, Tier tier, string keepKey)
    {
        var prefix = $"{photoId}|{tier}|";

        for (var node = _order.First; node is not null;)
        {
            var next = node.Next;
            var key = node.Value;

            if (key.StartsWith(prefix, StringComparison.Ordinal) &&
                !string.Equals(key, keepKey, StringComparison.Ordinal))
            {
                _order.Remove(node);
                if (_cache.Remove(key, out var entry)) entry.Image.Image.Dispose();
            }

            node = next;
        }
    }

    private enum Tier
    {
        Grid256,
        Preview1024,
        Full,
    }

    private readonly record struct CacheEntry(RenderImage Image);
}
