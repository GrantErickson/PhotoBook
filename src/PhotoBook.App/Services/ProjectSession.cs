using System.IO;
using PhotoBook.Analysis.Composition;
using PhotoBook.Analysis.Running;
using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;
using PhotoBook.Core.Persistence;
using PhotoBook.Core.Templates;
using PhotoBook.Engine;
using PhotoBook.Imaging;
using PhotoBook.Ingestion;
using PhotoBook.Rendering;

namespace PhotoBook.App.Services;

/// <summary>
/// Owns the open project and is the only place its model is mutated (kernel §8, single writer).
/// View models read through it and ask it to act; nothing else writes to disk.
/// </summary>
public sealed class ProjectSession : IDisposable
{
    private readonly ThumbnailProvider _thumbnails;
    private readonly SkiaTextMeasurer _measurer = new();

    private ThumbnailCache? _cache;
    private ThumbnailRenderImageSource? _images;

    public ProjectSession(ThumbnailProvider thumbnails) => _thumbnails = thumbnails;

    public ProjectStore? Store { get; private set; }

    public ProjectPaths? Paths => Store?.Paths;

    public Book? Book { get; private set; }

    public PhotoCatalog Catalog { get; private set; } = PhotoCatalog.Empty;

    public JournalDocument Journal { get; private set; } = new();

    public List<Chapter> Chapters { get; } = [];

    public bool IsOpen => Book is not null;

    /// <summary>Set whenever the model changes so autosave and the title bar know.</summary>
    public bool IsDirty { get; private set; }

    public event Action? Changed;

    /// <summary>The image source the renderer samples through. Rebuilt when the catalog changes.</summary>
    public IRenderImageSource Images =>
        _images ?? throw new InvalidOperationException("No project is open.");

