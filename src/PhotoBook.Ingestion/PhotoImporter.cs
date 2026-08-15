using System.Diagnostics;
using System.Security.Cryptography;
using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;
using PhotoBook.Core.Persistence;
using PhotoBook.Ingestion.Probing;

namespace PhotoBook.Ingestion;

/// <summary>
/// Turns a <see cref="IPhotoSource"/> into the two durable outputs of doc 05: immutable files in
/// <c>originals/</c> and catalog rows in <c>photos.json</c>. Folder-as-source and album-as-source are
/// the same pipeline after enumeration, so this class is shared by
/// <see cref="FolderPhotoSource"/> and <see cref="OneDrive.OneDrivePhotoSource"/>.
/// <para>Per item: hash the bytes → dedupe by hash → copy in under a content-hash-prefixed immutable
/// name → probe metadata → resolve the date chain (kernel §10) → write the catalog row.</para>
/// <para>Re-scan / re-sync semantics (doc 05, "Delta re-sync"):</para>
/// <list type="bullet">
/// <item><description><b>Excluded wins, always.</b> A re-scan never resurrects an excluded photo
/// (R17); it counts it in the report instead, so the behavior is visible rather than silent.</description></item>
/// <item><description><b>No duplicates.</b> Identity is content identity: matching bytes are a strict
/// no-op — no second copy, no second row (doc 04 §7).</description></item>
/// <item><description><b>Nothing is deleted.</b> An item that vanished from the source is flagged
/// <see cref="Photo.RemovedFromSource"/>; its original stays archived and its placements stand.</description></item>
/// <item><description><b>User intent is untouched.</b> Dates the user set, adjustments, focus regions,
/// tier overrides and exclusions are never written by an import.</description></item>
/// </list>
/// <para>The importer mutates the <see cref="PhotoCatalog"/> it is handed but never persists it: the
/// caller saves through the project store, on its own writer thread (kernel §5, doc 02).</para>
/// </summary>
public sealed class PhotoImporter
{
    private readonly ProjectPaths _paths;
    private readonly IImageProbe _probe;

    /// <summary>Creates an importer for one project folder.</summary>
    /// <param name="paths">The project the photos are being imported into.</param>
    /// <param name="probe">
    /// Metadata reader; defaults to <see cref="ImageDecoderProbe"/>, which is <c>PhotoBook.Imaging</c>'s
    /// Magick.NET header probe with a dependency-free container reader behind it.
    /// </param>
    public PhotoImporter(ProjectPaths paths, IImageProbe? probe = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _probe = probe ?? ImageDecoderProbe.Default;
    }

    /// <summary>
    /// Imports (or re-syncs) every photo the source lists into the catalog, copying originals into the
    /// project folder.
    /// </summary>
    /// <param name="source">Where the photos come from.</param>
    /// <param name="catalog">The catalog to update in place; excluded rows in it are honored (R17).</param>
    /// <param name="options">Run options; null uses <see cref="PhotoImportOptions.Default"/>.</param>
    /// <param name="progress">Optional progress for the job queue's status line.</param>
    /// <param name="ct">Cancellation token; a cancelled run leaves a consistent catalog and no partial files.</param>
    /// <returns>The Import Report for this run.</returns>
    public async Task<PhotoImportReport> ImportAsync(
        IPhotoSource source,
        PhotoCatalog catalog,
        PhotoImportOptions? options = null,
        IProgress<PhotoImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(catalog);
        options ??= PhotoImportOptions.Default;

        var stopwatch = Stopwatch.StartNew();
        var state = new RunState(catalog, options, source);

        Directory.CreateDirectory(_paths.OriginalsFolder);
        // A temp file in originals/ is by definition an abandoned import (doc 04 §7).
        AtomicFile.DeleteStrayTempFiles(_paths.OriginalsFolder);

        var completed = false;
        try
        {
            var parallel = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism),
                CancellationToken = ct,
            };

            await Parallel.ForEachAsync(source.EnumerateAsync(ct), parallel, async (item, token) =>
            {
                await ProcessItemAsync(source, item, state, options, token).ConfigureAwait(false);
                progress?.Report(new PhotoImportProgress(state.ItemsSeen, state.AddedCount, item.FileName));
            }).ConfigureAwait(false);

