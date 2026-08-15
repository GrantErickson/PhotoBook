namespace PhotoBook.Imaging;

/// <summary>
/// A mutex per string key, awaited rather than blocked on. The thumbnail cache needs exactly this: the
/// background job queue drives it at parallelism, and "two requests for the same key must produce one
/// build, not two files" (<see cref="Core.Abstractions.IThumbnailCache"/>), while two requests for
/// <em>different</em> keys must not wait on each other.
/// <para>
/// Entries are reference-counted and removed once the last waiter leaves, so a long session that
/// touches thousands of photos does not accumulate a semaphore per photo.
/// </para>
/// </summary>
internal sealed class KeyedAsyncLock : IDisposable
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Waits for exclusive access to <paramref name="key"/>; dispose the result to release it.</summary>
    /// <param name="key">The key to lock.</param>
    /// <param name="ct">Cancellation token; a cancelled wait acquires nothing and leaks nothing.</param>
    internal async Task<IDisposable> AcquireAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        Entry entry;
        lock (_entries)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.Waiters++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            Release(key, entry, acquired: false);
            throw;
        }

        return new Releaser(this, key, entry);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_entries)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _entries.Values) entry.Semaphore.Dispose();
            _entries.Clear();
        }
    }

    private void Release(string key, Entry entry, bool acquired)
    {
        if (acquired) entry.Semaphore.Release();

        lock (_entries)
        {
            entry.Waiters--;
            if (entry.Waiters > 0 || _disposed) return;
            if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
            {
                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        internal SemaphoreSlim Semaphore { get; } = new(1, 1);

        internal int Waiters { get; set; }
    }

    private sealed class Releaser(KeyedAsyncLock owner, string key, Entry entry) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            owner.Release(key, entry, acquired: true);
        }
    }
}
