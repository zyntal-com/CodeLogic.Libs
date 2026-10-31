namespace CL.Common.Threading;

/// <summary>
/// An asynchronous mutual-exclusion lock per key: callers holding different keys run in parallel, callers with the
/// same key run one at a time. Lock state exists only while a key is held or awaited; idle keys cost nothing, and
/// the per-key semaphores are pooled, so locking millions of distinct keys over time does not grow memory.
/// </summary>
/// <remarks>
/// <code>
/// using (await locks.LockAsync(userId, ct))
/// {
///     // only one caller per userId gets here at a time
/// }
/// </code>
/// The lock is not reentrant: locking a key you already hold deadlocks. Dispose each <see cref="Releaser"/> exactly
/// once. Thread-safe.
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
public sealed class AsyncKeyedLock<TKey> where TKey : notnull
{
    private const int MaxPooled = 64;

    private readonly Dictionary<TKey, Entry> _entries;
    private readonly Stack<Entry> _pool = new();
    private readonly Lock _sync = new();

    /// <summary>Creates a keyed lock.</summary>
    /// <param name="comparer">Key comparer; null uses the default equality comparer.</param>
    public AsyncKeyedLock(IEqualityComparer<TKey>? comparer = null) =>
        _entries = new Dictionary<TKey, Entry>(comparer);

    /// <summary>The number of keys currently held or awaited (diagnostics).</summary>
    public int Count
    {
        get
        {
            lock (_sync) return _entries.Count;
        }
    }

    /// <summary>
    /// Waits until <paramref name="key"/> is free and takes it. Completes synchronously (no allocation) when the key
    /// is free.
    /// </summary>
    /// <param name="key">The key to lock.</param>
    /// <param name="ct">Cancels the wait; the lock is then not taken.</param>
    /// <returns>A releaser; dispose it to release the key.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled before the lock was taken.</exception>
    public ValueTask<Releaser> LockAsync(TKey key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var entry = Rent(key);
        if (entry.Semaphore.Wait(0))
            return ValueTask.FromResult(new Releaser(this, key, entry));
        return WaitAsync(key, entry, ct);
    }

    /// <summary>Takes <paramref name="key"/> only if it is free right now.</summary>
    /// <param name="key">The key to lock.</param>
    /// <param name="releaser">On success, the releaser to dispose; otherwise <c>default</c>.</param>
    /// <returns><c>true</c> when the lock was taken.</returns>
    public bool TryLock(TKey key, out Releaser releaser)
    {
        var entry = Rent(key);
        if (entry.Semaphore.Wait(0))
        {
            releaser = new Releaser(this, key, entry);
            return true;
        }
        Return(key, entry);
        releaser = default;
        return false;
    }

    private async ValueTask<Releaser> WaitAsync(TKey key, Entry entry, CancellationToken ct)
    {
        try
        {
            await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            Return(key, entry);
            throw;
        }
        return new Releaser(this, key, entry);
    }

    private Entry Rent(TKey key)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = _pool.Count > 0 ? _pool.Pop() : new Entry();
                _entries.Add(key, entry);
            }
            entry.References++;
            return entry;
        }
    }

    private void Return(TKey key, Entry entry)
    {
        lock (_sync)
        {
            if (--entry.References > 0)
                return;
            _entries.Remove(key);
            // Every holder released and every waiter left, so the semaphore is back at 1 and reusable.
            if (_pool.Count < MaxPooled)
                _pool.Push(entry);
            else
                entry.Semaphore.Dispose();
        }
    }

    private void Release(TKey key, Entry entry)
    {
        entry.Semaphore.Release();
        Return(key, entry);
    }

    internal sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int References;
    }

    /// <summary>Holds a key of an <see cref="AsyncKeyedLock{TKey}"/>; dispose it (once) to release the key.</summary>
    public readonly struct Releaser : IDisposable
    {
        private readonly AsyncKeyedLock<TKey>? _owner;
        private readonly TKey _key;
        private readonly Entry? _entry;

        internal Releaser(AsyncKeyedLock<TKey> owner, TKey key, Entry entry)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
        }

        /// <summary>Releases the key. Disposing a <c>default</c> releaser does nothing.</summary>
        public void Dispose()
        {
            if (_owner is not null)
                _owner.Release(_key, _entry!);
        }
    }
}
