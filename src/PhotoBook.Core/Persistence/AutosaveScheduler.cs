namespace PhotoBook.Core.Persistence;

/// <summary>
/// The dirty-tracking autosave loop of doc 04 §6: a project that has changed is written every
/// <see cref="Interval"/> (30 s by default) and immediately after every major operation, so the
/// on-disk state trails the in-memory model by at most one interval (storage invariant 3).
/// <para>
/// Three properties make it safe to run under a live editor:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Never blocks the UI.</b> The tick fires on a timer thread and the save callback is awaited,
/// never waited on; nothing here touches a dispatcher.
/// </description></item>
/// <item><description>
/// <b>Never overlaps.</b> One write at a time, serialized by a gate — two triggers cannot have the
/// same file half-written from two threads.
/// </description></item>
/// <item><description>
/// <b>Coalesces.</b> Edits bump a version counter, and a save records the version it covered, so a
/// burst of a hundred nudges costs one write and a project with nothing to say writes nothing at all.
/// </description></item>
/// </list>
/// <para>
/// <see cref="TriggerAsync"/> is the whole loop's single entry point — the timer calls exactly what
/// a test calls, so autosave behaviour is asserted deterministically rather than by sleeping.
/// </para>
/// </summary>
public sealed class AutosaveScheduler : IDisposable
{
    /// <summary>The interval doc 04 §6 specifies: 30 seconds.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    private readonly Func<CancellationToken, Task> _save;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _sync = new();
    private readonly Timer _timer;

    private long _version;
    private long _savedVersion;
    private int _writeCount;
    private AutosaveState _state = AutosaveState.Closed;
    private DateTime? _lastSavedUtc;
    private string? _lastError;
    private bool _running;
    private volatile bool _disposed;

    /// <param name="save">
    /// Writes the whole project. It is invoked off the calling thread's control flow only in the
    /// sense that the scheduler awaits it — capturing a consistent snapshot of the model is the
    /// callback's job, not the scheduler's.
    /// </param>
    /// <param name="interval">Tick interval; defaults to <see cref="DefaultInterval"/>.</param>
    public AutosaveScheduler(Func<CancellationToken, Task> save, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(save);
        _save = save;
        Interval = interval is { } value && value > TimeSpan.Zero ? value : DefaultInterval;
        _timer = new Timer(_ => _ = TriggerAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>How long a change can sit in memory before the timer writes it.</summary>
    public TimeSpan Interval { get; }

    /// <summary>Raised on every state transition, on the thread that caused it.</summary>
    public event Action<AutosaveStatus>? StatusChanged;

    /// <summary>
    /// Raised when a write throws. Autosave never rethrows into a timer callback; the shell decides
    /// how loudly to complain.
    /// </summary>
    public event Action<Exception>? SaveFailed;

    /// <summary>The current state, safe to read from any thread.</summary>
    public AutosaveStatus Status
    {
        get
        {
            lock (_sync)
            {
                return new AutosaveStatus(_state, _lastSavedUtc, _lastError);
            }
        }
    }

    /// <summary>True when some edit exists only in memory.</summary>
    public bool HasUnsavedChanges
    {
        get
        {
            lock (_sync)
            {
                return _state != AutosaveState.Closed && _version != _savedVersion;
            }
        }
    }

    /// <summary>How many writes actually happened. Tests assert coalescing with it.</summary>
    public int WriteCount
    {
        get
        {
            lock (_sync)
            {
                return _writeCount;
            }
        }
    }

    /// <summary>True while a write is in flight.</summary>
    public bool IsSaving
    {
        get
        {
            lock (_sync)
            {
                return _running;
            }
        }
    }

    // ------------------------------------------------------------- lifecycle

    /// <summary>
    /// A project has been opened and matches what is on disk. Resets the dirty ledger and starts the
    /// timer.
    /// </summary>
    public void Attach()
    {
        AutosaveStatus status;
        lock (_sync)
        {
            _savedVersion = _version;
            _lastError = null;
            _state = AutosaveState.Saved;
            status = Snapshot();
        }

        Start();
        StatusChanged?.Invoke(status);
    }

    /// <summary>
    /// The project has been closed. Stops the timer and drops the dirty ledger — the caller is
    /// responsible for flushing <em>before</em> calling this (see the session's close path).
    /// </summary>
    public void Detach()
    {
        AutosaveStatus status;
        lock (_sync)
        {
            _savedVersion = _version;
            _lastError = null;
            _state = AutosaveState.Closed;
            status = Snapshot();
        }

        Stop();
        StatusChanged?.Invoke(status);
    }

    /// <summary>Starts (or restarts) the tick. Idempotent.</summary>
    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _timer.Change(Interval, Interval);
        }
        catch (ObjectDisposedException)
        {
            // Shutdown raced the last edit; the shutdown flush already covered it.
        }
    }

