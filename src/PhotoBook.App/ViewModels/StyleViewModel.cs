using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoBook.App.Services;
using PhotoBook.Core.Model;

namespace PhotoBook.App.ViewModels;

/// <summary>What kind of editor one style leaf gets.</summary>
public enum StyleFieldKind
{
    /// <summary>A slider with a numeric readout — sizes, widths, opacities.</summary>
    Number,

    /// <summary>A swatch plus a <c>#RRGGBB</c> box.</summary>
    Color,

    /// <summary>A picker over the bundled OFL families (doc 10 §3).</summary>
    Family,

    /// <summary>A switch — <c>imageBorder.enabled</c>, <c>overlayScrim.enabled</c>.</summary>
    Toggle,
}

/// <summary>
/// How one leaf of <see cref="Style"/> is read and written. Writing <c>null</c> clears the leaf,
/// which is what "reset to inherited" means in a sparse cascade (doc 10 §2).
/// </summary>
/// <param name="Key">Stable identity, used as the undo coalescing key.</param>
/// <param name="Label">The row's label.</param>
/// <param name="Kind">Which editor to draw.</param>
/// <param name="Read">Reads the leaf out of a style, or null when that style does not set it.</param>
/// <param name="Write">Writes the leaf into a style; null clears it.</param>
/// <param name="Minimum">Slider minimum, for <see cref="StyleFieldKind.Number"/>.</param>
/// <param name="Maximum">Slider maximum.</param>
/// <param name="Step">Slider tick.</param>
/// <param name="Unit">Suffix on the readout, e.g. "pt".</param>
/// <param name="Format">Numeric format string for the readout.</param>
public sealed record StyleFieldSpec(
    string Key,
    string Label,
    StyleFieldKind Kind,
    Func<Style, object?> Read,
    Action<Style, object?> Write,
    double Minimum = 0,
    double Maximum = 1,
    double Step = 1,
    string Unit = "",
    string Format = "0.##");

/// <summary>
/// One editable style leaf: its current effective value, the cascade level that supplies it, and a
/// per-field <em>reset to inherited</em> — the inheritance chip doc 10 §2 asks the panel to show.
/// </summary>
public sealed partial class StyleFieldViewModel : ObservableObject
{
    private readonly StyleViewModel _owner;
    private bool _suspend;

    /// <summary>Creates the row.</summary>
    /// <param name="owner">The panel that applies edits.</param>
    /// <param name="spec">How the leaf is read and written.</param>
    public StyleFieldViewModel(StyleViewModel owner, StyleFieldSpec spec)
    {
        _owner = owner;
        Spec = spec;
    }

    /// <summary>How this leaf is read and written.</summary>
    public StyleFieldSpec Spec { get; }

    /// <summary>The row's label.</summary>
    public string Label => Spec.Label;

    /// <summary>Which editor to draw.</summary>
    public StyleFieldKind Kind => Spec.Kind;

    /// <summary>Slider minimum.</summary>
    public double Minimum => Spec.Minimum;

    /// <summary>Slider maximum.</summary>
    public double Maximum => Spec.Maximum;

    /// <summary>Slider tick.</summary>
    public double Step => Spec.Step;

    /// <summary>The font families a <see cref="StyleFieldKind.Family"/> row offers.</summary>
    public IReadOnlyList<string> Families => StyleViewModel.BundledFamilies;

    /// <summary>The cascade level the effective value comes from.</summary>
    [ObservableProperty]
    private StyleLevel _level = StyleLevel.Default;

    /// <summary>True when the level being edited sets this leaf itself, so it can be reset.</summary>
    [ObservableProperty]
    private bool _isOwned;

    private double _number;

    private string _text = string.Empty;

    private bool _flag;

    /// <summary>The value of a <see cref="StyleFieldKind.Number"/> row.</summary>
    public double NumberValue
    {
        get => _number;
        set
        {
            if (SetProperty(ref _number, value) && !_suspend)
            {
                _owner.Apply(this, value);
            }

            OnPropertyChanged(nameof(NumberText));
        }
    }

    /// <summary>The readout beside the slider, e.g. "10.5 pt".</summary>
    public string NumberText =>
        _number.ToString(Spec.Format, CultureInfo.CurrentCulture) +
        (string.IsNullOrEmpty(Spec.Unit) ? string.Empty : " " + Spec.Unit);

