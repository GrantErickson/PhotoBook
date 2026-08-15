using System.Diagnostics;
using PhotoBook.Core.Model;
using PhotoBook.Core.Persistence;
using SkiaSharp;

namespace PhotoBook.Rendering;

/// <summary>
/// The print-ready PDF pipeline of doc 12: an <c>SKDocument.CreatePdf</c> page loop driving the very
/// same <see cref="PageRenderer"/> the editor draws with (ADR-0003, ADR-0006), so what the user
/// proofs is what prints.
/// <para>
/// Determinism is a feature, not an accident. Same project folder plus same app version plus same
/// profile ⇒ byte-identical PDF (kernel §11), because: document timestamps are pinned to
/// <see cref="Book.PdfTimestampUtc"/> and no wall-clock value enters the file; pages iterate by book
/// page number and slots by template declaration order; the renderer introduces no randomness; and
/// the file is written temp-then-rename so a crashed export never leaves a plausible truncated PDF
/// behind.
/// </para>
/// <para>
/// Two limitations, stated honestly. Skia's PDF backend writes a media box only — it cannot emit
/// <c>/TrimBox</c> and <c>/BleedBox</c> entries, so the trim is communicated the way doc 12
/// prescribes: the media box <em>is</em> the bleed box, content is drawn in trim space inset by the
/// bleed, and setting <see cref="PrintProfile.IncludeTrimMarks"/> adds a 0.25 in slug with printed
/// crop marks. It likewise cannot write a PDF/X OutputIntent, so <c>colorIntent</c> is enforced by
/// converting pixels to sRGB rather than by tagging the document.
/// </para>
/// </summary>
public sealed class PdfExporter
{
    /// <summary>The producer string written into every PDF.</summary>
    public const string Producer = "PhotoBook";

    /// <summary>Length of the crop marks drawn in the slug, in inches.</summary>
    public const double CropMarkLengthIn = 0.125;

    /// <summary>Exports a PDF and returns the log. Blocking; run it on the background job queue.</summary>
    /// <param name="request">What to export and where.</param>
    /// <param name="ct">Cancellation; a cancelled export leaves no partial file behind.</param>
    public PdfExportResult Export(PdfExportRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);

        var stopwatch = Stopwatch.StartNew();
        var project = request.Project;
        var book = project.Book;
        var profile = request.Profile;

        if (profile.FindPageSize(book.PageSize) is null)
        {
            throw new InvalidOperationException(
                $"Print profile '{profile.Id}' does not support the book's page size '{book.PageSize}'. " +
                "Preflight reports this as an error before export is offered.");
        }

        var allPages = BookPagination.Paginate(project);
        var sheets = BookPagination.Sheets(allPages, request.Scope, profile.Output);
        if (sheets.Count == 0)
            throw new InvalidOperationException($"Export scope {request.Scope} selects no pages.");

        var directory = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var finalPath = Path.GetFullPath(request.OutputPath);
        var tempPath = ProjectPaths.TempPathFor(finalPath);
        var diagnostics = new List<RenderDiagnostic>();
        var pageCount = 0;

