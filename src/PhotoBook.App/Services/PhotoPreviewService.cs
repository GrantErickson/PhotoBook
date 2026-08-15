using System.Windows.Media.Imaging;
using PhotoBook.Core.Model;
using PhotoBook.Imaging;

namespace PhotoBook.App.Services;

/// <summary>
/// Renders the Photos-tab inspector's live preview of a photo's non-destructive edits (doc 09 §2.4,
/// R11): the original is decoded <b>once</b> at the 1024 px preview tier and kept, and every slider
/// move replays <see cref="AdjustmentPipeline"/> over that buffer on a background thread.
///
/// <para>
/// Two rules this class exists to enforce. First, <b>the UI thread never decodes and never runs the
/// pipeline</b> (doc 09 §6) — everything here hands back a frozen <see cref="BitmapSource"/> built off
/// the dispatcher. Second, <b>the original file is never written</b>: the decoder opens
/// <c>originals/</c> read-only and the pipeline only ever mutates a decoded copy (kernel §5), so an
/// adjustment is a parameter change and nothing more.
/// </para>
///
/// <para>
/// The base buffer is deliberately the <em>unadjusted</em> decode rather than the cached
/// <see cref="Core.Abstractions.ThumbnailTier.Preview1024"/> file, whose pixels already have the saved
/// stack baked in — replaying the stack over that would double-apply it, most visibly for the geometry
/// stage. Decoding once costs a few hundred milliseconds; every slider frame after that is one
/// pipeline pass (~90 ms at 1024 px) and stays interactive.
/// </para>
/// </summary>
public sealed class PhotoPreviewService
{
    /// <summary>Long edge of the preview tier the editor works at (doc 09 §2.4: "1024 px").</summary>
    public const int PreviewLongEdge = 1024;

    private readonly ProjectSession _session;
    private readonly ImageDecoder _decoder = new();
    private readonly AdjustmentPipeline _pipeline = AdjustmentPipeline.Default;
    private readonly System.Threading.Lock _gate = new();
    private readonly Dictionary<string, Task<DecodedImage>> _bases = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _order = new();
    private readonly int _capacity;

    /// <param name="session">The open project; supplies the path of the archived original.</param>
    /// <param name="capacity">How many decoded originals to keep (about 4 MB each).</param>
    public PhotoPreviewService(ProjectSession session, int capacity = 4)
    {
        _session = session;
        _capacity = Math.Max(1, capacity);
    }

    /// <summary>
    /// The unadjusted decode of a photo at the preview tier, shared between callers and kept for
    /// re-use. The returned buffer is read-only by convention — the pipeline never writes to it.
    /// </summary>
    /// <param name="photo">The photo to decode.</param>
    /// <param name="ct">Cancels the caller's wait, never the shared decode.</param>
    public Task<DecodedImage> GetBaseAsync(Photo photo, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(photo);

        var paths = _session.Paths ?? throw new InvalidOperationException("No project is open.");
        var file = paths.OriginalFile(photo.OriginalPath);

        Task<DecodedImage> decode;
        lock (_gate)
        {
            if (!_bases.TryGetValue(photo.ContentHash, out var existing) || existing.IsFaulted)
            {
                // No caller's token is passed in: one impatient slider must not poison the decode
                // every other caller is waiting on.
                existing = _decoder.DecodeAsync(file, PreviewLongEdge, CancellationToken.None);
                _bases[photo.ContentHash] = existing;
                _order.AddFirst(photo.ContentHash);
                Trim();
            }
            else
            {
                _order.Remove(photo.ContentHash);
                _order.AddFirst(photo.ContentHash);
            }

            decode = existing;
        }

        return ct.CanBeCanceled ? decode.WaitAsync(ct) : decode;
    }

    /// <summary>
    /// Applies <paramref name="adjustments"/> to the photo's preview buffer and returns a frozen
    /// bitmap. Runs entirely off the UI thread; pass the identity stack (or null) for the "before"
    /// half of a before/after comparison.
    /// </summary>
    /// <param name="photo">The photo being edited.</param>
    /// <param name="adjustments">The stack to replay; null renders the untouched original.</param>
    /// <param name="ct">Cancellation — a superseded slider frame is abandoned mid-pipeline.</param>
    public async Task<BitmapSource> RenderAsync(
        Photo photo, AdjustmentStack? adjustments, CancellationToken ct = default)
    {
        var basis = await GetBaseAsync(photo, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var stack = ImageAdjustments.From(adjustments);
        return await Task.Run(
            () =>
            {
                var pixels = stack.IsIdentity ? basis : _pipeline.Apply(basis, stack, ct);
                ct.ThrowIfCancellationRequested();
                return PixelBridge.ToBitmap(pixels);
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A grid-sized copy of a rendered preview, so an edited photo's tile can be refreshed in place
    /// without waiting for the on-disk thumbnail tier to be rebuilt.
    /// </summary>
    /// <param name="preview">A frozen preview bitmap from <see cref="RenderAsync"/>.</param>
    /// <param name="longEdge">Long edge of the result in pixels.</param>
    public static BitmapSource Downscale(BitmapSource preview, int longEdge = 256)
    {
        ArgumentNullException.ThrowIfNull(preview);

        var scale = longEdge / (double)Math.Max(preview.PixelWidth, preview.PixelHeight);
        if (scale >= 1)
        {
            return preview;
        }

        var scaled = new TransformedBitmap(preview, new System.Windows.Media.ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    /// <summary>Drops a photo's cached decode — after a re-import, or when memory should be reclaimed.</summary>
    /// <param name="contentHash">The photo's content hash.</param>
    public void Invalidate(string contentHash)
    {
        lock (_gate)
        {
            _bases.Remove(contentHash);
            _order.Remove(contentHash);
        }
    }

    /// <summary>Drops every cached decode — on close, or when a different book is opened.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _bases.Clear();
            _order.Clear();
        }
    }

    private void Trim()
    {
        while (_order.Count > _capacity && _order.Last is { } oldest)
        {
            _bases.Remove(oldest.Value);
            _order.RemoveLast();
        }
    }
}