    /// <summary>The value of a <see cref="StyleFieldKind.Color"/> or <see cref="StyleFieldKind.Family"/> row.</summary>
    public string TextValue
    {
        get => _text;
        set
        {
            if (!SetProperty(ref _text, value) || _suspend)
            {
                return;
            }

            // A half-typed hex string is not a colour yet; wait rather than writing "#F" into the model.
            if (Kind == StyleFieldKind.Color && !Converters.HexToBrushConverter.TryParse(value, out _))
            {
                return;
            }

            _owner.Apply(this, value);
        }
    }

    /// <summary>The value of a <see cref="StyleFieldKind.Toggle"/> row.</summary>
    public bool FlagValue
    {
        get => _flag;
        set
        {
            if (SetProperty(ref _flag, value) && !_suspend)
            {
                _owner.Apply(this, value);
            }
        }
    }

    /// <summary>Pushes a fresh effective value in without echoing it back as an edit.</summary>
    /// <param name="value">The effective value from the resolved cascade.</param>
    /// <param name="level">Which level supplies it.</param>
    /// <param name="owned">True when the level being edited sets it.</param>
    public void Sync(object? value, StyleLevel level, bool owned)
    {
        _suspend = true;
        try
        {
            switch (Kind)
            {
                case StyleFieldKind.Number:
                    NumberValue = value is double d ? d : 0;
                    break;
                case StyleFieldKind.Toggle:
                    FlagValue = value is bool b && b;
                    break;
                default:
                    TextValue = value as string ?? string.Empty;
                    break;
            }
        }
        finally
        {
            _suspend = false;
        }

        Level = level;
        IsOwned = owned;
        ResetCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(NumberText));
    }

    /// <summary>Clears this leaf at the level being edited, restoring inheritance (doc 10 §2).</summary>
    [RelayCommand(CanExecute = nameof(IsOwned))]
    private void Reset() => _owner.Apply(this, null);
}

/// <summary>A titled group of style rows.</summary>
/// <param name="Title">The section heading.</param>
/// <param name="Description">One line of why the section exists.</param>
/// <param name="Fields">The rows.</param>
public sealed record StyleSectionViewModel(
    string Title, string Description, IReadOnlyList<StyleFieldViewModel> Fields);

/// <summary>
/// The style panel (R23, doc 10): image borders across every photo at once, journal and caption
/// typography set independently, month-title type, the overlay scrim and the page background —
/// each with the inheritance chip and per-field <em>reset to inherited</em> the cascade needs.
/// <para>
/// The scope selector picks which level an edit lands on — <b>Book</b>, <b>Chapter</b> or
/// <b>Page</b> — and overrides stay sparse: only the leaves actually changed are written, and
/// clearing the last leaf removes the override object entirely so inheritance is restored rather
/// than snapshotted. Style edits never touch geometry, so they never Pin or Detach a page
/// (doc 10 §2).
/// </para>
/// </summary>
public sealed partial class StyleViewModel : ObservableObject
{
    /// <summary>The three OFL families that ship with the book renderer (doc 10 §3).</summary>
    public static readonly string[] BundledFamilies =
    [
        BuiltInStyles.JournalFamily,
        BuiltInStyles.CaptionFamily,
        BuiltInStyles.MonthTitleFamily,
    ];

    private readonly ProjectSession _session;
    private readonly UndoStack _undo;
    private readonly List<StyleFieldViewModel> _fields = [];

    /// <summary>Creates the panel over the open session.</summary>
    public StyleViewModel(ProjectSession session, UndoStack undo)
    {
        _session = session;
        _undo = undo;
        Sections = BuildSections();
        Refresh();
    }

    /// <summary>Raised after any style edit, so the preview can re-render live.</summary>
    public event Action? StyleChanged;

    /// <summary>The rows, grouped.</summary>
    public IReadOnlyList<StyleSectionViewModel> Sections { get; }

    /// <summary>Which level edits land on.</summary>
    [ObservableProperty]
    private StyleLevel _scope = StyleLevel.Book;

    /// <summary>The chapter the Chapter and Page scopes refer to.</summary>
    [ObservableProperty]
    private Chapter? _chapter;

    /// <summary>The page the Page scope refers to.</summary>
    [ObservableProperty]
    private Page? _page;