        try
        {
            using (var stream = File.Create(tempPath))
            {
                using var document = SKDocument.CreatePdf(stream, Metadata(book, request));
                for (var i = 0; i < sheets.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var sheet = sheets[i];
                    pageCount += RenderSheet(document, sheet, request, allPages, diagnostics);
                    request.Progress?.Report(new PdfExportProgress(i, sheets.Count, sheet.Left.PageNumber));
                }

                document.Close();
            }

            if (request.Overwrite && File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(tempPath, finalPath, overwrite: false);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        stopwatch.Stop();
        return new PdfExportResult(
            finalPath, sheets.Count, pageCount, diagnostics, request.Fonts.Resolutions, stopwatch.Elapsed);
    }

    /// <summary>
    /// The pinned metadata behind the byte-stable guarantee. <see cref="Book.PdfTimestampUtc"/> is
    /// written once when the book is created and never touched again, so no wall-clock value ever
    /// enters the file (doc 12 "Deterministic, byte-stable output").
    /// </summary>
    public static SKDocumentPdfMetadata Metadata(Book book, PdfExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(request);

        var pinned = book.PdfTimestampUtc == default
            ? new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            : DateTime.SpecifyKind(book.PdfTimestampUtc, DateTimeKind.Utc);

        return new SKDocumentPdfMetadata
        {
            Title = string.IsNullOrWhiteSpace(book.Title) ? $"PhotoBook {book.Year}" : book.Title,
            Author = string.Empty,
            Subject = string.Empty,
            Keywords = string.Empty,
            Creator = Producer,
            Producer = Producer,
            Creation = pinned,
            Modified = pinned,
            RasterDpi = request.Target.RasterDpi(),
            EncodingQuality = request.Draft ? request.Target.JpegQuality() : request.Profile.JpegQuality,
            PdfA = false,
        };
    }

    private static int RenderSheet(
        SKDocument document,
        ExportSheet sheet,
        PdfExportRequest request,
        IReadOnlyList<BookPage> allPages,
        List<RenderDiagnostic> diagnostics)
    {
        var book = request.Project.Book;
        var geometry = PageGeometryMapper.Create(
            request.Profile, book.PageSize, PageGeometry.PointsPerInch, sheet.Surface);

        var canvas = document.BeginPage(geometry.PaperRect.Width, geometry.PaperRect.Height);
        try
        {
            var pages = 0;
            foreach (var (bookPage, half) in Halves(sheet))
            {
                var template = ResolveTemplate(bookPage.Page, request.Templates);
                if (template is null)
                {
                    diagnostics.Add(new RenderDiagnostic(
                        RenderDiagnosticKind.MissingPhoto, RenderSeverity.Error,
                        $"Page {bookPage.PageNumber} references template '{bookPage.Page.TemplateRef}', which is not in the library.",
                        bookPage.PageNumber, bookPage.Page.Id));
                    continue;
                }

                var (facingPage, facingTemplate, side) = Facing(sheet, bookPage, allPages, request.Templates);
                var pageRequest = new PageRenderRequest
                {
                    Book = book,
                    Chapter = bookPage.Chapter,
                    Page = bookPage.Page,
                    Template = template,
                    Geometry = geometry,
                    Images = request.Images,
                    Photos = request.Project.Photos,
                    Journal = request.Project.Journal,
                    Fonts = request.Fonts,
                    Target = request.Target,
                    Half = half,
                    Side = side,
                    FacingPage = facingPage,
                    FacingTemplate = facingTemplate,
                    BookPageNumber = bookPage.PageNumber,
                    ShowEmptySlotFlags = false,
                };

                var result = PageRenderer.Render(canvas, pageRequest);
                diagnostics.AddRange(result.Diagnostics);
                pages++;
            }

            if (request.Profile.IncludeTrimMarks) DrawCropMarks(canvas, geometry);
            return pages;
        }
        finally
        {
            document.EndPage();
        }
    }

    private static IEnumerable<(BookPage Page, PageHalf Half)> Halves(ExportSheet sheet)
    {
        if (sheet.Surface == PageSurface.SinglePage || sheet.Right is null)
        {
            yield return (sheet.Left, PageHalf.Full);
            yield break;
        }

        yield return (sheet.Left, PageHalf.Left);
        yield return (sheet.Right, PageHalf.Right);
    }

    /// <summary>
    /// The facing page of a spread, needed to resolve gutter-spanning photos (R18). On a single-page
    /// sheet the facing page still matters — the shared crop runs across both halves — so it is
    /// looked up from the book sequence rather than from the sheet.
    /// </summary>
    private static (Page? FacingPage, Template? FacingTemplate, PairSide Side) Facing(
        ExportSheet sheet, BookPage current, IReadOnlyList<BookPage> allPages, Func<string, Template?> templates)
    {
        // Book page numbers pair (2,3), (4,5), …: an even page is a left page (verso), and page 1 —
        // the opening recto — has no facing page at all.
        var side = current.PageNumber > 1 && current.PageNumber % 2 == 0 ? PairSide.Left : PairSide.Right;
        var facingNumber = side == PairSide.Left ? current.PageNumber + 1 : current.PageNumber - 1;

        BookPage? facing = null;
        if (sheet.Right is not null)
            facing = ReferenceEquals(sheet.Left, current) ? sheet.Right : sheet.Left;
        facing ??= allPages.FirstOrDefault(p => p.PageNumber == facingNumber);

        if (facing is null || ReferenceEquals(facing, current)) return (null, null, side);
        return (facing.Page, ResolveTemplate(facing.Page, templates), side);
    }

    private static Template? ResolveTemplate(Page page, Func<string, Template?> templates) =>
        page.ResolveTemplate(id => templates(id));

    /// <summary>
    /// Crop marks in the slug, at the four trim corners — what
    /// <see cref="PrintProfile.IncludeTrimMarks"/> buys. They live outside the bleed box, so nothing
    /// they touch survives trimming.
    /// </summary>
    private static void DrawCropMarks(SKCanvas canvas, PageGeometryMapper geometry)
    {
        if (geometry.SlugIn <= 0) return;

        using var paint = new SKPaint
        {
            Color = SKColors.Black,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0.25f,
            IsAntialias = false,
        };

        var length = geometry.Inches(CropMarkLengthIn);
        var gap = geometry.Inches(geometry.BleedIn);
        var trim = geometry.TrimRect;

        foreach (var (x, y, dx, dy) in new (float, float, float, float)[]
        {
            (trim.Left, trim.Top, -1, 0), (trim.Left, trim.Top, 0, -1),
            (trim.Right, trim.Top, 1, 0), (trim.Right, trim.Top, 0, -1),
            (trim.Left, trim.Bottom, -1, 0), (trim.Left, trim.Bottom, 0, 1),
            (trim.Right, trim.Bottom, 1, 0), (trim.Right, trim.Bottom, 0, 1),
        })
        {
            var startX = x + dx * gap;
            var startY = y + dy * gap;
            canvas.DrawLine(startX, startY, startX + dx * length, startY + dy * length, paint);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp file we cannot remove is untidy, not incorrect.
        }
    }
}
