using PhotoBook.Core.Model;
using PhotoBook.Core.Persistence;

namespace PhotoBook.Tests;

/// <summary>
/// Durability (doc 04 §6): the autosave loop and crash recovery. The loop is driven through
/// <see cref="AutosaveScheduler.TriggerAsync"/> — exactly what the 30-second timer calls — so these
/// assertions are deterministic instead of sleeping through an interval.
/// </summary>
public class AutosaveAndRecoveryTests
{
    private static readonly DateTime CreatedAt = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------- autosave

    [Fact]
    public async Task DirtyProject_IsWrittenOnTheNextTick()
    {
        using var temp = new TempFolder();
        var store = await NewProjectAsync(temp);
        var book = (await store.LoadBookAsync()).Value;

        using var autosave = NewScheduler(store, book);
        autosave.Attach();

        book.Title = "Renamed while editing";
        autosave.MarkChanged();
        Assert.True(autosave.HasUnsavedChanges);
        Assert.Equal(AutosaveState.Dirty, autosave.Status.State);

        Assert.True(await autosave.TriggerAsync());

        Assert.Equal("Renamed while editing", (await store.LoadBookAsync()).Value.Title);
        Assert.False(autosave.HasUnsavedChanges);
        Assert.Equal(AutosaveState.Saved, autosave.Status.State);
        Assert.NotNull(autosave.Status.LastSavedUtc);
        Assert.Equal(1, autosave.WriteCount);
    }

    [Fact]
    public async Task CleanProject_WritesNothing()
    {
        using var temp = new TempFolder();
        var store = await NewProjectAsync(temp);
        var book = (await store.LoadBookAsync()).Value;

        var writes = 0;
        using var autosave = new AutosaveScheduler(ct =>
        {
            writes++;
            return store.SaveBookAsync(book, ct);
        });
        autosave.Attach();

        var stamp = File.GetLastWriteTimeUtc(store.Paths.BookFile);

        Assert.False(await autosave.TriggerAsync());
        Assert.False(await autosave.TriggerAsync());

        Assert.Equal(0, writes);
        Assert.Equal(0, autosave.WriteCount);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(store.Paths.BookFile));

