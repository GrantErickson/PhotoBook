using PhotoBook.Core.Model;
using PhotoBook.Core.Persistence;

namespace PhotoBook.Rendering;

/// <summary>Progress for one exported sheet, for the UI's per-page progress bar (doc 12).</summary>
/// <param name="SheetIndex">0-based index of the sheet just written.</param>
/// <param name="SheetCount">Total sheets in this export.</param>
/// <param name="FirstPageNumber">Book page number of the first page on the sheet.</param>
public readonly record struct PdfExportProgress(int SheetIndex, int SheetCount, int FirstPageNumber)
{
    /// <summary>Completion in <c>0..1</c>.</summary>
    public double Fraction => SheetCount <= 0 ? 1 : (double)(SheetIndex + 1) / SheetCount;
}

/// <summary>Everything one PDF export needs.</summary>
public sealed record PdfExportRequest
{
    /// <summary>The loaded project — book, photos, journal and chapters.</summary>
    public required ProjectSnapshot Project { get; init; }

    /// <summary>The print profile: page sizes, bleed, output mode, DPI and encoding (doc 12).</summary>
    public required PrintProfile Profile { get; init; }

    /// <summary>Resolves a template id to a library template; detached pages carry their own snapshot.</summary>
    public required Func<string, Template?> Templates { get; init; }

    /// <summary>Where the pixels come from — normally a <see cref="ThumbnailRenderImageSource"/>.</summary>
    public required IRenderImageSource Images { get; init; }

    /// <summary>The destination path. The file is written atomically: temp file, then rename.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Which pages to emit.</summary>
    public ExportScope Scope { get; init; } = ExportScope.WholeBook;

    /// <summary>The font library; substitutions are reported in the result.</summary>
    public FontLibrary Fonts { get; init; } = FontLibrary.Default;

    /// <summary>
    /// True for the doc 12 draft escape hatch: 150 DPI, JPEG q75 and a diagonal DRAFT watermark on
    /// every page. For proofing on screen or a home printer, never for upload.
    /// </summary>
    public bool Draft { get; init; }

    /// <summary>True to replace an existing file at <see cref="OutputPath"/>.</summary>
    public bool Overwrite { get; init; }

    /// <summary>Optional per-sheet progress.</summary>
    public IProgress<PdfExportProgress>? Progress { get; init; }

    /// <summary>The render target this request implies.</summary>
    public RenderTarget Target => Draft ? RenderTarget.Draft150Dpi : RenderTarget.Export300Dpi;
}

/// <summary>What an export produced.</summary>
/// <param name="OutputPath">The file that was written.</param>
/// <param name="SheetCount">How many PDF pages were emitted.</param>
/// <param name="PageCount">How many book pages were emitted.</param>
/// <param name="Diagnostics">Everything the renderer noticed, in page order — the export log.</param>
/// <param name="Fonts">Which font families actually resolved (doc 10 §3).</param>
/// <param name="Duration">Wall-clock time the export took.</param>
public sealed record PdfExportResult(
    string OutputPath,
    int SheetCount,
    int PageCount,
    IReadOnlyList<RenderDiagnostic> Diagnostics,
    IReadOnlyList<FontResolution> Fonts,
    TimeSpan Duration)
{
    /// <summary>True when no diagnostic was worse than a warning.</summary>
    public bool IsClean => Diagnostics.All(d => d.Severity != RenderSeverity.Error);
}
