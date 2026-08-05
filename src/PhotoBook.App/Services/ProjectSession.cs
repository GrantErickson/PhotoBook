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
    private readonly AutosaveScheduler _autosave;

    private ThumbnailCache? _cache;
    private ThumbnailRenderImageSource? _images;

    /// <param name="thumbnails">The shared thumbnail provider the shell binds to.</param>
    /// <param name="autosaveInterval">
    /// How often a dirty project is written; defaults to the 30 s of doc 04 §6. Tests and diagnostic
    /// builds override it.
    /// </param>
    public ProjectSession(ThumbnailProvider thumbnails, TimeSpan? autosaveInterval = null)
    {
        _thumbnails = thumbnails;
        _autosave = new AutosaveScheduler(SaveSnapshotAsync, autosaveInterval);
        _autosave.StatusChanged += status => JobQueue.PostUi(() =>
        {
            SaveStatusChanged?.Invoke(status);
            Changed?.Invoke();
        });
        _autosave.SaveFailed += ex => JobQueue.PostUi(() => SaveFailed?.Invoke(ex));
    }

    public ProjectStore? Store { get; private set; }

    public ProjectPaths? Paths => Store?.Paths;

    public Book? Book { get; private set; }

    public PhotoCatalog Catalog { get; private set; } = PhotoCatalog.Empty;

    public JournalDocument Journal { get; private set; } = new();

    public List<Chapter> Chapters { get; } = [];

    public bool IsOpen => Book is not null;

    /// <summary>
    /// The print profile the open book names, falling back to the generic one. Everything that needs
    /// page geometry — preview, preflight, export — resolves it here rather than assuming the
    /// generic profile, so the book's <see cref="Book.PrintProfileRef"/> is honoured end to end.
    /// </summary>
    public PrintProfile Profile => BuiltInPrintProfiles.Find(Book?.PrintProfileRef);

    /// <summary>
    /// The trim the open book prints at. A book naming a size its profile does not print falls back
    /// to the profile's first size, which is what keeps a mis-set page size a preflight error rather
    /// than a crash (doc 12).
    /// </summary>
    public PageSizeSpec PageSize
    {
        get
        {
            var profile = Profile;
            return (Book is null ? null : profile.FindPageSize(Book.PageSize)) ?? profile.PageSizes[0];
        }
    }

    /// <summary>True while some edit exists only in memory — including while it is being written.</summary>
    public bool IsDirty => _autosave.HasUnsavedChanges;

    /// <summary>
    /// What the autosave loop is doing, so the shell can say "Saved" / "Saving…" / "Unsaved changes"
    /// honestly rather than assuming a save it never made.
    /// </summary>
    public AutosaveStatus SaveStatus => _autosave.Status;

    /// <summary>How often a dirty project is written (doc 04 §6).</summary>
    public TimeSpan AutosaveInterval => _autosave.Interval;

    /// <summary>Raised on the UI thread whenever <see cref="SaveStatus"/> changes.</summary>
    public event Action<AutosaveStatus>? SaveStatusChanged;

    /// <summary>Raised on the UI thread when an autosave fails; the loop retries on the next tick.</summary>
    public event Action<Exception>? SaveFailed;

    /// <summary>
    /// Raised on the UI thread after an open that had to repair something — a file restored from its
    /// <c>.bak</c>, an interrupted write swept away, a sync conflict copy ignored (doc 04 §6).
    /// </summary>
    public event Action<ProjectRecoveryReport>? Recovered;

    /// <summary>What the last open had to do to get the project open. Never null.</summary>
    public ProjectRecoveryReport LastLoadReport { get; private set; } = ProjectRecoveryReport.Clean;

    public event Action? Changed;

    /// <summary>The image source the renderer samples through. Rebuilt when the catalog changes.</summary>
    public IRenderImageSource Images =>
        _images ?? throw new InvalidOperationException("No project is open.");

    public void MarkDirty()
    {
        _autosave.MarkChanged();
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- open / create

    /// <summary>Creates a new project folder for a year and opens it.</summary>
    public async Task CreateAsync(string folder, int year, string title, CancellationToken ct = default)
    {
        await ProjectStore.CreateNewAsync(folder, year, title, ct: ct).ConfigureAwait(false);
        await OpenAsync(folder, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens an existing project folder (or its book.json), repairing an interrupted write if the
    /// last session died mid-save.
    /// <para>
    /// The sweep and the <c>.bak</c> fallback live in <see cref="ProjectStore"/>; what happens here is
    /// that the result is <em>reported</em> rather than swallowed — through
    /// <see cref="LastLoadReport"/> and the <see cref="Recovered"/> event — and that a project which
    /// had to fall back to a backup is left dirty on purpose, so the very next autosave rewrites the
    /// broken file and re-establishes a fresh <c>.bak</c> beside it (doc 04 §6).
    /// </para>
    /// </summary>
    public async Task OpenAsync(string folderOrBookFile, CancellationToken ct = default)
    {
        Close();

        var store = new ProjectStore(folderOrBookFile);
        store.Paths.EnsureFolders();

        // LoadAsync sweeps stray *.tmp itself and reports what it swept; sweeping here first would
        // delete the evidence and leave the user with no idea the app had crashed mid-write.
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

        LastLoadReport = new ProjectRecoveryReport(loaded.Notices);
        _autosave.Attach();

        if (LastLoadReport.RecoveredAnything)
        {
            MarkDirty();
        }
        else
        {
            Changed?.Invoke();
        }

        if (LastLoadReport.NeedsAttention)
        {
            Recovered?.Invoke(LastLoadReport);
        }
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

    /// <summary>
    /// Closes the project <b>without</b> saving. Prefer <see cref="CloseAsync"/>, which flushes
    /// first; this exists for the paths that have already flushed (or deliberately discard).
    /// </summary>
    public void Close()
    {
        _autosave.Detach();

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
        LastLoadReport = ProjectRecoveryReport.Clean;
        Changed?.Invoke();
    }

    /// <summary>
    /// Closes the book after writing anything still pending, waiting for a save already in flight.
    /// This is the close path the shell should use (doc 04 §6, "app exit" and close-book).
    /// </summary>
    public async Task CloseAsync(CancellationToken ct = default)
    {
        await FlushAsync(ct).ConfigureAwait(false);
        Close();
    }

    // ---------------------------------------------------------------- save

    /// <summary>
    /// An explicit save (Ctrl+S). Serialized against the autosave loop, so a manual save can never
    /// race a tick and half-write a file; a clean project writes nothing. Failures propagate — the
    /// user asked for this one and deserves to hear it did not work.
    /// </summary>
    public Task SaveAsync(CancellationToken ct = default) => _autosave.SaveNowAsync(ct);

    /// <summary>
    /// The autosave tick and the immediate save after a major operation. Never throws — a failure
    /// surfaces through <see cref="SaveFailed"/> and <see cref="SaveStatus"/> and is retried on the
    /// next tick. This is also the hook tests drive instead of waiting 30 seconds.
    /// </summary>
    /// <returns>True when this call actually wrote the project.</returns>
    public Task<bool> AutosaveAsync(CancellationToken ct = default) => _autosave.TriggerAsync(ct);

    /// <summary>Waits for any save in flight, then writes anything still pending. Never throws.</summary>
    public Task<bool> FlushAsync(CancellationToken ct = default) => _autosave.FlushAsync(ct);

    /// <summary>
    /// Asks for a save without waiting for it — for the synchronous call sites (a layout run, the
    /// moment before an export) where a major operation has just finished and blocking the caller
    /// would be worse than being a few hundred milliseconds behind.
    /// </summary>
    public void RequestSave() => _ = Task.Run(() => _autosave.TriggerAsync());

    /// <summary>
    /// The synchronous last chance on app exit (<c>Application.Exit</c>, <c>SessionEnding</c>), where
    /// there is no await to be had. Stops the timer, waits up to <paramref name="timeout"/> for an
    /// in-flight write plus one final one, and never throws. Safe from the UI thread: nothing in the
    /// save path marshals back to the dispatcher.
    /// </summary>
    /// <returns>True when everything reached disk.</returns>
    public bool FlushOnShutdown(TimeSpan? timeout = null) => _autosave.FlushBlocking(timeout);

    /// <summary>Flushes and closes for an orderly shutdown.</summary>
    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        await FlushAsync(ct).ConfigureAwait(false);
        Close();
        _autosave.Dispose();
    }

    /// <summary>
    /// Writes the whole project. Called only by the autosave loop, which guarantees one at a time.
    /// <para>
    /// The snapshot is taken on the calling thread — cheap, and on a UI-initiated save it is taken on
    /// the thread that owns the model, which makes it exact. Serialization and I/O then go to the
    /// thread pool: a megabyte of <c>photos.json</c> must never be serialized on the dispatcher just
    /// because a save happened to be started from it.
    /// </para>
    /// </summary>
    private Task SaveSnapshotAsync(CancellationToken ct)
    {
        var store = Store;
        if (store is null || Book is null)
        {
            return Task.CompletedTask;
        }

        var snapshot = CaptureSnapshot();
        return Task.Run(() => store.SaveAsync(snapshot, ct), ct);
    }

    /// <summary>
    /// Copies the model's collections before serialization starts. Every mutation happens on the UI
    /// thread while the write runs on a worker, so handing the store the live lists would let an edit
    /// landing mid-save throw "collection was modified" — and lose the save. Copying is references
    /// only: a few thousand pointers, microseconds, no dispatcher round-trip to deadlock a shutdown
    /// flush on.
    /// </summary>
    private ProjectSnapshot CaptureSnapshot()
    {
        var book = Book ?? throw new InvalidOperationException("No project is open.");

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new ProjectSnapshot(book, Catalog with { Photos = [.. Catalog.Photos] }, Journal, [.. Chapters]);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException && attempt < 3)
            {
                // An edit landed while the list was being copied. Let it finish and take the copy again.
                Thread.Sleep(2);
            }
        }
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

        // Import is a major operation: a crash after copying thousands of originals must not leave
        // the catalog that describes them only in memory (doc 04 §6).
        await AutosaveAsync(ct).ConfigureAwait(false);
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
        await AutosaveAsync(ct).ConfigureAwait(false);
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

        return OutsideYearNote(Book.Year);
    }

    /// <summary>
    /// How many photos a candidate year would strand in the Outside-book tray, said in the user's
    /// terms, or null when it strands none. The book-settings surface asks this <em>before</em>
    /// committing a year change; the import path asks it afterwards. One wording, one meaning (R6).
    /// </summary>
    public string? OutsideYearNote(int year) => BookYear.OutsideNote(Catalog, year);

    /// <summary>How many non-excluded photos fall outside a candidate year.</summary>
    public int PhotosOutside(int year) => BookYear.PhotosOutside(Catalog, year);

    /// <summary>
    /// Retargets the book at another year, moving its chapters with it and rewriting a default title
    /// like "Family 2026" so nothing keeps showing the year the book no longer covers. Photos are
    /// untouched: those dated outside the new year fall into the Outside-book tray, from which
    /// re-dating — or undoing this — brings them straight back (R6, R17).
    /// </summary>
    /// <returns>The state before the move, which <see cref="RestoreBookYear"/> puts back exactly.</returns>
    public BookYearState SetBookYear(int year)
    {
        if (Book is null)
        {
            throw new InvalidOperationException("No project is open.");
        }

        var before = BookYear.Capture(Book, Chapters);
        BookYear.MoveTo(Book, Chapters, year);
        MarkDirty();
        return before;
    }

    /// <summary>The undo half of <see cref="SetBookYear"/>: year, title and every chapter's year, back as they were.</summary>
    public void RestoreBookYear(BookYearState state)
    {
        if (Book is null)
        {
            return;
        }

        BookYear.Restore(Book, Chapters, state);
        MarkDirty();
    }

    // ---------------------------------------------------------------- book settings

    /// <summary>Renames the book (R3 — one book, one year, one title).</summary>
    public void SetBookTitle(string title)
    {
        if (Book is null)
        {
            return;
        }

        Book.Title = title;
        MarkDirty();
    }

    /// <summary>
    /// Moves the book to another trim (R19). Nothing re-flows: templates are authored per page size,
    /// so the caller is expected to have shown what this strands and to offer the layout run that
    /// rebuilds it — see <see cref="PageSizeChange"/>.
    /// </summary>
    public void SetPageSize(string pageSizeId)
    {
        if (Book is null)
        {
            return;
        }

        Book.PageSize = pageSizeId;
        MarkDirty();
    }

    /// <summary>Points the book at another print profile; export and preflight follow it (doc 12).</summary>
    public void SetPrintProfile(string profileId)
    {
        if (Book is null)
        {
            return;
        }

        Book.PrintProfileRef = profileId;
        MarkDirty();
    }

    /// <summary>
    /// Sets the engine seed — the only source of randomness in layout (kernel §7). Re-rolling it is
    /// how a user asks for a different arrangement of the same photos; it changes nothing until the
    /// layout is actually re-run.
    /// </summary>
    public void SetSeed(ulong seed)
    {
        if (Book is null)
        {
            return;
        }

        Book.Seed = seed;
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
        await AutosaveAsync(ct).ConfigureAwait(false);
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

        // Analysis can run for many minutes over thousands of photos; the tiers and focus regions it
        // produced are user-visible intent and must not depend on a clean exit to survive.
        await AutosaveAsync(ct).ConfigureAwait(false);
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
        var size = PageSize;

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

                // Without these the engine would offer 11 × 8.5 templates and 11 × 8.5 physical
                // metrics to a book printed at some other trim (R19).
                PageSize = Book.PageSize,
                TrimWidthIn = size.TrimWidthIn,
                TrimHeightIn = size.TrimHeightIn,
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

        // A layout run is a major operation (doc 04 §6). Fire-and-forget because this is called from
        // the UI thread mid-gesture and the user must not wait on a disk write to see the pages.
        RequestSave();
    }

    // ---------------------------------------------------------------- render + export

    public Template? FindTemplate(string id) => TemplateLibrary.Default.Find(id);

    /// <summary>Renders one page at the requested pixel size through the same renderer the PDF uses.</summary>
    /// <param name="chapter">The chapter the page belongs to.</param>
    /// <param name="page">The page to draw.</param>
    /// <param name="width">Target width in pixels.</param>
    /// <param name="height">Target height in pixels.</param>
    /// <param name="showFlags">Draw the amber empty-slot flags (R14).</param>
    /// <param name="drawGuides">
    /// Bake trim, safe and gutter guides into the page bitmap — screen only. The page editor passes
    /// <c>false</c> and draws its own guides over the preview instead, so what it shows of the page is
    /// byte-for-byte what the PDF gets; this stays for callers that want a self-contained annotated
    /// raster.
    /// </param>
    public PagePreview RenderPage(
        Chapter chapter, Page page, int width, int height, bool showFlags = true, bool drawGuides = false)
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
                    Profile, PageSize.Id, PageGeometry.PointsPerInch),
                Images = Images,
                Photos = Catalog,
                Journal = Journal,
                Target = RenderTarget.Screen,
                ShowEmptySlotFlags = showFlags,
                DrawGuides = drawGuides,
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
            Profile = Profile,
            Templates = FindTemplate,
            Scope = scope,
        });

    /// <summary>Exports a print-ready PDF (R19, doc 12).</summary>
    public PdfExportResult Export(string outputPath, ExportScope scope, CancellationToken ct = default)
    {
        // Doc 04 §6 lists "before PDF export" as a save point: an export is long, and a crash during
        // one must not cost the edits that produced it.
        RequestSave();

        return new PdfExporter().Export(
            new PdfExportRequest
            {
                Project = Snapshot(),
                Profile = Profile,
                Templates = FindTemplate,
                Images = Images,
                OutputPath = outputPath,
                Scope = scope,
            },
            ct);
    }

    /// <summary>
    /// Last-ditch durability: if the process is being torn down with edits still in memory, write
    /// them before letting go. Callers that can await should use <see cref="ShutdownAsync"/>.
    /// </summary>
    public void Dispose()
    {
        FlushOnShutdown();
        _autosave.Dispose();
        _images?.Dispose();
        _cache?.Dispose();
    }
}