    /// <summary>Stops the tick without touching the dirty ledger. Idempotent.</summary>
    public void Stop()
    {
        try
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // ---------------------------------------------------------------- signal

    /// <summary>
    /// The model changed. Cheap and lock-only — this is on the path of every keystroke and every
    /// drag, so it must never do I/O.
    /// </summary>
    public void MarkChanged()
    {
        AutosaveStatus? status = null;
        lock (_sync)
        {
            _version++;
            if (_state is AutosaveState.Saved)
            {
                _state = AutosaveState.Dirty;
                status = Snapshot();
            }
        }

        if (status is not null)
        {
            StatusChanged?.Invoke(status);
        }
    }

    // ------------------------------------------------------------------ save

    /// <summary>
    /// The autosave tick, and the immediate save after a major operation. Writes only if something
    /// changed, waits for any write already in flight, and never throws — a failed autosave is
    /// reported through <see cref="SaveFailed"/> and retried on the next tick.
    /// </summary>
    /// <returns>True when this call actually wrote the project.</returns>
    public Task<bool> TriggerAsync(CancellationToken ct = default) => SaveCoreAsync(throwOnError: false, ct);

    /// <summary>
    /// An explicit user save (Ctrl+S). Same serialization as autosave, so a manual save can never
    /// race a tick — but failures propagate, because the user is standing right there.
    /// </summary>
    /// <returns>True when this call actually wrote the project.</returns>
    public Task<bool> SaveNowAsync(CancellationToken ct = default) => SaveCoreAsync(throwOnError: true, ct);

    /// <summary>
    /// The exit / close-book flush: waits for a write already in flight, then writes anything still
    /// pending. Never throws — shutdown is not the moment for a dialog that blocks the process.
    /// </summary>
    public Task<bool> FlushAsync(CancellationToken ct = default) => SaveCoreAsync(throwOnError: false, ct);

    /// <summary>
    /// The last-resort flush for a synchronous shutdown path (<c>Application.Exit</c>,
    /// <c>SessionEnding</c>). Blocks the caller for at most <paramref name="timeout"/>. Safe to call
    /// from the UI thread: nothing in the save path marshals back to a dispatcher, so there is no
    /// re-entrancy to deadlock on.
    /// </summary>
    /// <returns>True when the project was written (or was already clean).</returns>
    public bool FlushBlocking(TimeSpan? timeout = null)
    {
        Stop();
        var limit = timeout ?? TimeSpan.FromSeconds(10);
        try
        {
            return FlushAsync().Wait(limit);
        }
        catch (AggregateException)
        {
            return false;
        }
    }

    private async Task<bool> SaveCoreAsync(bool throwOnError, CancellationToken ct)
    {
        if (_disposed)
        {
            return false;
        }

        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }

        try
        {
            AutosaveStatus entered;
            long target;
            lock (_sync)
            {
                // A closed project has nothing to write; a clean one must not touch the disk at all,
                // or every tick would rewrite identical bytes and churn the .bak files.
                if (_disposed || _state == AutosaveState.Closed || _version == _savedVersion)
                {
                    return false;
                }

                target = _version;
                _running = true;
                _state = AutosaveState.Saving;
                entered = Snapshot();
            }

            StatusChanged?.Invoke(entered);

            await _save(ct).ConfigureAwait(false);

            AutosaveStatus done;
            lock (_sync)
            {
                _savedVersion = Math.Max(_savedVersion, target);
                _writeCount++;
                _lastSavedUtc = DateTime.UtcNow;
                _lastError = null;
                _running = false;
                _state = _version == _savedVersion ? AutosaveState.Saved : AutosaveState.Dirty;
                done = Snapshot();
            }

            StatusChanged?.Invoke(done);
            return true;
        }
        catch (OperationCanceledException)
        {
            // Settled first and published second: `StatusChanged?.Invoke(Settle(…))` would skip the
            // state change entirely whenever nobody is listening.
            var cancelled = Settle(error: null);
            StatusChanged?.Invoke(cancelled);
            if (throwOnError)
            {
                throw;
            }

            return false;
        }
        catch (Exception ex)
        {
            var failed = Settle(ex.Message);
            StatusChanged?.Invoke(failed);
            SaveFailed?.Invoke(ex);
            if (throwOnError)
            {
                throw;
            }

            return false;
        }
        finally
        {
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Returns the loop to a resting state after a cancelled or failed write.</summary>
    private AutosaveStatus Settle(string? error)
    {
        lock (_sync)
        {
            _running = false;
            _lastError = error;
            _state = error is not null
                ? AutosaveState.Failed
                : _version == _savedVersion ? AutosaveState.Saved : AutosaveState.Dirty;
            return Snapshot();
        }
    }

    private AutosaveStatus Snapshot() => new(_state, _lastSavedUtc, _lastError);

    /// <summary>Stops the tick. Does not flush — flushing is an explicit decision the caller makes.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();

        // The gate is deliberately not disposed: a write may still be unwinding on another thread,
        // and SemaphoreSlim without a wait handle has nothing to leak.
    }
}
