namespace PhotoBook.Rendering;

/// <summary>What a renderer noticed while drawing a page.</summary>
public enum RenderDiagnosticKind
{
    /// <summary>An <see cref="PhotoBook.Core.Model.ImageSlot"/> with no photo (R14, doc 09 §3.6).</summary>
    EmptySlot,

    /// <summary>A placement referencing a photo id that is not in the catalog.</summary>
    MissingPhoto,

    /// <summary>A photo whose pixels could not be produced by the image source.</summary>
    MissingImageData,

    /// <summary>Journal or title text that did not fit its slot. Never shrunk — see kernel §9.</summary>
    TextOverflow,

    /// <summary>A caption that exceeded its band and was ellipsized (doc 10 §4).</summary>
    CaptionTruncated,

    /// <summary>A placement whose effective print resolution is below 200 DPI (kernel §11).</summary>
    LowEffectiveDpi,

    /// <summary>A style font family that is not installed and was substituted (doc 10 §3).</summary>
    FontFallback,

    /// <summary>A style feature that v1 does not render, such as an image background (R21).</summary>
    UnsupportedStyle,

    /// <summary>A gutter-spanning slot whose facing page was not supplied, so it drew page-local (R18).</summary>
    UnresolvedSpan,
}

/// <summary>Whether a render diagnostic blocks export or merely informs.</summary>
public enum RenderSeverity
{
    /// <summary>Reported in the export log only.</summary>
    Info,

    /// <summary>Worth telling the user about; maps to a preflight warning.</summary>
    Warning,

    /// <summary>Wrong output; maps to a preflight error that disables Export.</summary>
    Error,
}

/// <summary>
/// One structured, user-readable observation from a page render, carrying enough identity for the
/// editor to navigate straight to the offending page, slot or photo (doc 12 "Preflight gate").
/// </summary>
/// <param name="Kind">What was noticed.</param>
/// <param name="Severity">How much it matters.</param>
/// <param name="Message">A sentence a user can act on.</param>
/// <param name="BookPageNumber">The book-wide page number, when known.</param>
/// <param name="PageId">The page's id.</param>
/// <param name="SlotId">The slot's id, for slot-scoped findings.</param>
/// <param name="PhotoId">The photo's id, for photo-scoped findings.</param>
/// <param name="Value">A number the message quotes, e.g. the computed DPI.</param>
public sealed record RenderDiagnostic(
    RenderDiagnosticKind Kind,
    RenderSeverity Severity,
    string Message,
    int? BookPageNumber = null,
    string? PageId = null,
    string? SlotId = null,
    string? PhotoId = null,
    double? Value = null)
{
    /// <inheritdoc/>
    public override string ToString() =>
        BookPageNumber is { } page ? $"[{Severity}] p{page}: {Message}" : $"[{Severity}] {Message}";
}

/// <summary>
/// What one page render produced: the diagnostics, and the geometry the caller may want back — hit
/// testing in the editor uses <see cref="SlotRects"/> to turn a mouse point into a slot.
/// </summary>
/// <param name="Diagnostics">Everything noticed while drawing, in draw order.</param>
/// <param name="SlotRects">Slot id → the device rect the slot's image occupies after bleed extension.</param>
/// <param name="VisibleImageRects">Slot id → the device rect the photo's visible pixels occupy (differs when zoom &lt; 1).</param>
/// <param name="TextSlotRects">Text slot id → the device rect it occupies.</param>
public sealed record PageRenderResult(
    IReadOnlyList<RenderDiagnostic> Diagnostics,
    IReadOnlyDictionary<string, SkiaSharp.SKRect> SlotRects,
    IReadOnlyDictionary<string, SkiaSharp.SKRect> VisibleImageRects,
    IReadOnlyDictionary<string, SkiaSharp.SKRect> TextSlotRects)
{
    /// <summary>An empty result.</summary>
    public static PageRenderResult Empty { get; } = new([], new Dictionary<string, SkiaSharp.SKRect>(),
        new Dictionary<string, SkiaSharp.SKRect>(), new Dictionary<string, SkiaSharp.SKRect>());

    /// <summary>True when nothing worse than <see cref="RenderSeverity.Info"/> was reported.</summary>
    public bool IsClean => Diagnostics.All(d => d.Severity == RenderSeverity.Info);

    /// <summary>The number of empty image slots drawn on this page (R14).</summary>
    public int EmptySlotCount => Diagnostics.Count(d => d.Kind == RenderDiagnosticKind.EmptySlot);
}