    public void MarkDirty()
    {
        IsDirty = true;
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- open / create

    /// <summary>Creates a new project folder for a year and opens it.</summary>
    public async Task CreateAsync(string folder, int year, string title, CancellationToken ct = default)
    {
        await ProjectStore.CreateNewAsync(folder, year, title, ct: ct).ConfigureAwait(false);
        await OpenAsync(folder, ct).ConfigureAwait(false);
    }

    /// <summary>Opens an existing project folder (or its book.json).</summary>
    public async Task OpenAsync(string folderOrBookFile, CancellationToken ct = default)
    {
        Close();

        var store = new ProjectStore(folderOrBookFile);
        store.Paths.EnsureFolders();
        store.SweepTempFiles();

        var loaded = await store.LoadAsync(ct).ConfigureAwait(false);
        var snapshot = loaded.Value;

        Store = store;
        Book = snapshot.Book;
        Catalog = snapshot.Photos;
        Journal = snapshot.Journal;
        Chapters.Clear();
        Chapters.AddRange(snapshot.Chapters);

        EnsureChapters();
        RebuildImagePipeline();
        IsDirty = false;
        Changed?.Invoke();
    }

    /// <summary>Every month of the year exists as a chapter, so the rail is never ragged (R3).</summary>
    private void EnsureChapters()
    {
        if (Book is null)
        {
            return;
        }

        for (var month = 1; month <= 12; month++)
        {
            if (Chapters.All(c => c.Month != month))
            {
                Chapters.Add(new Chapter { Year = Book.Year, Month = month });
            }
        }

        Chapters.Sort((a, b) => a.Month.CompareTo(b.Month));
    }

    private void RebuildImagePipeline()
    {
        if (Store is null)
        {
            return;
        }

        _images?.Dispose();
        _cache?.Dispose();

        _cache = new ThumbnailCache(Store.Paths, ThumbnailSources.FromCatalog(Store.Paths, () => Catalog));
        _images = new ThumbnailRenderImageSource(Store.Paths, Catalog, _cache);
        _thumbnails.Attach(_cache);
    }

    public void Close()
    {
        _images?.Dispose();
        _cache?.Dispose();
        _images = null;
        _cache = null;
        _thumbnails.Attach(null);

        Store = null;
        Book = null;
        Catalog = PhotoCatalog.Empty;
        Journal = new JournalDocument();
        Chapters.Clear();
        IsDirty = false;
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- save

    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (Store is null || Book is null)
        {
            return;
        }

        await Store.SaveBookAsync(Book, ct).ConfigureAwait(false);
        await Store.SavePhotosAsync(Catalog, ct).ConfigureAwait(false);
        await Store.SaveJournalAsync(Journal, ct).ConfigureAwait(false);
        foreach (var chapter in Chapters)
        {
            await Store.SaveChapterAsync(chapter, ct).ConfigureAwait(false);
        }

        IsDirty = false;
    }

    // ---------------------------------------------------------------- import

    /// <summary>Imports a folder of photos, copying originals into the project (R1).</summary>
    public async Task<PhotoImportReport> ImportFolderAsync(
        string folder, IProgress<PhotoImportProgress>? progress, CancellationToken ct = default)
    {
        if (Store is null)
        {
            throw new InvalidOperationException("No project is open.");
        }

        var importer = new PhotoImporter(Store.Paths);
        var source = new FolderPhotoSource(folder);
        var report = await importer.ImportAsync(source, Catalog, null, progress, ct).ConfigureAwait(false);

        RebuildImagePipeline();
        MarkDirty();
        return report;
    }

    /// <summary>
    /// Imports (or re-syncs) photos from a OneDrive album or folder, copying originals into the
    /// project exactly as a folder import does. The chosen source is recorded on the book so a later
    /// "Sync now" needs no picker, and so re-syncing never resurrects an excluded photo (R17).
    /// </summary>
    public async Task<PhotoImportReport> ImportOneDriveAsync(
        PhotoBook.Ingestion.OneDrive.IOneDriveClient client,
        BookSource source,
        IProgress<PhotoImportProgress>? progress,
        CancellationToken ct = default)
    {
        if (Store is null || Book is null)
        {
            throw new InvalidOperationException("No project is open.");
        }

        var importer = new PhotoImporter(Store.Paths);
        var photoSource = OneDrivePhotoSource.ForBookSource(client, source);
        var report = await importer.ImportAsync(photoSource, Catalog, null, progress, ct).ConfigureAwait(false);

        Book.Source = source;
        RebuildImagePipeline();
        MarkDirty();
        return report;
    }

    /// <summary>Non-excluded photos currently in the catalog, whatever year they fall in.</summary>
    public int PhotoCount => Catalog.Photos.Count(p => !p.Excluded);

    /// <summary>
    /// Keeps a book's year and its photos from silently disagreeing. A book covers exactly one year
    /// (R3), so a photo outside it lands in the Outside-book tray — correct, but invisible: import a
    /// 2022 album into a book that defaulted to this year and every month reads zero with no
    /// explanation.
    /// <para>
    /// An <b>empty</b> book therefore adopts the year its first photos actually come from, which is
    /// almost always what was meant. A book that already has photos is left alone and the caller is
    /// told how many fell outside, because retargeting a populated book would move everything.
    /// </para>
    /// </summary>
    /// <param name="photosBeforeImport">Catalog count before the import that just ran.</param>
    /// <returns>A message worth showing the user, or null when nothing needs saying.</returns>
    public string? ReconcileYearAfterImport(int photosBeforeImport)
    {
        if (Book is null)
        {
            return null;
        }

        var photos = Catalog.Photos.Where(p => !p.Excluded).ToList();
        if (photos.Count == 0)
        {
            return null;
        }

        var dominant = photos
            .GroupBy(p => p.TakenAt.Year)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .First();

        if (photosBeforeImport == 0 && dominant.Key != Book.Year)
        {
            var previous = Book.Year;
            SetBookYear(dominant.Key);

            var outside = photos.Count - dominant.Count();
            var note = $"These photos are from {dominant.Key}, so this book is now “{Book.Title}” " +
                       $"covering {dominant.Key} rather than {previous}.";
            return outside == 0
                ? note
                : note + $" {outside} photo{(outside == 1 ? "" : "s")} from other years " +
                         "will sit in the Outside-book tray.";
        }

        var strays = photos.Count(p => p.TakenAt.Year != Book.Year);
        return strays == 0
            ? null
            : $"{strays} photo{(strays == 1 ? " is" : "s are")} outside {Book.Year} and will not appear " +
              $"in any month. Change the photo's date, or make a book for that year.";
    }

    /// <summary>
    /// Retargets an empty book at another year, moving its (empty) chapters with it and rewriting a
    /// default title like "Family 2026" so the shell does not keep showing the year it is no longer.
    /// </summary>
    private void SetBookYear(int year)
    {
        if (Book is null)
        {
            return;
        }

        var previous = Book.Year.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Book.Year = year;

        foreach (var chapter in Chapters)
        {
            chapter.Year = year;
        }

        if (Book.Title.Contains(previous, StringComparison.Ordinal))
        {
            Book.Title = Book.Title.Replace(
                previous, year.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        MarkDirty();
    }

    /// <summary>The OneDrive source this book syncs from, or null when it is a local-folder book.</summary>
    public BookSource? OneDriveSource =>
        Book?.Source is { Kind: BookSourceKind.OneDriveAlbum or BookSourceKind.OneDriveFolder } source
            ? source
            : null;

    /// <summary>Parses a Word journal and merges its entries (R2).</summary>
    public async Task<JournalImportReport> ImportJournalAsync(string docxPath, CancellationToken ct = default)
    {
        if (Book is null)
        {
            throw new InvalidOperationException("No project is open.");
        }

        var parser = new PhotoBook.Ingestion.Journal.JournalParser();
        await using var stream = File.OpenRead(docxPath);
        var request = new JournalParseRequest(Path.GetFileName(docxPath), Book.Year, Journal);

        Journal = await parser.ParseAsync(stream, request, ct).ConfigureAwait(false);
        MarkDirty();
        return Journal.ImportReport;
    }

    // ---------------------------------------------------------------- analysis

    /// <summary>Runs image analysis over the photos that still need it (R25, R26).</summary>
    public async Task<AnalysisRunResult> AnalyzeAsync(
        IReadOnlyList<Photo> photos, IProgress<AnalysisProgress>? progress, CancellationToken ct = default)
    {
        if (Store is null || Book is null || _cache is null)
        {
            throw new InvalidOperationException("No project is open.");
        }

        using var selection = AnalyzerFactory.CreateFor(Book);
        var cache = _cache;
        var locator = new DelegateAnalysisCopyLocator((photo, token) =>
            cache.GetOrCreateAsync(photo.ContentHash, ThumbnailTier.Analysis1024, token));

        var runner = new AnalysisRunner(selection.Analyzer, locator);
        var result = await runner
            .RunAsync(photos, new AnalysisRunOptions { TierPool = photos }, progress, ct)
            .ConfigureAwait(false);

        MarkDirty();
        return result;
    }

    // ---------------------------------------------------------------- layout

    /// <summary>
    /// Lays out a chapter. The Skia measurer is injected deliberately: with the engine's headless
    /// default the estimated text capacity disagrees with real font metrics and journal text is
    /// clipped mid-word on the rendered page.
    /// </summary>
    public LayoutResult Layout(int month, LayoutScope? scope = null)
    {
        if (Book is null)
        {
            throw new InvalidOperationException("No project is open.");
        }

        var chapter = Chapters.First(c => c.Month == month);
        var photos = Catalog.InChapter(Book.Year, month).Where(p => !p.Excluded).ToList();

        return LayoutEngine.LayoutChapter(new LayoutRequest
        {
            Chapter = new ChapterInput
            {
                Year = Book.Year,
                Month = month,
                Title = chapter.Title,
                Photos = photos,
                JournalEntries = [.. Journal.EntriesIn(Book.Year, month)],
                ExistingPages = [.. chapter.Pages],
            },
            Style = StyleResolver.Resolve(Book, chapter, null),
            Seed = Book.Seed,
            Scope = scope ?? LayoutScope.WholeChapter,
            TextMeasurer = _measurer,
        });
    }

    /// <summary>Applies a layout result to the chapter, replacing its pages.</summary>
    public void ApplyLayout(int month, LayoutResult result)
    {
        var chapter = Chapters.First(c => c.Month == month);
        chapter.Pages = [.. result.Pages];
        MarkDirty();
    }

    // ---------------------------------------------------------------- render + export

    public Template? FindTemplate(string id) => TemplateLibrary.Default.Find(id);

    /// <summary>Renders one page at the requested pixel size through the same renderer the PDF uses.</summary>
    public PagePreview RenderPage(Chapter chapter, Page page, int width, int height, bool showFlags = true)
    {
        if (Book is null)
        {
            throw new InvalidOperationException("No project is open.");
        }

        var template = page.ResolveTemplate(FindTemplate)
                       ?? throw new InvalidOperationException($"Unresolved template '{page.TemplateRef}'.");

        return PagePreviewRenderer.Render(
            new PageRenderRequest
            {
                Book = Book,
                Chapter = chapter,
                Page = page,
                Template = template,
                Geometry = PageGeometryMapper.Create(
                    BuiltInPrintProfiles.Generic, Book.PageSize, PageGeometry.PointsPerInch),
                Images = Images,
                Photos = Catalog,
                Journal = Journal,
                Target = RenderTarget.Screen,
                ShowEmptySlotFlags = showFlags,
            },
            width,
            height,
            SkiaSharp.SKColors.Black);
    }

    public ProjectSnapshot Snapshot() =>
        new(Book ?? throw new InvalidOperationException("No project is open."), Catalog, Journal, [.. Chapters]);

    /// <summary>Runs the kernel §11 preflight gate for an export scope.</summary>
    public PreflightReport Preflight(ExportScope scope) =>
        new PreflightChecker().Check(new PreflightRequest
        {
            Project = Snapshot(),
            Profile = BuiltInPrintProfiles.Generic,
            Templates = FindTemplate,
            Scope = scope,
        });

    /// <summary>Exports a print-ready PDF (R19, doc 12).</summary>
    public PdfExportResult Export(string outputPath, ExportScope scope, CancellationToken ct = default) =>
        new PdfExporter().Export(
            new PdfExportRequest
            {
                Project = Snapshot(),
                Profile = BuiltInPrintProfiles.Generic,
                Templates = FindTemplate,
                Images = Images,
                OutputPath = outputPath,
                Scope = scope,
            },
            ct);

    public void Dispose()
    {
        _images?.Dispose();
        _cache?.Dispose();
    }
}