    /// <summary>True when a chapter is selected, so Chapter scope is available.</summary>
    public bool CanScopeToChapter => Chapter is not null;

    /// <summary>True when a page is selected, so Page scope is available.</summary>
    public bool CanScopeToPage => Page is not null && Chapter is not null;

    /// <summary>What the scope selector's help line says.</summary>
    public string ScopeDescription => Scope switch
    {
        StyleLevel.Chapter => "Edits apply to this chapter only. Everything else keeps the book's style.",
        StyleLevel.Page => "Edits apply to this page only — the most specific level of the cascade.",
        _ => "Edits apply to the whole book: borders on every photo, type on every page (R23).",
    };

    /// <summary>How many leaves the level being edited overrides right now.</summary>
    public int OverrideCount => _fields.Count(f => f.IsOwned);

    /// <summary>True when the level being edited overrides anything at all.</summary>
    public bool HasOverrides => OverrideCount > 0;

    /// <summary>The label on the "clear all" button.</summary>
    public string ClearAllLabel => Scope switch
    {
        StyleLevel.Chapter => "Clear this chapter's overrides",
        StyleLevel.Page => "Clear this page's overrides",
        _ => "Reset the book to the shipped defaults",
    };

    // ------------------------------------------------------------------ binding

    /// <summary>Points the panel at a chapter and page; safe to call on every selection change.</summary>
    /// <param name="chapter">The selected chapter, or null.</param>
    /// <param name="page">The selected page, or null.</param>
    public void Attach(Chapter? chapter, Page? page)
    {
        Chapter = chapter;
        Page = page;

        if (Scope == StyleLevel.Page && !CanScopeToPage)
        {
            Scope = CanScopeToChapter ? StyleLevel.Chapter : StyleLevel.Book;
        }
        else if (Scope == StyleLevel.Chapter && !CanScopeToChapter)
        {
            Scope = StyleLevel.Book;
        }

        Refresh();
    }

    partial void OnScopeChanged(StyleLevel value)
    {
        OnPropertyChanged(nameof(ScopeDescription));
        OnPropertyChanged(nameof(ClearAllLabel));
        Refresh();
    }

    partial void OnChapterChanged(Chapter? value) => OnPropertyChanged(nameof(CanScopeToChapter));

    partial void OnPageChanged(Page? value) => OnPropertyChanged(nameof(CanScopeToPage));

