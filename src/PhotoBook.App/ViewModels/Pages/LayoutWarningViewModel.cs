using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoBook.App.Services;
using PhotoBook.Engine;

namespace PhotoBook.App.ViewModels.Pages;

/// <summary>How one page of the chapter fares in a planned run — the colour of its chip in the map.</summary>
public enum LayoutPageFate
{
    /// <summary>Outside the run's scope; not touched.</summary>
    Untouched,

    /// <summary>Will be thrown away and rebuilt.</summary>
    Rebuilt,

    /// <summary>Kept because the user pinned it (R16).</summary>
    Pinned,

    /// <summary>Kept because its layout is hand-built; never rebuilt, whatever the checkbox says (R15).</summary>
    Detached,

    /// <summary>New pages are spliced in after this page.</summary>
    InsertAfter,
}

/// <summary>One page in the dialog's chapter map.</summary>
/// <param name="Number">The 1-based page number within the chapter.</param>
/// <param name="Fate">What the run does to it.</param>
public sealed record LayoutPageChip(int Number, LayoutPageFate Fate)
{
    /// <summary>True for the two states the user is being reassured about.</summary>
    public bool IsProtected => Fate is LayoutPageFate.Pinned or LayoutPageFate.Detached;

    /// <summary>The tooltip, so the colour code never has to be memorized.</summary>
    public string Explanation => Fate switch
    {
        LayoutPageFate.Rebuilt => $"Page {Number} will be re-laid out",
        LayoutPageFate.Pinned => $"Page {Number} is pinned and will not change",
        LayoutPageFate.Detached => $"Page {Number} has a hand-built layout and will not change",
        LayoutPageFate.InsertAfter => $"New pages will be inserted after page {Number}",
        _ => $"Page {Number} is not affected",
    };
}

/// <summary>
/// The R16 warning modal (doc 09 §3.8): it states, before anything runs, exactly which pages change
/// and which are protected, offers the optional <em>include pinned pages</em> escape, and labels its
/// confirm button with the size of the operation ("Re-lay out 5 pages").
/// <para>
/// Both plans arrive precomputed, so ticking the checkbox re-reads the sentence and the map with no
/// engine round trip and no waiting.
/// </para>
/// </summary>
public sealed partial class LayoutWarningViewModel : ObservableObject
{
    private readonly LayoutConfirmationRequest _request;

    /// <summary>Creates the modal's model from the two dry runs.</summary>
    public LayoutWarningViewModel(LayoutConfirmationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;
        Rebuild();
    }

    /// <summary>Raised when the user answers: the accepted plan, or null for cancel.</summary>
    public event Action<LayoutPlan?>? CloseRequested;

    /// <summary>
    /// The doc 09 §3.8 checkbox, off by default. Detached pages are never included, whatever this
    /// says — a hand-built layout outranks an auto-layout command.
    /// </summary>
    [ObservableProperty]
    private bool _includePinnedPages;

    /// <summary>The pages of the chapter, colour-coded by what the run does to them.</summary>
    public ObservableCollection<LayoutPageChip> Map { get; } = [];

    /// <summary>The plan the buttons would actually execute.</summary>
    public LayoutPlan Current => IncludePinnedPages && _request.WithPinned is { } alt ? alt : _request.Plan;

    /// <summary>The dialog title.</summary>
    public string Title => Current.Kind switch
    {
        LayoutCommandKind.Day => "Re-layout this day",
        LayoutCommandKind.InsertUnplaced => "Insert pages for unplaced photos",
        _ => "Auto-layout rest of chapter",
    };

    /// <summary>The one-line subtitle under the title.</summary>
    public string Subtitle => Current.Kind switch
    {
        LayoutCommandKind.InsertUnplaced => "Existing pages keep their layouts.",
        _ => "This replaces those pages entirely. Undo puts them back in one step.",
    };

    /// <summary>The sentence naming the concrete page numbers.</summary>
    public string Headline => Current.Headline;

    /// <summary>The sentence naming what survives; empty when nothing is protected.</summary>
    public string ProtectedNote => Current.ProtectedNote;

