using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoBook.App.Services;
using PhotoBook.Core.Model;

namespace PhotoBook.App.ViewModels;

/// <summary>One tile in the month grid. Loads its thumbnail lazily so a 2,000-photo month is cheap.</summary>
public sealed partial class PhotoItemViewModel : ObservableObject
{
    private readonly ThumbnailProvider _thumbnails;
    private bool _requested;

    public PhotoItemViewModel(Photo photo, ThumbnailProvider thumbnails)
    {
        Photo = photo;
        _thumbnails = thumbnails;
        Thumbnail = thumbnails.Peek(photo.ContentHash);
    }

    public Photo Photo { get; }

    [ObservableProperty]
    private BitmapSource? _thumbnail;

    [ObservableProperty]
    private bool _isSelected;

    public string FileName => Photo.OriginalFileName;

    public DateTime TakenAt => Photo.TakenAt;

    public string TakenAtDisplay => Photo.TakenAt.ToString("MMM d, h:mm tt");

    public Tier EffectiveTier => Photo.UserTierOverride ?? Photo.Tier ?? Core.Model.Tier.B;

    public string TierLabel => EffectiveTier.ToString();

    public bool IsTierOverridden => Photo.UserTierOverride is not null;

    public bool DateUncertain => Photo.DateUncertain;

    public bool Excluded => Photo.Excluded;

    public bool HasUserFocus => Photo.FocusRegions.Any(r => r.Kind == FocusKind.User);

    /// <summary>Pulls the thumbnail in, once, when the tile first becomes visible.</summary>
    public async Task EnsureThumbnailAsync(CancellationToken ct = default)
    {
        if (_requested || Thumbnail is not null)
        {
            return;
        }

        _requested = true;
        var image = await _thumbnails.GetAsync(Photo.ContentHash, ct: ct).ConfigureAwait(false);
        if (image is not null)
        {
            JobQueue.PostUi(() => Thumbnail = image);
        }
    }

    /// <summary>Re-reads everything derived from the model after an edit.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(EffectiveTier));
        OnPropertyChanged(nameof(TierLabel));
        OnPropertyChanged(nameof(IsTierOverridden));
        OnPropertyChanged(nameof(Excluded));
        OnPropertyChanged(nameof(DateUncertain));
        OnPropertyChanged(nameof(HasUserFocus));
        OnPropertyChanged(nameof(TakenAt));
        OnPropertyChanged(nameof(TakenAtDisplay));
    }

    /// <summary>
    /// Drops the cached bitmap so the tile re-reads it after the photo's pixels change.
    /// <para>
    /// Both halves are needed. Clearing the property alone is not enough — the provider's own LRU is
    /// keyed by content hash, which an edit does not change, so the next fetch would hand back the
    /// same stale bitmap it just discarded.
    /// </para>
    /// </summary>
    public void InvalidateThumbnail()
    {
        _thumbnails.Forget(Photo.ContentHash);
        _requested = false;
        Thumbnail = null;
    }
}