    /// <summary>Re-reads every row from the resolved cascade.</summary>
    public void Refresh()
    {
        var book = _session.Book;
        if (book is null)
        {
            return;
        }

        var chapter = Scope == StyleLevel.Book ? null : Chapter;
        var page = Scope == StyleLevel.Page ? Page : null;
        var effective = StyleResolver.Resolve(book, chapter, page);
        var target = CurrentOverride();

        foreach (var field in _fields)
        {
            var level = StyleResolver.LevelOf(field.Spec.Read, book, chapter, page);
            var owned = target is not null && field.Spec.Read(target) is not null;
            field.Sync(field.Spec.Read(effective), level, owned);
        }

        OnPropertyChanged(nameof(OverrideCount));
        OnPropertyChanged(nameof(HasOverrides));
        ClearAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The sparse override object at the level being edited, or null when there is none.</summary>
    private Style? CurrentOverride() => Scope switch
    {
        StyleLevel.Chapter => Chapter?.StyleOverride,
        StyleLevel.Page => Page?.StyleOverride,
        _ => _session.Book?.Style,
    };

    private void SetOverride(Style? style)
    {
        switch (Scope)
        {
            case StyleLevel.Chapter when Chapter is { } chapter:
                chapter.StyleOverride = style;
                break;
            case StyleLevel.Page when Page is { } page:
                page.StyleOverride = style;
                break;
            case StyleLevel.Book when _session.Book is { } book:
                // The book level always exists; an empty style there simply falls through to the
                // shipped defaults, which is exactly what "no override" means at the top of the chain.
                book.Style = style ?? new Style();
                break;
        }

        _session.MarkDirty();
    }

    /// <summary>
    /// Writes one leaf at the current scope as a single undoable edit. Passing <c>null</c> clears
    /// the leaf; when that empties the override, the override object itself goes away so
    /// inheritance is restored rather than frozen (doc 10 §2).
    /// </summary>
    /// <param name="field">The row being edited.</param>
    /// <param name="value">The new value, or null to reset to inherited.</param>
    internal void Apply(StyleFieldViewModel field, object? value)
    {
        if (_session.Book is null)
        {
            return;
        }

        var before = ModelClone.CloneStyle(CurrentOverride());
        var draft = ModelClone.CloneStyle(CurrentOverride()) ?? new Style();
        field.Spec.Write(draft, value);
        Prune(draft);
        var after = draft.IsEmpty ? null : draft;

        var description = value is null
            ? $"Reset {field.Label.ToLowerInvariant()}"
            : $"Set {field.Label.ToLowerInvariant()}";

        // Slider drags coalesce inside the 500 ms window, so a whole drag is one entry; the key
        // names the level as well as the leaf, because coalescing must never cross targets.
        _undo.ExecuteValue(
            $"{description} ({Scope.ToString().ToLowerInvariant()})",
            before,
            after,
            style =>
            {
                SetOverride(ModelClone.CloneStyle(style));
                Refresh();
                StyleChanged?.Invoke();
            },
            $"style:{Scope}:{ScopeKey()}:{field.Spec.Key}");
    }

    /// <summary>
    /// Drops any block whose last leaf was just cleared. Without this, resetting the final field of
    /// a block would leave <c>"journalText": {}</c> behind — technically harmless, but it is no
    /// longer a sparse override, and the next reader cannot tell "inherited" from "set to nothing".
    /// </summary>
    private static void Prune(Style style)
    {
        if (style.JournalText is { Family: null, SizePt: null, Color: null, Weight: null, LineHeight: null })
        {
            style.JournalText = null;
        }

        if (style.CaptionText is { Family: null, SizePt: null, Color: null, Weight: null, LineHeight: null })
        {
            style.CaptionText = null;
        }

        if (style.MonthTitle is { Family: null, SizePt: null, Color: null, Weight: null, LineHeight: null })
        {
            style.MonthTitle = null;
        }

        if (style.ImageBorder is { Enabled: null, WidthPt: null, Color: null, CornerRadiusPt: null })
        {
            style.ImageBorder = null;
        }

        if (style.OverlayScrim is { Enabled: null, MaxOpacity: null, PaddingPt: null })
        {
            style.OverlayScrim = null;
        }

        if (style.Background is { Kind: null, Color: null })
        {
            style.Background = null;
        }
    }

    private string ScopeKey() => Scope switch
    {
        StyleLevel.Chapter => Chapter?.Month.ToString(CultureInfo.InvariantCulture) ?? "-",
        StyleLevel.Page => Page?.Id ?? "-",
        _ => "book",
    };

    /// <summary>Drops every override at the level being edited in one entry.</summary>
    [RelayCommand(CanExecute = nameof(HasOverrides))]
    private void ClearAll()
    {
        if (_session.Book is null)
        {
            return;
        }

        var before = ModelClone.CloneStyle(CurrentOverride());
        _undo.ExecuteValue(
            ClearAllLabel,
            before,
            (Style?)null,
            style =>
            {
                SetOverride(ModelClone.CloneStyle(style));
                Refresh();
                StyleChanged?.Invoke();
            });
    }

    // ------------------------------------------------------------------ the rows

    private IReadOnlyList<StyleSectionViewModel> BuildSections()
    {
        var sections = new List<StyleSectionViewModel>
        {
            Section(
                "Journal text",
                "The long-form text of a day. Independent of captions — R23 requires the two sizes to differ.",
                Text("journal", s => s.JournalText, (s, t) => s.JournalText = t, 6, 36)),

            Section(
                "Captions",
                "Below-image and overlay captions.",
                Text("caption", s => s.CaptionText, (s, t) => s.CaptionText = t, 5, 24)),

            Section(
                "Month titles",
                "The display type on each chapter's opening page (R24).",
                Text("monthTitle", s => s.MonthTitle, (s, t) => s.MonthTitle = t, 18, 140)),

            Section(
                "Image borders",
                "One switch, every photo in scope. The stroke runs inside the visible image, so borders never move anything (doc 10 §5).",
                [
                    Field(new StyleFieldSpec(
                        "border.enabled", "Draw borders", StyleFieldKind.Toggle,
                        s => s.ImageBorder?.Enabled,
                        (s, v) => Border(s).Enabled = (bool?)v)),
                    Field(new StyleFieldSpec(
                        "border.width", "Width", StyleFieldKind.Number,
                        s => s.ImageBorder?.WidthPt,
                        (s, v) => Border(s).WidthPt = (double?)v,
                        0.5, 8, 0.5, "pt")),
                    Field(new StyleFieldSpec(
                        "border.color", "Colour", StyleFieldKind.Color,
                        s => s.ImageBorder?.Color,
                        (s, v) => Border(s).Color = (string?)v)),
                    Field(new StyleFieldSpec(
                        "border.radius", "Corner radius", StyleFieldKind.Number,
                        s => s.ImageBorder?.CornerRadiusPt,
                        (s, v) => Border(s).CornerRadiusPt = (double?)v,
                        0, 24, 1, "pt")),
                ]),

            Section(
                "Overlay scrim",
                "The gradient under an overlay caption that keeps white text legible on any photo (doc 10 §4).",
                [
                    Field(new StyleFieldSpec(
                        "scrim.enabled", "Draw the scrim", StyleFieldKind.Toggle,
                        s => s.OverlayScrim?.Enabled,
                        (s, v) => Scrim(s).Enabled = (bool?)v)),
                    Field(new StyleFieldSpec(
                        "scrim.opacity", "Maximum opacity", StyleFieldKind.Number,
                        s => s.OverlayScrim?.MaxOpacity,
                        (s, v) => Scrim(s).MaxOpacity = (double?)v,
                        0, 1, 0.05, string.Empty, "0.00")),
                    Field(new StyleFieldSpec(
                        "scrim.padding", "Padding", StyleFieldKind.Number,
                        s => s.OverlayScrim?.PaddingPt,
                        (s, v) => Scrim(s).PaddingPt = (double?)v,
                        0, 36, 1, "pt")),
                ]),

            Section(
                "Page background",
                "v1 renders a solid colour. Black with white text is the tested path (R21).",
                [
                    Field(new StyleFieldSpec(
                        "background.color", "Colour", StyleFieldKind.Color,
                        s => s.Background?.Color,
                        (s, v) => Background(s).Color = (string?)v)),
                ]),
        };

        return sections;
    }

    private StyleSectionViewModel Section(string title, string description, IReadOnlyList<StyleFieldViewModel> fields) =>
        new(title, description, fields);

    private IReadOnlyList<StyleFieldViewModel> Text(
        string key,
        Func<Style, TextStyle?> read,
        Action<Style, TextStyle?> write,
        double minSize,
        double maxSize)
    {
        return
        [
            Field(new StyleFieldSpec(
                $"{key}.family", "Font", StyleFieldKind.Family,
                s => read(s)?.Family,
                (s, v) => Block(s, read, write).Family = (string?)v)),
            Field(new StyleFieldSpec(
                $"{key}.size", "Size", StyleFieldKind.Number,
                s => read(s)?.SizePt,
                (s, v) => Block(s, read, write).SizePt = (double?)v,
                minSize, maxSize, 0.5, "pt")),
            Field(new StyleFieldSpec(
                $"{key}.color", "Colour", StyleFieldKind.Color,
                s => read(s)?.Color,
                (s, v) => Block(s, read, write).Color = (string?)v)),
            Field(new StyleFieldSpec(
                $"{key}.lineHeight", "Line height", StyleFieldKind.Number,
                s => read(s)?.LineHeight,
                (s, v) => Block(s, read, write).LineHeight = (double?)v,
                0.9, 2.2, 0.05, "×", "0.00")),
        ];
    }

    private StyleFieldViewModel Field(StyleFieldSpec spec)
    {
        var field = new StyleFieldViewModel(this, spec);
        _fields.Add(field);
        return field;
    }

    // The four helpers below create the owning block on demand and clear it again when its last
    // leaf goes, which is what keeps an override sparse rather than a copied-down snapshot.
    private static TextStyle Block(Style style, Func<Style, TextStyle?> read, Action<Style, TextStyle?> write)
    {
        var block = read(style);
        if (block is null)
        {
            block = new TextStyle();
            write(style, block);
        }

        return block;
    }

    private static ImageBorderStyle Border(Style style) => style.ImageBorder ??= new ImageBorderStyle();

    private static OverlayScrimStyle Scrim(Style style) => style.OverlayScrim ??= new OverlayScrimStyle();

    private static BackgroundStyle Background(Style style) => style.Background ??= new BackgroundStyle();
}