        // A clean tick must not manufacture a .bak either — churning backups would quietly destroy
        // the last-known-good copy the recovery path depends on.
        Assert.False(File.Exists(store.Paths.BookFile + ProjectPaths.BackupSuffix));
        Assert.Equal(AutosaveState.Saved, autosave.Status.State);
    }

    [Fact]
    public async Task AClosedProject_NeverWrites()
    {
        using var temp = new TempFolder();
        var store = await NewProjectAsync(temp);
        var book = (await store.LoadBookAsync()).Value;

        using var autosave = NewScheduler(store, book);
        autosave.Attach();
        autosave.MarkChanged();
        autosave.Detach();

        Assert.False(await autosave.TriggerAsync());
        Assert.Equal(0, autosave.WriteCount);
        Assert.Equal(AutosaveState.Closed, autosave.Status.State);
        Assert.False(autosave.HasUnsavedChanges);
    }

    [Fact]
    public async Task ABurstOfEdits_CoalescesIntoOneWrite()
    {
        using var temp = new TempFolder();
        var store = await NewProjectAsync(temp);
        var book = (await store.LoadBookAsync()).Value;

        using var autosave = NewScheduler(store, book);
        autosave.Attach();

        for (var i = 0; i < 50; i++)
        {
            book.Title = "Edit " + i;
            autosave.MarkChanged();
        }

        Assert.True(await autosave.TriggerAsync());
        Assert.False(await autosave.TriggerAsync());

        Assert.Equal(1, autosave.WriteCount);
        Assert.Equal("Edit 49", (await store.LoadBookAsync()).Value.Title);
    }

    [Fact]
    public async Task OverlappingTriggers_DoNotDoubleWriteOrCorrupt()
    {
        using var temp = new TempFolder();
        var store = await NewProjectAsync(temp);
        var book = (await store.LoadBookAsync()).Value;

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrent = 0;
        var maxConcurrent = 0;
        var writes = 0;

        using var autosave = new AutosaveScheduler(async ct =>
        {
            var now = Interlocked.Increment(ref concurrent);
            maxConcurrent = Math.Max(maxConcurrent, now);
            Interlocked.Increment(ref writes);

            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            await store.SaveBookAsync(book, ct).ConfigureAwait(false);

            Interlocked.Decrement(ref concurrent);
        });

        autosave.Attach();
        book.Title = "Concurrent";
        autosave.MarkChanged();

        // Five ticks land while the first write is still in the middle of the file.
        var first = autosave.TriggerAsync();
        await entered.Task;
        var rest = Enumerable.Range(0, 4).Select(_ => autosave.TriggerAsync()).ToArray();

        release.SetResult();
        var results = await Task.WhenAll(new[] { first }.Concat(rest));

        Assert.Equal(1, maxConcurrent);          // never two writers in the same file
        Assert.Equal(1, writes);                 // the four late ticks found nothing left to do
        Assert.Equal(1, autosave.WriteCount);
        Assert.Single(results, wrote => wrote);   // exactly one caller reports having written
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.tmp", SearchOption.AllDirectories));
        Assert.Equal("Concurrent", (await store.LoadBookAsync()).Value.Title);
    }

    [Fact]
    public async Task AnEditDuringASave_IsPickedUpByTheNextOne()
    {
        using var temp = new TempFolder();
        var store = await NewProjectAsync(temp);
        var book = (await store.LoadBookAsync()).Value;

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var autosave = new AutosaveScheduler(async ct =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            await store.SaveBookAsync(book, ct).ConfigureAwait(false);
        });

        autosave.Attach();
        book.Title = "First";
        autosave.MarkChanged();

        var saving = autosave.TriggerAsync();
        await entered.Task;
        Assert.Equal(AutosaveState.Saving, autosave.Status.State);

        // The user keeps typing while the write is in flight.
        book.Title = "Second";
        autosave.MarkChanged();

        release.SetResult();
        Assert.True(await saving);

        // The in-flight save cannot claim to have covered an edit that arrived after it started.
        Assert.True(autosave.HasUnsavedChanges);
        Assert.Equal(AutosaveState.Dirty, autosave.Status.State);

        Assert.True(await autosave.TriggerAsync());
        Assert.Equal(2, autosave.WriteCount);
        Assert.Equal("Second", (await store.LoadBookAsync()).Value.Title);
    }

    [Fact]
    public async Task AFailedSave_IsReportedAndRetried()
    {
        using var temp = new TempFolder();
        var store = await NewProjectAsync(temp);
        var book = (await store.LoadBookAsync()).Value;

        var fail = true;
        Exception? reported = null;
        using var autosave = new AutosaveScheduler(ct =>
        {
            if (fail) throw new IOException("the drive went away");
            return store.SaveBookAsync(book, ct);
        });
        autosave.SaveFailed += ex => reported = ex;

        autosave.Attach();
        book.Title = "Survives a bad disk";
        autosave.MarkChanged();

        Assert.False(await autosave.TriggerAsync());          // autosave never throws at the timer
        Assert.Equal(AutosaveState.Failed, autosave.Status.State);
        Assert.True(autosave.Status.HasUnsavedChanges);
        Assert.Contains("drive went away", autosave.Status.Label, StringComparison.Ordinal);
        Assert.IsType<IOException>(reported);

        fail = false;
        Assert.True(await autosave.TriggerAsync());           // the edit was never dropped
        Assert.Equal(AutosaveState.Saved, autosave.Status.State);
        Assert.Equal("Survives a bad disk", (await store.LoadBookAsync()).Value.Title);
    }

    [Fact]
    public async Task AnExplicitSave_ThrowsWhereAutosaveSwallows()
    {
        using var temp = new TempFolder();
        var store = await NewProjectAsync(temp);

        using var autosave = new AutosaveScheduler(_ => throw new IOException("read-only volume"));
        autosave.Attach();
        autosave.MarkChanged();

        await Assert.ThrowsAsync<IOException>(() => autosave.SaveNowAsync());
        Assert.Equal(AutosaveState.Failed, autosave.Status.State);
    }

    [Fact]
    public async Task FlushOnExit_WaitsForTheSaveInFlightAndWritesWhatIsLeft()
    {
        using var temp = new TempFolder();
        var store = await NewProjectAsync(temp);
        var book = (await store.LoadBookAsync()).Value;

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateFirstWrite = true;

        using var autosave = new AutosaveScheduler(async ct =>
        {
            if (gateFirstWrite)
            {
                gateFirstWrite = false;
                entered.TrySetResult();
                await release.Task.ConfigureAwait(false);
            }

            await store.SaveBookAsync(book, ct).ConfigureAwait(false);
        });

        autosave.Attach();
        book.Title = "In flight";
        autosave.MarkChanged();

        var inFlight = autosave.TriggerAsync();
        await entered.Task;

        book.Title = "Typed just before quitting";
        autosave.MarkChanged();

        var flush = autosave.FlushAsync();
        release.SetResult();

        await inFlight;
        Assert.True(await flush);

        Assert.False(autosave.HasUnsavedChanges);
        Assert.Equal("Typed just before quitting", (await store.LoadBookAsync()).Value.Title);
    }

    [Fact]
    public async Task FlushBlocking_IsSafeFromASynchronousShutdownPath()
    {
        using var temp = new TempFolder();
        var store = await NewProjectAsync(temp);
        var book = (await store.LoadBookAsync()).Value;

        using var autosave = NewScheduler(store, book);
        autosave.Attach();
        autosave.Start();

        book.Title = "Saved on exit";
        autosave.MarkChanged();

        Assert.True(autosave.FlushBlocking(TimeSpan.FromSeconds(30)));
        Assert.Equal("Saved on exit", (await store.LoadBookAsync()).Value.Title);
        Assert.False(autosave.HasUnsavedChanges);
    }

    [Fact]
    public void TheStatusLabel_NeverClaimsASaveThatDidNotHappen()
    {
        Assert.Equal(string.Empty, AutosaveStatus.Closed.Label);
        Assert.Equal("Unsaved changes", new AutosaveStatus(AutosaveState.Dirty, null, null).Label);
        Assert.Equal("Saving…", new AutosaveStatus(AutosaveState.Saving, null, null).Label);
        Assert.StartsWith("Saved", new AutosaveStatus(AutosaveState.Saved, DateTime.UtcNow, null).Label, StringComparison.Ordinal);
        Assert.StartsWith("Not saved", new AutosaveStatus(AutosaveState.Failed, null, "disk full").Label, StringComparison.Ordinal);
        Assert.False(new AutosaveStatus(AutosaveState.Saved, DateTime.UtcNow, null).HasUnsavedChanges);
        Assert.True(new AutosaveStatus(AutosaveState.Saving, null, null).HasUnsavedChanges);
    }

    // ------------------------------------------------------------- recovery

    [Fact]
    public async Task ATruncatedBookFile_RecoversFromItsBackupAndSaysSo()
    {
        using var temp = new TempFolder();
        var store = await NewProjectAsync(temp);

        // Two good saves, so book.json has a .bak beside it holding the previous version.
        var book = (await store.LoadBookAsync()).Value;
        book.Title = "Our 2024, revised";
        await store.SaveBookAsync(book);
        Assert.True(File.Exists(store.Paths.BookFile + ProjectPaths.BackupSuffix));

        // The crash: the machine died with book.json half written.
        var good = await File.ReadAllTextAsync(store.Paths.BookFile);
        await File.WriteAllTextAsync(store.Paths.BookFile, good[..(good.Length / 2)]);

        var loaded = await new ProjectStore(temp.Path).LoadAsync();
        var report = new ProjectRecoveryReport(loaded.Notices);

        Assert.Equal("Our 2024", loaded.Value.Book.Title);   // the .bak content, not a guess
        Assert.True(report.RecoveredAnything);
        Assert.True(report.InterruptedWriteDetected);
        Assert.True(report.NeedsAttention);
        Assert.Contains("book.json", report.Headline, StringComparison.Ordinal);
        Assert.Contains("restored from the last good save", report.Headline, StringComparison.Ordinal);
        Assert.Contains("rewritten in full by the next save", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecoveredProject_IsRewrittenByTheNextSave()
    {
        using var temp = new TempFolder();
        var store = await NewProjectAsync(temp);

        var book = (await store.LoadBookAsync()).Value;
        book.Title = "Our 2024, revised";
        await store.SaveBookAsync(book);

        var good = await File.ReadAllTextAsync(store.Paths.BookFile);
        await File.WriteAllTextAsync(store.Paths.BookFile, good[..(good.Length / 2)]);

        var loaded = await new ProjectStore(temp.Path).LoadAsync();
        var report = new ProjectRecoveryReport(loaded.Notices);
        var recovered = loaded.Value.Book;

        // This is what the session does after a recovery: stay dirty so the loop heals the folder.
        using var autosave = NewScheduler(store, recovered);
        autosave.Attach();
        if (report.RecoveredAnything) autosave.MarkChanged();

        Assert.True(await autosave.TriggerAsync());

        var healed = await new ProjectStore(temp.Path).LoadAsync();
        Assert.Empty(healed.Notices);
        Assert.Equal("Our 2024", healed.Value.Book.Title);
    }

    [Fact]
    public async Task AStrayTempFile_IsSweptAndReportedAsAnInterruptedWrite()
    {
        using var temp = new TempFolder();
        var store = await NewProjectAsync(temp);
        await store.SaveChapterAsync(NewChapter(2024, 3));

        // The fingerprints of a process killed mid-write: temp files in the root and in chapters/.
        var rootTemp = store.Paths.PhotosFile + ProjectPaths.TempSuffix;
        var chapterTemp = store.Paths.ChapterFile(2024, 3) + ProjectPaths.TempSuffix;
        await File.WriteAllTextAsync(rootTemp, "{ \"schemaVersion\": 1, \"photo");
        await File.WriteAllTextAsync(chapterTemp, "{ \"schemaVersion\": 1, \"pag");

        var loaded = await new ProjectStore(temp.Path).LoadAsync();
        var report = new ProjectRecoveryReport(loaded.Notices);

        Assert.False(File.Exists(rootTemp));
        Assert.False(File.Exists(chapterTemp));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.tmp", SearchOption.AllDirectories));

        Assert.Equal(2, report.DiscardedTempFiles.Count);
        Assert.False(report.RecoveredAnything);              // nothing was lost — the swap never happened
        Assert.True(report.InterruptedWriteDetected);
        Assert.Contains("not closed cleanly", report.Headline, StringComparison.Ordinal);
        Assert.Contains(ProjectPaths.PhotosFileName, report.Detail, StringComparison.Ordinal);
        Assert.Equal("Our 2024", loaded.Value.Book.Title);   // and the project still opens intact
    }

    [Fact]
    public async Task ACleanOpen_ReportsNothing()
    {
        using var temp = new TempFolder();
        await NewProjectAsync(temp);

        var loaded = await new ProjectStore(temp.Path).LoadAsync();
        var report = new ProjectRecoveryReport(loaded.Notices);

        Assert.True(report.IsClean);
        Assert.False(report.NeedsAttention);
        Assert.False(report.InterruptedWriteDetected);
        Assert.Equal(string.Empty, report.Headline);
        Assert.Equal(string.Empty, report.ToString());
    }

    [Fact]
    public async Task ASyncConflictCopy_IsIgnoredAndNamed()
    {
        using var temp = new TempFolder();
        var store = await NewProjectAsync(temp);
        await store.SaveChapterAsync(NewChapter(2024, 3));

        var conflict = Path.Combine(store.Paths.ChaptersFolder, "2024-03-Copy.json");
        File.Copy(store.Paths.ChapterFile(2024, 3), conflict);

        var loaded = await new ProjectStore(temp.Path).LoadAsync();
        var report = new ProjectRecoveryReport(loaded.Notices);

        Assert.Single(loaded.Value.Chapters);
        Assert.Single(report.IgnoredConflictCopies);
        Assert.Contains("sync conflict copy", report.Headline, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- helpers

    private static async Task<ProjectStore> NewProjectAsync(TempFolder temp)
    {
        await ProjectStore.CreateNewAsync(temp.Path, 2024, "Our 2024", seed: 1, createdAtUtc: CreatedAt);
        return new ProjectStore(temp.Path);
    }

    /// <summary>An autosave loop over a real project folder, with the timer left stopped.</summary>
    private static AutosaveScheduler NewScheduler(ProjectStore store, Book book) =>
        new(ct => store.SaveBookAsync(book, ct));

    private static Chapter NewChapter(int year, int month) => new()
    {
        Year = year,
        Month = month,
        Pages = { new Page { Id = "pg-0001", TemplateRef = "t-01-text-a" } },
    };
}
