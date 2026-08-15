using System.Text.Json;
using System.Text.Json.Nodes;
using PhotoBook.Core.Model;

namespace PhotoBook.Core.Persistence;

/// <summary>
/// Reads and writes a project folder (doc 04). Every write is atomic with a rolling <c>.bak</c>
/// (<see cref="AtomicFile"/>); every read sweeps stray temp files, migrates the document forward in
/// memory, and falls back to the backup when the primary file is missing or malformed — opening a
/// project never mutates it.
/// <para>
/// The store is stateless apart from its <see cref="Paths"/>; callers marshal it onto the single
/// writer thread (doc 02) as they do every other project mutation.
/// </para>
/// </summary>
public sealed class ProjectStore
{
    /// <summary>Opens a store over an existing or about-to-exist project folder.</summary>
    /// <param name="paths">The project folder layout.</param>
    public ProjectStore(ProjectPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Paths = paths;
    }

    /// <summary>Opens a store over a project folder or its <c>book.json</c>.</summary>
    /// <param name="folderOrBookFile">The project folder, or the <c>book.json</c> inside it.</param>
    public ProjectStore(string folderOrBookFile)
        : this(ProjectPaths.FromFolderOrBookFile(folderOrBookFile))
    {
    }

    /// <summary>The folder layout this store reads and writes.</summary>
    public ProjectPaths Paths { get; }

    // ---------------------------------------------------------------- create