            completed = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A cancelled sync still returns everything it managed to import.
        }

        if (source is ISkipReportingPhotoSource skipReporter)
        {
            foreach (var skipped in skipReporter.SkippedFiles)
            {
                state.Report(new PhotoImportEntry(PhotoImportOutcome.SkippedUnsupported,
                    Path.GetFileName(skipped.Path), Message: skipped.Reason, SourceId: skipped.Path));
            }
        }

        if (completed && options.DetectRemovals) DetectRemovals(state, source);

        // photos.json stores rows sorted by id so two saves of the same model are byte-identical
        // (doc 04 §4 rule 5).
        var sorted = catalog.Photos.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        catalog.Photos.Clear();
        foreach (var photo in sorted) catalog.Photos.Add(photo);

        stopwatch.Stop();
        return state.BuildReport(source.DisplayName, completed, stopwatch.Elapsed);
    }

    private async Task ProcessItemAsync(
        IPhotoSource source, SourcePhoto item, RunState state, PhotoImportOptions options, CancellationToken ct)
    {
        state.CountSeen();

        try
        {
            if (!SupportedImageFormats.IsSupported(item.FileName))
            {
                state.Report(new PhotoImportEntry(PhotoImportOutcome.SkippedUnsupported, item.FileName,
                    Message: "PhotoBook does not read this file type.", SourceId: item.SourceId));
                return;
            }

            // Pre-download skip: when the source supplies a hash we already hold, the bytes are never
            // fetched at all (doc 05, "Download to originals").
            if (TryNormalizeHash(item.Sha256Hash, out var advertised) && state.TryHandleKnownHash(advertised, item))
                return;

            var temp = Path.Combine(_paths.OriginalsFolder,
                $"import-{Guid.NewGuid():N}{ProjectPaths.TempSuffix}");
            string hash;
            try
            {
                hash = await CopyAndHashAsync(source, item, temp, ct).ConfigureAwait(false);

                if (TryNormalizeHash(item.Sha256Hash, out var expected) &&
                    !string.Equals(expected, hash, StringComparison.OrdinalIgnoreCase))
                {
                    state.Report(new PhotoImportEntry(PhotoImportOutcome.Failed, item.FileName,
                        Message: "The downloaded bytes did not match the hash the source advertised.",
                        SourceId: item.SourceId));
                    TryDelete(temp);
                    return;
                }

                var probe = ProbeTemp(temp, item.FileName);
                Commit(item, state, options, temp, hash, probe);
            }
            finally
            {
                TryDelete(temp);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One bad file never aborts an import batch (doc 05).
            state.Report(new PhotoImportEntry(PhotoImportOutcome.Failed, item.FileName,
                Message: ex.Message, SourceId: item.SourceId));
        }
    }

    private static async Task<string> CopyAndHashAsync(
        IPhotoSource source, SourcePhoto item, string tempPath, CancellationToken ct)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = await source.OpenReadAsync(item, ct).ConfigureAwait(false);
        await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                         bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                hasher.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            await output.FlushAsync(ct).ConfigureAwait(false);
            output.Flush(flushToDisk: true);                      // bytes are durable before the row exists
        }

        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    private ImageProbeResult ProbeTemp(string tempPath, string fileName)
    {
        try
        {
            return _probe.Probe(tempPath, fileName);
        }
        catch (Exception ex)
        {
            // Belt and braces: the contract says a probe returns a failure rather than throwing.
            return ImageProbeResult.Failure(ex.Message);
        }
    }

    private void Commit(
        SourcePhoto item, RunState state, PhotoImportOptions options, string tempPath, string hash, ImageProbeResult probe)
    {
        lock (state.Gate)
        {
            state.MarkSeen(hash, item.SourceId);

            if (state.TryGetByHash(hash, out var known))
            {
                state.RefreshExisting(known!, item);
                return;
            }

            if (state.TryGetBySourceId(item.SourceId, out var modified) && modified is not null)
            {
                var reimport = options.ReimportModifiedSources || options.ReimportPhotoIds?.Contains(modified.Id) == true;
                if (!reimport)
                {
                    // The archived original is immutable; the Import Report offers a per-photo re-import.
                    modified.SourceModified = true;
                    state.Report(new PhotoImportEntry(PhotoImportOutcome.ModifiedAtSource, item.FileName,
                        modified.Id, "The source item changed since import.", item.SourceId));
                    return;
                }

                var swappedPath = PlaceOriginal(state, tempPath, hash, item.FileName, options);
                state.Reindex(modified, hash);
                modified.ContentHash = hash;
                modified.OriginalPath = swappedPath;
                modified.OriginalFileName = item.FileName;
                modified.Width = probe.Width;
                modified.Height = probe.Height;
                modified.DecodeFailed = !probe.Succeeded;
                modified.SourceModified = false;
                modified.RemovedFromSource = false;
                // Identity, dates, adjustments, focus regions, tier override and placements all stand;
                // the new hash changes every cache key by construction (doc 05).
                state.Report(new PhotoImportEntry(PhotoImportOutcome.Reimported, item.FileName, modified.Id,
                    "New bytes swapped in; edits and placements kept.", item.SourceId));
                return;
            }

            var relativePath = PlaceOriginal(state, tempPath, hash, item.FileName, options);
            var photo = BuildPhoto(item, state, hash, relativePath, probe);
            state.Add(photo);

            state.Report(new PhotoImportEntry(PhotoImportOutcome.Added, item.FileName, photo.Id,
                photo.DateUncertain ? "Date taken from the file timestamp." : null, item.SourceId));

            if (!probe.Succeeded)
            {
                state.Report(new PhotoImportEntry(PhotoImportOutcome.DecodeFailed, item.FileName, photo.Id,
                    probe.FailureReason, item.SourceId));
            }
        }
    }

    /// <summary>
    /// Moves the hashed temp file to its immutable home. The name is
    /// <c>originals/{hash16}-{sanitizedName}</c>; if that 16-char prefix is already taken by different
    /// content, the full 64-char hash is used instead (doc 05).
    /// </summary>
    private string PlaceOriginal(RunState state, string tempPath, string hash, string fileName, PhotoImportOptions options)
    {
        var relative = ProjectPaths.OriginalRelativePath(hash, fileName);
        var full = _paths.OriginalFile(relative);

        if (state.IsPathTaken(relative))
        {
            relative = $"{ProjectPaths.OriginalsFolderName}/{hash.ToLowerInvariant()}-{ProjectPaths.SanitizeFileName(fileName)}";
            full = _paths.OriginalFile(relative);
        }

        if (File.Exists(full))
        {
            // A leftover from an interrupted import of these same bytes; the name encodes the content.
            File.SetAttributes(full, FileAttributes.Normal);
            File.Delete(full);
        }

        File.Move(tempPath, full);
        if (options.MarkOriginalsReadOnly)
        {
            try
            {
                File.SetAttributes(full, FileAttributes.ReadOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Immutability is also enforced in code; the attribute is belt and braces.
            }
        }

        return relative;
    }

    private static Photo BuildPhoto(
        SourcePhoto item, RunState state, string hash, string relativePath, ImageProbeResult probe)
    {
        var (takenAt, dateSource, uncertain) = ResolveDate(item, probe, state.ImportedAtUtc);

        var photo = new Photo
        {
            Id = Ids.PhotoId(hash),
            ContentHash = hash,
            OriginalFileName = item.FileName,
            OriginalPath = relativePath,
            Source = new PhotoSourceRef
            {
                Kind = state.PhotoSourceKind,
                DriveItemId = state.PhotoSourceKind == PhotoSourceKind.OneDrive ? item.SourceId : null,
                ImportedAtUtc = state.ImportedAtUtc,
            },
            TakenAt = takenAt,
            DateSource = dateSource,
            DateUncertain = uncertain,
            Width = probe.Width,
            Height = probe.Height,
            DecodeFailed = !probe.Succeeded,
        };

        if (item.PersonTags is { Count: > 0 })
        {
            foreach (var tag in item.PersonTags) photo.PersonTags.Add(tag);
        }

        return photo;
    }

    /// <summary>
    /// The date chain of kernel §10: EXIF <c>DateTimeOriginal</c> → Graph <c>photo.takenDateTime</c> →
    /// file last-modified time, and only the last resort sets <c>dateUncertain</c>. Dates are stored as
    /// unzoned local wall-clock time — the date on the calendar where the photo was taken (doc 05).
    /// </summary>
    private static (DateTime TakenAt, DateSource Source, bool Uncertain) ResolveDate(
        SourcePhoto item, ImageProbeResult probe, DateTime importedAtUtc)
    {
        if (probe.DateTimeOriginal is { } exif)
            return (DateTime.SpecifyKind(exif, DateTimeKind.Unspecified), DateSource.Exif, false);

        if (item.TakenDateTimeUtc is { } graph)
            return (ToLocalWallClock(graph), DateSource.Graph, false);

        if (item.LastModifiedUtc is { } modified)
            return (ToLocalWallClock(modified), DateSource.FileMtime, true);

        // Nothing at all to go on: the import instant, loudly uncertain.
        return (ToLocalWallClock(importedAtUtc), DateSource.FileMtime, true);
    }

    private static DateTime ToLocalWallClock(DateTime utc)
    {
        var asUtc = utc.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(utc, DateTimeKind.Utc) : utc.ToUniversalTime();
        return DateTime.SpecifyKind(asUtc.ToLocalTime(), DateTimeKind.Unspecified);
    }

    private static void DetectRemovals(RunState state, IPhotoSource source)
    {
        var kind = state.PhotoSourceKind;
        foreach (var photo in state.Catalog.Photos)
        {
            if (photo.Source.Kind != kind) continue;
            if (state.WasSeen(photo)) continue;

            if (!photo.RemovedFromSource)
            {
                photo.RemovedFromSource = true;
                state.Report(new PhotoImportEntry(PhotoImportOutcome.RemovedFromSource, photo.OriginalFileName,
                    photo.Id, $"No longer present in {source.DisplayName}; the archived original is kept.",
                    photo.Source.DriveItemId));
            }
        }
    }

    private static bool TryNormalizeHash(string? hash, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(hash)) return false;
        var trimmed = hash.Trim();
        if (trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[7..];
        if (trimmed.Length != 64 || !trimmed.All(Uri.IsHexDigit)) return false;
        normalized = trimmed.ToLowerInvariant();
        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best-effort; a stray temp file is removed by the next import.
        }
    }

    /// <summary>Mutable state for one import run: the indexes, the seen-sets and the report lines.</summary>
    private sealed class RunState
    {
        private readonly Dictionary<string, Photo> _byHash = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Photo> _bySourceId = new(StringComparer.Ordinal);
        private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seenHashes = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seenSourceIds = new(StringComparer.Ordinal);
        private readonly List<PhotoImportEntry> _entries = [];
        private int _seen;
        private int _added;

        public RunState(PhotoCatalog catalog, PhotoImportOptions options, IPhotoSource source)
        {
            Catalog = catalog;
            ImportedAtUtc = options.ImportedAtUtc ?? DateTime.UtcNow;
            PhotoSourceKind = source.Kind == BookSourceKind.LocalFolder ? PhotoSourceKind.Folder : PhotoSourceKind.OneDrive;

            foreach (var photo in catalog.Photos)
            {
                if (!string.IsNullOrEmpty(photo.ContentHash)) _byHash[photo.ContentHash] = photo;
                if (!string.IsNullOrEmpty(photo.Source.DriveItemId)) _bySourceId[photo.Source.DriveItemId!] = photo;
                if (!string.IsNullOrEmpty(photo.OriginalPath)) _paths.Add(photo.OriginalPath);
            }
        }

        public Lock Gate { get; } = new();

        public PhotoCatalog Catalog { get; }

        public DateTime ImportedAtUtc { get; }

        public PhotoSourceKind PhotoSourceKind { get; }

        public int ItemsSeen => Volatile.Read(ref _seen);

        public int AddedCount => Volatile.Read(ref _added);

        public void CountSeen() => Interlocked.Increment(ref _seen);

        public void Report(PhotoImportEntry entry)
        {
            lock (Gate) _entries.Add(entry);
        }

        public bool TryGetByHash(string hash, out Photo? photo) => _byHash.TryGetValue(hash, out photo);

        public bool TryGetBySourceId(string? sourceId, out Photo? photo)
        {
            photo = null;
            return PhotoSourceKind == PhotoSourceKind.OneDrive &&
                   !string.IsNullOrEmpty(sourceId) &&
                   _bySourceId.TryGetValue(sourceId, out photo);
        }

        public bool IsPathTaken(string relativePath) => _paths.Contains(relativePath);

        public void MarkSeen(string hash, string? sourceId)
        {
            _seenHashes.Add(hash);
            if (!string.IsNullOrEmpty(sourceId)) _seenSourceIds.Add(sourceId);
        }

        public bool WasSeen(Photo photo) =>
            _seenHashes.Contains(photo.ContentHash) ||
            (!string.IsNullOrEmpty(photo.Source.DriveItemId) && _seenSourceIds.Contains(photo.Source.DriveItemId!));

        public void Add(Photo photo)
        {
            Catalog.Photos.Add(photo);
            _byHash[photo.ContentHash] = photo;
            _paths.Add(photo.OriginalPath);
            if (!string.IsNullOrEmpty(photo.Source.DriveItemId)) _bySourceId[photo.Source.DriveItemId!] = photo;
            Interlocked.Increment(ref _added);
        }

        public void Reindex(Photo photo, string newHash)
        {
            _byHash.Remove(photo.ContentHash);
            _byHash[newHash] = photo;
            _paths.Add(ProjectPaths.OriginalRelativePath(newHash, photo.OriginalFileName));
        }

        /// <summary>
        /// Handles an item whose hash the source advertised and the catalog already holds, so the bytes
        /// are never fetched. Returns false when the hash is new and the item must be downloaded.
        /// </summary>
        public bool TryHandleKnownHash(string hash, SourcePhoto item)
        {
            lock (Gate)
            {
                if (!_byHash.TryGetValue(hash, out var existing)) return false;

                MarkSeen(hash, item.SourceId);
                RefreshExisting(existing, item);
                return true;
            }
        }

        /// <summary>
        /// Re-sync bookkeeping for a photo that is already in the catalog. Excluded wins, always (R17):
        /// the row is left exactly as it is and only counted.
        /// </summary>
        public void RefreshExisting(Photo existing, SourcePhoto item)
        {
            if (existing.Excluded)
            {
                _entries.Add(new PhotoImportEntry(PhotoImportOutcome.SkippedExcluded, item.FileName, existing.Id,
                    "Excluded photos stay excluded across re-scans.", item.SourceId));
                return;
            }

            existing.RemovedFromSource = false;
            existing.SourceModified = false;

            if (PhotoSourceKind == PhotoSourceKind.OneDrive && !string.IsNullOrEmpty(item.SourceId) &&
                !string.Equals(existing.Source.DriveItemId, item.SourceId, StringComparison.Ordinal))
            {
                // A hash match with a new item id is a OneDrive-side move or rename.
                existing.Source.DriveItemId = item.SourceId;
                _bySourceId[item.SourceId] = existing;
            }

            if (item.PersonTags is { Count: > 0 })
            {
                // People tags are source data, not user intent: refresh the OneDrive ones, keep the
                // user's own (doc 05 — re-sync never touches user intent).
                var userTags = existing.PersonTags.Where(t => t.Source == PersonTagSource.User).ToList();
                existing.PersonTags.Clear();
                foreach (var tag in userTags) existing.PersonTags.Add(tag);
                foreach (var tag in item.PersonTags) existing.PersonTags.Add(tag);
            }

            _entries.Add(new PhotoImportEntry(PhotoImportOutcome.SkippedDuplicate, item.FileName, existing.Id,
                null, item.SourceId));
        }

        public PhotoImportReport BuildReport(string sourceName, bool completed, TimeSpan duration)
        {
            lock (Gate)
            {
                var ordered = _entries
                    .OrderBy(e => (int)e.Outcome)
                    .ThenBy(e => e.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return new PhotoImportReport
                {
                    Entries = ordered,
                    ItemsSeen = ItemsSeen,
                    Completed = completed,
                    SourceName = sourceName,
                    Duration = duration,
                };
            }
        }
    }
}
