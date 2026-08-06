using System.IO;
using PhotoBook.Core.Model;
using PhotoBook.Imaging.Auto;

namespace PhotoBook.App.Services;

/// <summary>
/// Runs auto-adjust over many photos: measure off the UI thread, commit on it as one undoable step.
///
/// <para><b>Measure wide, commit once.</b> Measuring is a decode per photo and is the whole cost of a
/// run, so it happens in parallel on the job queue's thread and can be cancelled at any point without
/// having changed anything — doc 09 §3.8's rule for engine batch commands, "cancel = no-op, model
/// untouched until commit". The commit then runs on the dispatcher inside one
/// <see cref="UndoStack.BeginBatch"/>, so the whole book-wide change is a single <c>Ctrl+Z</c> rather
/// than several hundred entries that would blow the 200-deep undo stack and take the user's earlier
/// history with them.</para>
///
/// <para><b>Nothing needless is written.</b> A photo whose freshly-derived stack equals the one it
/// already has is left untouched, not rewritten with identical numbers: every write mints new
/// thumbnail keys and abandons the old files on disk, and a second click of the button should cost
/// nothing.</para>
/// </summary>
public sealed class AutoAdjustRunner
{
    private readonly ProjectSession _session;
    private readonly PhotoEditor _editor;
    private readonly PhotoMeasurer _measurer;

    /// <summary>Creates a runner over the open project.</summary>
    /// <param name="session">The open project; supplies the catalog, the look profile and the paths.</param>
    /// <param name="editor">The editor every write goes through, so every write is undoable.</param>
    /// <param name="measurer">The measurer; defaults to one over a fresh decoder.</param>
    public AutoAdjustRunner(ProjectSession session, PhotoEditor editor, PhotoMeasurer? measurer = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(editor);
        _session = session;
        _editor = editor;
        _measurer = measurer ?? new PhotoMeasurer();
    }

    /// <summary>The look settings a run would apply — the book's, or the defaults before one is open.</summary>
    public LookProfile Look => _session.Book?.Look ?? new LookProfile();

    /// <summary>Every photo a run could consider: everything in the book that still exists.</summary>
    public IReadOnlyList<Photo> Candidates() =>
        _session.Catalog.Photos.Where(p => !p.Excluded && !p.DecodeFailed).ToList();

    /// <summary>Counts what a run would do, so the user can be told before it starts.</summary>
    public AutoAdjustPlan Describe() => AutoAdjustSelection.Describe(Candidates(), Look);

    /// <summary>
    /// Measures and adjusts every photo in scope. Call from the job queue; the commit marshals itself
    /// onto the UI thread.
    /// </summary>
    /// <param name="includeManual">Also re-adjust photos the user edited by hand.</param>
    /// <param name="progress">Reports photos measured, out of the total to measure.</param>
    /// <param name="ct">Cancellation; a cancelled run commits nothing.</param>
    /// <returns>How many photos actually changed.</returns>
    public async Task<int> RunAsync(
        bool includeManual, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var look = Look;
        var version = AutoAdjustRules.VersionFor(look);
        var targets = Candidates().Where(p => AutoAdjustSelection.IsInScope(p, includeManual)).ToList();
        if (targets.Count == 0) return 0;

        var results = await MeasureAllAsync(targets, look, version, progress, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        return await CommitAsync(results, "Auto-adjust photos").ConfigureAwait(false);
    }

    /// <summary>
    /// Auto-adjusts a single photo, overriding any hand edits and returning it to the automatic state.
    /// This is the per-photo command in the editor, and the one route back for a photo the user
    /// changed and now regrets.
    /// </summary>
    /// <param name="photo">The photo to adjust.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when the photo's parameters actually changed.</returns>
    public async Task<bool> RunOneAsync(Photo photo, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(photo);

        var look = Look;
        var version = AutoAdjustRules.VersionFor(look);

        // Always re-measure for the single-photo command: it is one decode, the user asked for it
        // explicitly, and reusing a stamp written under different bytes would be the one case where
        // the shortcut is visible.
        var result = await MeasureOneAsync(photo, look, version, reuseStamp: false, ct).ConfigureAwait(false);
        if (result is null) return false;

        return await CommitAsync([result.Value], "Auto-adjust photo").ConfigureAwait(false) > 0;
    }

    private async Task<List<Result>> MeasureAllAsync(
        IReadOnlyList<Photo> targets,
        LookProfile look,
        string version,
        IProgress<(int Done, int Total)>? progress,
        CancellationToken ct)
    {
        var results = new List<Result>(targets.Count);
        var gate = new object();
        var done = 0;

        // Decoding is CPU-bound and Magick.NET's natives are single-threaded per image, so the work
        // parallelizes cleanly. Half the cores, matching the analysis runner: the other half keeps
        // the UI and the thumbnail builder responsive while a book-length run is going.
        var options = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
        };

        await Parallel.ForEachAsync(targets, options, async (photo, token) =>
        {
            var result = await MeasureOneAsync(photo, look, version, reuseStamp: true, token).ConfigureAwait(false);

            lock (gate)
            {
                if (result is not null) results.Add(result.Value);
                done++;
                progress?.Report((done, targets.Count));
            }
        }).ConfigureAwait(false);

        return results;
    }