    /// <summary>
    /// Scaffolds a fresh project folder: the <c>chapters/</c>, <c>originals/</c> and <c>cache/</c>
    /// subfolders of doc 04 §2, a <c>book.json</c> carrying the shipped default style, the generic
    /// print profile reference, a generated engine seed and the pinned PDF timestamp, plus an empty
    /// <c>photos.json</c> and <c>journal.json</c>. No chapter files are created — a month gets a file
    /// only once it has pages.
    /// </summary>
    /// <param name="folderPath">The project folder to create; it must not already contain a <c>book.json</c>.</param>
    /// <param name="year">The calendar year the book covers (R3).</param>
    /// <param name="title">The book's display title.</param>
    /// <param name="seed">Engine seed; a random one is generated when omitted (kernel §7).</param>
    /// <param name="createdAtUtc">Creation instant used for the pinned PDF timestamp; defaults to now (doc 12).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newly written <see cref="Model.Book"/>.</returns>
    public static async Task<Book> CreateNewAsync(
        string folderPath,
        int year,
        string title,
        ulong? seed = null,
        DateTime? createdAtUtc = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var paths = new ProjectPaths(folderPath);
        if (File.Exists(paths.BookFile))
            throw new IOException($"A PhotoBook project already exists at '{paths.Root}'.");

        paths.EnsureFolders();

        var created = (createdAtUtc ?? DateTime.UtcNow).ToUniversalTime();
        created = new DateTime(created.Ticks - created.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

        var book = new Book
        {
            SchemaVersion = ProjectSchema.BookVersion,
            Id = Ids.NewBookId(),
            Title = title,
            Year = year,
            PageSize = PageGeometry.DefaultPageSizeId,
            Style = BuiltInStyles.Default,
            PrintProfileRef = PrintProfile.GenericId,
            Seed = seed ?? NewSeed(),
            PdfTimestampUtc = created,
            Source = new BookSource { Kind = BookSourceKind.LocalFolder },
            Analysis = new AnalysisSettings(),
        };

        var store = new ProjectStore(paths);
        await store.SaveBookAsync(book, ct).ConfigureAwait(false);
        await store.SavePhotosAsync(PhotoCatalog.Empty, ct).ConfigureAwait(false);
        await store.SaveJournalAsync(JournalDocument.Empty, ct).ConfigureAwait(false);
        return book;
    }

    // ------------------------------------------------------------------ load

    /// <summary>Loads the whole project: book, catalog, journal and every chapter file present.</summary>
    public async Task<ProjectLoadResult<ProjectSnapshot>> LoadAsync(CancellationToken ct = default)
    {
        var notices = new List<ProjectLoadNotice>();
        SweepTempFiles(notices);

        var book = await LoadBookAsync(ct).ConfigureAwait(false);
        notices.AddRange(book.Notices);
        var photos = await LoadPhotosAsync(ct).ConfigureAwait(false);
        notices.AddRange(photos.Notices);
        var journal = await LoadJournalAsync(ct).ConfigureAwait(false);
        notices.AddRange(journal.Notices);
        var chapters = await LoadChaptersAsync(ct).ConfigureAwait(false);
        notices.AddRange(chapters.Notices);

        var snapshot = new ProjectSnapshot(book.Value, photos.Value, journal.Value, chapters.Value);
        return new ProjectLoadResult<ProjectSnapshot>(snapshot, Deduplicate(notices));
    }

    /// <summary>Loads <c>book.json</c>. Throws <see cref="FileNotFoundException"/> when the folder is not a project.</summary>
    public Task<ProjectLoadResult<Book>> LoadBookAsync(CancellationToken ct = default) =>
        LoadDocumentAsync<Book>(Paths.BookFile, ProjectFileKind.Book, required: true, fallback: null, validate: null, ct);

    /// <summary>Loads <c>photos.json</c>; a missing file yields an empty catalog.</summary>
    public Task<ProjectLoadResult<PhotoCatalog>> LoadPhotosAsync(CancellationToken ct = default) =>
        LoadDocumentAsync(Paths.PhotosFile, ProjectFileKind.Photos, required: false, fallback: PhotoCatalog.Empty, validate: null, ct);

    /// <summary>Loads <c>journal.json</c>; a missing file yields an empty journal (R2 is optional).</summary>
    public Task<ProjectLoadResult<JournalDocument>> LoadJournalAsync(CancellationToken ct = default) =>
        LoadDocumentAsync(Paths.JournalFile, ProjectFileKind.Journal, required: false, fallback: JournalDocument.Empty, validate: null, ct);

    /// <summary>
    /// Loads one chapter file. A missing month yields an empty chapter; a file whose
    /// <c>year</c>/<c>month</c> disagree with its name is a load error (doc 04 §2).
    /// </summary>
    public Task<ProjectLoadResult<Chapter>> LoadChapterAsync(int year, int month, CancellationToken ct = default)
    {
        var path = Paths.ChapterFile(year, month);
        var empty = new Chapter { SchemaVersion = ProjectSchema.ChapterVersion, Year = year, Month = month };
        return LoadDocumentAsync(path, ProjectFileKind.Chapter, required: false, fallback: empty, validate: chapter =>
        {
            if (chapter.Year != year || chapter.Month != month)
                throw new ProjectFormatException(
                    Path.GetFileName(path),
                    $"the file declares {chapter.Year}-{chapter.Month:00} but is named for {year}-{month:00}.");
        }, ct);
    }

    /// <summary>Loads every chapter file present on disk, ordered by month.</summary>
    public async Task<ProjectLoadResult<IReadOnlyList<Chapter>>> LoadChaptersAsync(CancellationToken ct = default)
    {
        var notices = new List<ProjectLoadNotice>();
        var chapters = new List<Chapter>();

        if (Directory.Exists(Paths.ChaptersFolder))
        {
            foreach (var file in Directory.EnumerateFiles(Paths.ChaptersFolder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                if (ProjectPaths.IsTransient(file)) continue;
                if (ProjectPaths.IsSyncConflictCopy(file))
                {
                    notices.Add(new ProjectLoadNotice(ProjectLoadNoticeKind.IgnoredConflictCopy, Path.GetFileName(file),
                        $"Ignored '{Path.GetFileName(file)}' — it looks like a sync conflict copy, not project content."));
                    continue;
                }

                if (!ProjectPaths.TryParseChapterFileName(file, out var year, out var month))
                    throw new ProjectFormatException(Path.GetFileName(file), "chapter files must be named {year}-{month:00}.json.");

                var result = await LoadChapterAsync(year, month, ct).ConfigureAwait(false);
                notices.AddRange(result.Notices);
                chapters.Add(result.Value);
            }
        }

        return new ProjectLoadResult<IReadOnlyList<Chapter>>(
            chapters.OrderBy(c => c.Year).ThenBy(c => c.Month).ToList(),
            notices);
    }

    // ------------------------------------------------------------------ save

    /// <summary>Writes <c>book.json</c> atomically.</summary>
    public Task SaveBookAsync(Book book, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        book.SchemaVersion = ProjectSchema.BookVersion;
        return WriteAsync(Paths.BookFile, book, ct);
    }

    /// <summary>
    /// Writes <c>photos.json</c> atomically, with photos sorted by id and each photo's focus regions
    /// sorted by kind then weight, so two saves of the same model are byte-identical (doc 04 §4).
    /// </summary>
    public Task SavePhotosAsync(PhotoCatalog catalog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        catalog.SchemaVersion = ProjectSchema.PhotosVersion;
        var canonical = catalog with
        {
            Photos = catalog.Photos
                .OrderBy(p => p.Id, StringComparer.Ordinal)
                .Select(p => p with
                {
                    FocusRegions = p.FocusRegions
                        .OrderBy(r => r.KindPriority)
                        .ThenByDescending(r => r.Weight)
                        .ToList(),
                })
                .ToList(),
        };

        return WriteAsync(Paths.PhotosFile, canonical, ct);
    }

    /// <summary>Writes <c>journal.json</c> atomically, with entries sorted by effective date then id.</summary>
    public Task SaveJournalAsync(JournalDocument journal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        journal.SchemaVersion = ProjectSchema.JournalVersion;
        var canonical = journal with
        {
            Entries = journal.Entries
                .OrderBy(e => e.EffectiveDate)
                .ThenBy(e => e.Id, StringComparer.Ordinal)
                .ToList(),
        };

        return WriteAsync(Paths.JournalFile, canonical, ct);
    }

    /// <summary>
    /// Writes one chapter file atomically. A chapter with no pages has no file (doc 04 §2), so
    /// saving an empty chapter deletes it.
    /// </summary>
    public Task SaveChapterAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        chapter.SchemaVersion = ProjectSchema.ChapterVersion;
        var path = Paths.ChapterFile(chapter.Year, chapter.Month);
        if (chapter.Pages.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return Task.CompletedTask;
        }

        Directory.CreateDirectory(Paths.ChaptersFolder);
        return WriteAsync(path, chapter, ct);
    }

    /// <summary>Writes every document of a snapshot atomically.</summary>
    public async Task SaveAsync(ProjectSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await SaveBookAsync(snapshot.Book, ct).ConfigureAwait(false);
        await SavePhotosAsync(snapshot.Photos, ct).ConfigureAwait(false);
        await SaveJournalAsync(snapshot.Journal, ct).ConfigureAwait(false);
        foreach (var chapter in snapshot.Chapters)
        {
            await SaveChapterAsync(chapter, ct).ConfigureAwait(false);
        }
    }

    // --------------------------------------------------------------- helpers

    /// <summary>
    /// Deletes stray <c>*.tmp</c> files in the project root and <c>chapters/</c> — by definition
    /// incomplete writes (doc 04 §6). Called automatically by <see cref="LoadAsync"/>.
    /// </summary>
    public IReadOnlyList<ProjectLoadNotice> SweepTempFiles()
    {
        var notices = new List<ProjectLoadNotice>();
        SweepTempFiles(notices);
        return notices;
    }

    private void SweepTempFiles(List<ProjectLoadNotice> notices)
    {
        foreach (var deleted in AtomicFile.DeleteStrayTempFiles(Paths.Root)
                     .Concat(AtomicFile.DeleteStrayTempFiles(Paths.ChaptersFolder)))
        {
            notices.Add(new ProjectLoadNotice(ProjectLoadNoticeKind.DeletedStrayTemp, Path.GetFileName(deleted),
                $"Discarded '{Path.GetFileName(deleted)}' — an incomplete write left over from a previous run."));
        }
    }

    private static Task WriteAsync<T>(string path, T model, CancellationToken ct) =>
        AtomicFile.WriteAllTextAsync(path, ProjectJson.Serialize(model) + Environment.NewLine, ct);

    private static async Task<ProjectLoadResult<T>> LoadDocumentAsync<T>(
        string path,
        ProjectFileKind kind,
        bool required,
        T? fallback,
        Action<T>? validate,
        CancellationToken ct)
        where T : class
    {
        var notices = new List<ProjectLoadNotice>();
        var fileName = Path.GetFileName(path);

        // A .tmp is by definition an incomplete write; discard it before reading (doc 04 §6).
        var temp = ProjectPaths.TempPathFor(path);
        if (File.Exists(temp))
        {
            try
            {
                File.Delete(temp);
                notices.Add(new ProjectLoadNotice(ProjectLoadNoticeKind.DeletedStrayTemp, Path.GetFileName(temp),
                    $"Discarded '{Path.GetFileName(temp)}' — an incomplete write left over from a previous run."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still held open elsewhere; it is not ours to clean up.
            }
        }

        if (File.Exists(path))
        {
            try
            {
                var value = await ParseAsync<T>(path, kind, ct).ConfigureAwait(false);
                validate?.Invoke(value);
                return new ProjectLoadResult<T>(value, notices);
            }
            catch (ProjectSchemaTooNewException)
            {
                throw; // never guess forward, and never silently fall back to an older backup
            }
            catch (Exception ex) when (ex is JsonException or ProjectFormatException or IOException)
            {
                var recovered = await TryRecoverAsync<T>(path, kind, validate, notices, ex, ct).ConfigureAwait(false);
                if (recovered is not null) return new ProjectLoadResult<T>(recovered, notices);
                throw;
            }
        }

        // Missing primary file: the backup may still hold the last good save.
        if (File.Exists(ProjectPaths.BackupPathFor(path)))
        {
            var recovered = await TryRecoverAsync<T>(path, kind, validate, notices, null, ct).ConfigureAwait(false);
            if (recovered is not null) return new ProjectLoadResult<T>(recovered, notices);
        }

        if (required || fallback is null)
            throw new FileNotFoundException($"'{fileName}' is missing — '{Path.GetDirectoryName(path)}' is not a PhotoBook project.", path);

        notices.Add(new ProjectLoadNotice(ProjectLoadNoticeKind.MissingFileDefaulted, fileName,
            $"'{fileName}' was not found; starting from an empty document."));
        return new ProjectLoadResult<T>(fallback, notices);
    }

    private static async Task<T?> TryRecoverAsync<T>(
        string path,
        ProjectFileKind kind,
        Action<T>? validate,
        List<ProjectLoadNotice> notices,
        Exception? cause,
        CancellationToken ct)
        where T : class
    {
        var backup = ProjectPaths.BackupPathFor(path);
        if (!File.Exists(backup)) return null;

        try
        {
            var value = await ParseAsync<T>(backup, kind, ct).ConfigureAwait(false);
            validate?.Invoke(value);
            var lastGood = File.GetLastWriteTime(backup);
            notices.Add(new ProjectLoadNotice(ProjectLoadNoticeKind.RecoveredFromBackup, Path.GetFileName(path),
                $"Recovered {Path.GetFileName(path)} from backup (last good save: {lastGood:g})." +
                (cause is null ? string.Empty : $" The file could not be read: {cause.Message}")));
            return value;
        }
        catch (Exception ex) when (ex is JsonException or ProjectFormatException or IOException)
        {
            return null; // the backup is broken too — let the caller report the original failure
        }
    }

    private static async Task<T> ParseAsync<T>(string path, ProjectFileKind kind, CancellationToken ct)
        where T : class
    {
        var text = await AtomicFile.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var fileName = Path.GetFileName(path);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text, nodeOptions: null, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            throw new ProjectFormatException(fileName, $"invalid JSON at byte {ex.BytePositionInLine?.ToString() ?? "?"} of line {ex.LineNumber?.ToString() ?? "?"}.", ex);
        }

        if (node is not JsonObject root)
            throw new ProjectFormatException(fileName, "the root of a project file must be a JSON object.");

        root = ProjectSchema.Migrate(root, kind, fileName);

        var value = root.Deserialize<T>(ProjectJson.Options);
        if (value is null) throw new ProjectFormatException(fileName, "the document deserialized to null.");
        return value;
    }

    private static IReadOnlyList<ProjectLoadNotice> Deduplicate(IEnumerable<ProjectLoadNotice> notices) =>
        notices.DistinctBy(n => (n.Kind, n.File, n.Message)).ToList();

    /// <summary>
    /// A fresh engine seed. The seed is the only source of randomness in the layout engine
    /// (kernel §7), so re-rolling it is how a user asks for a different arrangement of the same
    /// photos — the shuffle in book settings and a brand-new book draw from the same generator.
    /// </summary>
    public static ulong NewSeed()
    {
        Span<byte> bytes = stackalloc byte[8];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt64(bytes);
    }
}
