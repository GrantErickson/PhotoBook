using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoBook.App.Services;
using PhotoBook.Core.Model;

namespace PhotoBook.App.ViewModels.Pages;

/// <summary>
/// One thumbnail in a bin (doc 09 §3.5). The same view model serves all three bins; what differs is
/// the badge it carries — Upcoming items know the later page they are sitting on, so they can show
/// <c>p. 9</c> and jump there, and tray items show the year that took them out of the book.
/// </summary>
public sealed partial class BinItemViewModel : ObservableObject
{
    private readonly ThumbnailProvider _thumbnails;
    private bool _requested;

    /// <param name="photo">The catalog entry this tile shows.</param>
    /// <param name="bin">Which bin it belongs to.</param>
    /// <param name="thumbnails">The shared thumbnail cache.</param>
    public BinItemViewModel(Photo photo, BinTab bin, ThumbnailProvider thumbnails)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(thumbnails);

        Photo = photo;
        Bin = bin;
        _thumbnails = thumbnails;
        Thumbnail = thumbnails.Peek(photo.ContentHash);
    }

    /// <summary>The photo behind the tile.</summary>
    public Photo Photo { get; }

    /// <summary>Which bin this tile is currently listed in.</summary>
    public BinTab Bin { get; private set; }

    /// <summary>The decoded 256 px thumbnail, or null until it arrives.</summary>
    [ObservableProperty]
    private BitmapSource? _thumbnail;

    /// <summary>True while the tile is the bin's selection.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>The chapter page this photo is placed on — Upcoming only (R13).</summary>
    [ObservableProperty]
    private int? _pageNumber;

    /// <summary>That page's id, so a pull-forward can vacate the right slot.</summary>
    [ObservableProperty]
    private string? _pageId;

    /// <summary>The slot the photo occupies on that page.</summary>
    [ObservableProperty]
    private string? _slotId;

    /// <summary>The file name, shown in the tooltip and used in undo descriptions.</summary>
    public string FileName => Photo.OriginalFileName;

    /// <summary>Capture date, formatted for the tile's caption.</summary>
    public string TakenAtDisplay => Photo.TakenAt.ToString("MMM d, h:mm tt");

    /// <summary>Just the day — the side-docked list row has no width for the time.</summary>
    public string DayDisplay => Photo.TakenAt.ToString("MMM d");

    /// <summary>The tier layout actually uses (R26).</summary>
    public Tier EffectiveTier => Photo.EffectiveTier;

    /// <summary>The tier chip's letter.</summary>
    public string TierLabel => EffectiveTier.ToString();

    /// <summary>The <c>p. 9</c> badge text for an Upcoming item; empty otherwise.</summary>
    public string PageBadge => PageNumber is { } number ? $"p. {number}" : string.Empty;

    /// <summary>True when this item carries a page badge.</summary>
    public bool HasPageBadge => PageNumber is not null;

    /// <summary>The year that put a tray item outside the book (R6).</summary>
    public string YearBadge => Photo.TakenAt.Year.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>True for tray items, which show a year badge instead of a page badge.</summary>
    public bool IsOutsideTray => Bin == BinTab.Outside;

    /// <summary>Full tooltip: file name, date and where the photo currently lives.</summary>
    public string Tooltip => Bin switch
    {
        BinTab.Upcoming => $"{FileName}\n{TakenAtDisplay}\nOn page {PageNumber} — drag here to pull it forward",
        BinTab.Outside => $"{FileName}\n{TakenAtDisplay}\nDated {Photo.TakenAt.Year}, outside this book's year — change its date to bring it in",
        _ => $"{FileName}\n{TakenAtDisplay}",
    };

    /// <summary>Re-points an existing tile at another bin, so a refresh can reuse it and keep its bitmap.</summary>
    /// <param name="bin">The bin the tile now belongs to.</param>
    /// <param name="pageNumber">The page it is placed on, for Upcoming.</param>
    /// <param name="pageId">That page's id.</param>
    /// <param name="slotId">The slot it occupies.</param>
    public void Retarget(BinTab bin, int? pageNumber = null, string? pageId = null, string? slotId = null)
    {
        Bin = bin;
        PageNumber = pageNumber;
        PageId = pageId;
        SlotId = slotId;

        OnPropertyChanged(nameof(Bin));
        OnPropertyChanged(nameof(IsOutsideTray));
        Refresh();
    }

    /// <summary>The drag payload this tile hands to the page canvas (doc 09 §3.2).</summary>
    public PhotoDragPayload ToDragPayload() => new(
        Photo.Id,
        Bin switch
        {
            BinTab.Upcoming => PhotoDragOrigin.UpcomingBin,
            BinTab.Outside => PhotoDragOrigin.OutsideTray,
            _ => PhotoDragOrigin.UnplacedBin,
        },
        PageId,
        SlotId,
        PageNumber);

    /// <summary>Pulls the thumbnail in, once, when the tile first becomes visible.</summary>
    /// <param name="ct">Cancels when the bin is rebuilt for another month.</param>
    public async Task EnsureThumbnailAsync(CancellationToken ct = default)
    {
        if (_requested || Thumbnail is not null)
        {
            return;
        }

        _requested = true;
        try
        {
            var image = await _thumbnails.GetAsync(Photo.ContentHash, ct: ct).ConfigureAwait(false);
            if (image is not null)
            {
                JobQueue.PostUi(() => Thumbnail = image);
            }
        }
        catch (OperationCanceledException)
        {
            _requested = false;
        }
        catch
        {
            // One unreadable file must not stop the bin filling in.
        }
    }

    /// <summary>Re-reads everything derived from the model after an edit.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(EffectiveTier));
        OnPropertyChanged(nameof(TierLabel));
        OnPropertyChanged(nameof(TakenAtDisplay));
        OnPropertyChanged(nameof(DayDisplay));
        OnPropertyChanged(nameof(PageBadge));
        OnPropertyChanged(nameof(HasPageBadge));
        OnPropertyChanged(nameof(YearBadge));
        OnPropertyChanged(nameof(Tooltip));
    }

    partial void OnPageNumberChanged(int? value)
    {
        OnPropertyChanged(nameof(PageBadge));
        OnPropertyChanged(nameof(HasPageBadge));
        OnPropertyChanged(nameof(Tooltip));
    }
}