    /// <summary>True when there is a protected-pages sentence to show.</summary>
    public bool HasProtectedNote => !string.IsNullOrEmpty(ProtectedNote);

    /// <summary>The confirm button's label.</summary>
    public string ConfirmLabel => Current.ConfirmLabel;

    /// <summary>True when the run has Pinned pages to offer un-pinning.</summary>
    public bool CanIncludePinned => _request.WithPinned is not null;

    /// <summary>The checkbox's label, which names the pages it would unpin.</summary>
    public string IncludePinnedLabel =>
        $"Include pinned pages ({PageRanges.Format(_request.Plan.PinnedPages)}) — unpins them first";

    /// <summary>True when detached pages are being held back, which the checkbox cannot override.</summary>
    public bool HasDetachedPages => Current.DetachedPages.Count > 0;

    /// <summary>True when confirming would do nothing.</summary>
    public bool IsConfirmEnabled => Current.HasWork;

    /// <summary>Engine warnings worth reading before agreeing, worst first.</summary>
    public ObservableCollection<string> Notes { get; } = [];

    /// <summary>True when there is at least one engine note.</summary>
    public bool HasNotes => Notes.Count > 0;

    partial void OnIncludePinnedPagesChanged(bool value) => Rebuild();

    private void Rebuild()
    {
        var plan = Current;

        Map.Clear();
        var rebuilt = plan.AffectedPages.ToHashSet();
        var pinned = plan.PinnedPages.ToHashSet();
        var detached = plan.DetachedPages.ToHashSet();
        var insertAfter = plan.InsertedAfterPages.ToHashSet();

        for (var number = 1; number <= plan.ChapterPageCount; number++)
        {
            var fate =
                detached.Contains(number) ? LayoutPageFate.Detached :
                pinned.Contains(number) ? LayoutPageFate.Pinned :
                rebuilt.Contains(number) ? LayoutPageFate.Rebuilt :
                insertAfter.Contains(number) ? LayoutPageFate.InsertAfter :
                LayoutPageFate.Untouched;

            Map.Add(new LayoutPageChip(number, fate));
        }

        Notes.Clear();
        foreach (var note in plan.Diagnostics
                     .Where(d => d.Severity != LayoutSeverity.Info)
                     .Select(Describe)
                     .Distinct()
                     .Take(4))
        {
            Notes.Add(note);
        }

        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Headline));
        OnPropertyChanged(nameof(ProtectedNote));
        OnPropertyChanged(nameof(HasProtectedNote));
        OnPropertyChanged(nameof(ConfirmLabel));
        OnPropertyChanged(nameof(HasDetachedPages));
        OnPropertyChanged(nameof(IsConfirmEnabled));
        OnPropertyChanged(nameof(HasNotes));
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    private static string Describe(LayoutDiagnostic diagnostic)
    {
        var where = diagnostic.PageNumber is { } page ? $"Page {page}: " : string.Empty;
        return diagnostic.Kind switch
        {
            LayoutDiagnosticKind.TextOverflow => where + "a day's journal text will not fit and would be clipped.",
            LayoutDiagnosticKind.TextNeedsSpread => where + "a day's journal text wants a whole spread.",
            LayoutDiagnosticKind.EmptySlot => where + "a photo slot will be left empty.",
            LayoutDiagnosticKind.UnplaceablePhoto => "Some photos could not be placed and stay in the Unplaced bin.",
            LayoutDiagnosticKind.Crowding => where + "this page is carrying more than it holds comfortably.",
            LayoutDiagnosticKind.NoTemplate => where + "no template fitted; the page falls back.",
            LayoutDiagnosticKind.FocusClipped => where + "part of a photo's focus area is cropped away.",
            LayoutDiagnosticKind.FaceNearGutter => where + "a face sits close to the spread gutter.",
            LayoutDiagnosticKind.JournalOnlyDayCarried => where + "a text-only day rides along with its neighbour.",
            LayoutDiagnosticKind.NothingToDo => "Every page in scope is protected, so nothing would change.",
            _ => where + diagnostic.Message,
        };
    }

    [RelayCommand(CanExecute = nameof(IsConfirmEnabled))]
    private void Confirm() => CloseRequested?.Invoke(Current);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}
