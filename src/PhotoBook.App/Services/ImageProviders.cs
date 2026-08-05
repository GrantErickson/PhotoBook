using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoBook.Core.Abstractions;
using PhotoBook.Imaging;

namespace PhotoBook.App.Services;

/// <summary>
/// Supplies frozen <see cref="BitmapSource"/> thumbnails to the photo grid from the imaging
/// layer's on-disk cache, with a bounded in-memory LRU so a 2,000-photo month never holds
/// 2,000 decoded bitmaps.
/// </summary>
public sealed class ThumbnailProvider
{
    private readonly Dictionary<string, BitmapSource> _memory = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();
    private readonly Lock _gate = new();
    private readonly int _capacity;

    private ThumbnailCache? _cache;

    public ThumbnailProvider(int capacity = 600) => _capacity = capacity;

    /// <summary>Points the provider at the open project's cache. Clears anything from the last book.</summary>
    public void Attach(ThumbnailCache? cache)
    {
        lock (_gate)
        {
            _cache = cache;
            _memory.Clear();
            _order.Clear();
        }
    }

    /// <summary>Returns a cached thumbnail if one is already decoded, without touching the disk.</summary>
    public BitmapSource? Peek(string contentHash)
    {
        lock (_gate)
        {
            return _memory.GetValueOrDefault(contentHash);
        }
    }

    /// <summary>
    /// Loads (generating if necessary) the thumbnail for a photo. Safe to call from a background
    /// thread; the returned bitmap is frozen and therefore usable from the UI thread.
    /// </summary>
    public async Task<BitmapSource?> GetAsync(
        string contentHash, ThumbnailTier tier = ThumbnailTier.Grid256, CancellationToken ct = default)
    {
        var hit = Peek(contentHash);
        if (hit is not null && tier == ThumbnailTier.Grid256)
        {
            return hit;
        }

        ThumbnailCache? cache;
        lock (_gate)
        {
            cache = _cache;
        }

        if (cache is null)
        {
            return null;
        }

        string path;
        try
        {
            path = await cache.GetOrCreateAsync(contentHash, tier, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }

        var bitmap = LoadFrozen(path);
        if (bitmap is not null && tier == ThumbnailTier.Grid256)
        {
            Remember(contentHash, bitmap);
        }

        return bitmap;
    }

    private void Remember(string key, BitmapSource bitmap)
    {
        lock (_gate)
        {
            if (_memory.ContainsKey(key))
            {
                _order.Remove(key);
            }

            _memory[key] = bitmap;
            _order.AddFirst(key);

            while (_order.Count > _capacity && _order.Last is { } oldest)
            {
                _memory.Remove(oldest.Value);
                _order.RemoveLast();
            }
        }
    }

    private static BitmapSource? LoadFrozen(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// The shell's main-window handle. The WAM account picker parents to it, so without this the
/// Windows sign-in dialog can open behind the app.
/// </summary>
public static class WindowHandles
{
    /// <summary>
    /// Returns the main window's HWND, or <see cref="IntPtr.Zero"/> before it exists. MSAL invokes
    /// this from whichever thread is acquiring the token, and <c>Application.MainWindow</c> is a
    /// DispatcherObject, so the lookup marshals to the UI thread rather than throwing.
    /// </summary>
    public static IntPtr MainHandle()
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return IntPtr.Zero;
        }

        return app.Dispatcher.CheckAccess() ? Handle(app) : app.Dispatcher.Invoke(() => Handle(app));
    }

    private static IntPtr Handle(System.Windows.Application app) =>
        app.MainWindow is { } window
            ? new System.Windows.Interop.WindowInteropHelper(window).Handle
            : IntPtr.Zero;
}

/// <summary>Converts the renderer's BGRA8888 output into a WPF bitmap without an extra copy pass.</summary>
public static class PixelBridge
{
    /// <summary>
    /// Wraps a <see cref="DecodedImage"/> (tightly packed BGRA8888, which is exactly WPF's Bgra32)
    /// as a frozen <see cref="BitmapSource"/>.
    /// </summary>
    public static BitmapSource ToBitmap(DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var bitmap = BitmapSource.Create(
            image.Width,
            image.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            image.Pixels,
            image.Stride);

        bitmap.Freeze();
        return bitmap;
    }
}
