using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoBook.Core.Model;

namespace PhotoBook.App.ViewModels;

/// <summary>One page in the chapter, with its rendered preview.</summary>
public sealed partial class PageItemViewModel : ObservableObject
{
    public PageItemViewModel(Page page, int number)
    {
        Page = page;
        Number = number;
    }

    public Page Page { get; }

    [ObservableProperty]
    private int _number;

    [ObservableProperty]
    private BitmapSource? _preview;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Left pages carry even indices; the side decides template mirroring (R20).</summary>
    public PageSide Side => Number % 2 == 0 ? PageSide.Left : PageSide.Right;

    public bool IsPinned => Page.Pinned;

    public bool IsDetached => Page.DetachedTemplate is not null;

    public int PhotoCount => Page.Placements.Count;

    public string TemplateName => Page.DetachedTemplate?.Name ?? Page.TemplateRef ?? "—";

    public void Refresh()
    {
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(IsDetached));
        OnPropertyChanged(nameof(PhotoCount));
        OnPropertyChanged(nameof(TemplateName));
        OnPropertyChanged(nameof(Side));
    }
}