    private async Task<Result?> MeasureOneAsync(
        Photo photo, LookProfile look, string version, bool reuseStamp, CancellationToken ct)
    {
        AutoAdjustMeasurement measurement;

        if (reuseStamp && AutoAdjustSelection.ReusableMeasurement(photo) is { } cached)
        {
            // The pixels have not changed, so neither has what they measure. Only the rules can have
            // moved, and those are arithmetic — this is what makes changing the look and re-running
            // instant rather than a decode per photo.
            measurement = cached;
        }
        else
        {
            var path = ResolveOriginal(photo);
            if (path is null || !File.Exists(path)) return null;

            try
            {
                measurement = await _measurer
                    .MeasureAsync(path, look.Straighten, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unreadable file must not abandon a book-length run; it simply keeps whatever
                // adjustments it already had.
                return null;
            }
        }

        var next = AutoAdjustRules.Choose(measurement, look, photo.Adjustments);
        var stamp = new AutoAdjustStamp
        {
            RulesVersion = version,
            SourceHash = photo.ContentHash,
            Measurement = measurement,
        };

        return new Result(photo, next, stamp);
    }

    /// <summary>
    /// Applies the measured results on the UI thread as one undo entry, then saves. Returns the
    /// number of photos whose parameters actually moved.
    /// </summary>
    private Task<int> CommitAsync(IReadOnlyList<Result> results, string description)
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        JobQueue.PostUi(() =>
        {
            try
            {
                var changed = 0;

                using (_editor.Undo.BeginBatch(description))
                {
                    foreach (var (photo, next, stamp) in results)
                    {
                        // Identical parameters and identical provenance: writing would re-key this
                        // photo's thumbnails to produce the very same pixels.
                        if (!AutoAdjustSelection.NeedsWrite(photo, next, stamp)) continue;

                        var moved = next != photo.Adjustments;
                        _editor.SetAutoAdjustments(photo, next, stamp, description);
                        if (moved) changed++;
                    }
                }

                if (changed > 0 || results.Count > 0)
                {
                    // A book-length run is far too much work to leave riding on a clean exit, and it
                    // is not on doc 04 §6's immediate-save list only because it did not exist yet.
                    _ = _session.AutosaveAsync();
                }

                completion.SetResult(changed);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        return completion.Task;
    }

    private string? ResolveOriginal(Photo photo)
    {
        if (string.IsNullOrWhiteSpace(photo.OriginalPath)) return null;
        if (Path.IsPathRooted(photo.OriginalPath)) return photo.OriginalPath;
        return _session.Paths?.OriginalFile(photo.OriginalPath);
    }

    private readonly record struct Result(Photo Photo, AdjustmentStack Next, AutoAdjustStamp Stamp);
}
