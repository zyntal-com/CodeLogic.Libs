using CL.Storage.Models;

namespace CL.Storage.Providers;

/// <summary>
/// Bounded, self-expiring store of sorted listing snapshots shared by the providers that have no
/// native server-side paging (FTP, SFTP, WebDAV, local). A snapshot is produced once per listing
/// pass and every later page is served from it, so paging a directory costs one walk instead of one
/// walk per page.
/// </summary>
/// <remarks>
/// Losing a snapshot is always safe: <see cref="ProviderPaging"/> re-walks the directory and mints a
/// fresh snapshot, which costs time but never changes the items a caller sees. Nothing here is
/// authoritative, so eviction is free to be aggressive.
/// </remarks>
internal sealed class ProviderListingCache
{
    /// <summary>Snapshots retained before the least recently used one is evicted.</summary>
    internal const int MaxSnapshots = 32;
    /// <summary>Items retained across all snapshots before the least recently used ones are evicted.</summary>
    internal const int MaxItems = 250_000;
    /// <summary>Idle time since the last page before a snapshot is discarded.</summary>
    /// <remarks>
    /// <para>
    /// Measured from last use, not from creation: a consumer doing real work between pages (an IPC
    /// round trip per entry, say) can take far longer than this to page a large listing, and an
    /// age-based limit would drop the snapshot mid-pass and force exactly the full re-walk this cache
    /// exists to avoid.
    /// </para>
    /// <para>
    /// There is deliberately no absolute age ceiling on top of this. A ceiling would expire a
    /// snapshot mid-pass and re-walk, which does not make the listing fresher — it splices two
    /// different views of the directory into one paged result, which is worse for the caller than
    /// finishing the point-in-time view they started. Retention stays bounded by the snapshot and
    /// item limits above plus idle expiry, so a long-lived snapshot is a freshness question rather
    /// than a resource one.
    /// </para>
    /// </remarks>
    internal static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Gets the process-wide cache used by the providers.</summary>
    public static ProviderListingCache Shared { get; } = new();

    private readonly Dictionary<string, Snapshot> _snapshots = [with(StringComparer.Ordinal)];
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private long _stamp;
    private int _itemCount;

    /// <summary>Initializes a cache over an injectable clock.</summary>
    /// <param name="time">Clock used for snapshot expiry; defaults to the system clock.</param>
    public ProviderListingCache(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Gets the number of snapshots currently retained, including any not yet expired out.</summary>
    public int Count
    {
        get { lock (_gate) return _snapshots.Count; }
    }

    /// <summary>Returns a live snapshot recorded for one listing pass.</summary>
    /// <param name="id">Snapshot identifier carried by the continuation token.</param>
    /// <param name="scope">Listing identity the snapshot must belong to.</param>
    /// <param name="items">Receives the sorted items when the snapshot is still cached.</param>
    /// <returns><see langword="true"/> when a matching, unexpired snapshot was found.</returns>
    public bool TryGet(string id, string scope, out StorageItem[] items)
    {
        lock (_gate)
        {
            if (!_snapshots.TryGetValue(id, out var snapshot))
            {
                items = [];
                return false;
            }

            // A token minted for a different directory, connection, or recursion mode must never
            // resume this snapshot, even if identifiers somehow collide.
            if (!string.Equals(snapshot.Scope, scope, StringComparison.Ordinal) || IsExpired(snapshot))
            {
                Remove(id, snapshot);
                items = [];
                return false;
            }

            snapshot.LastUsed = ++_stamp;
            snapshot.LastUsedAt = _time.GetUtcNow();
            items = snapshot.Items;
            return true;
        }
    }

    /// <summary>Records the sorted listing for a pass and trims the cache back inside its bounds.</summary>
    /// <param name="id">Freshly minted snapshot identifier.</param>
    /// <param name="scope">Listing identity this snapshot belongs to.</param>
    /// <param name="items">Sorted, de-duplicated items for the whole listing.</param>
    public void Store(string id, string scope, StorageItem[] items)
    {
        // A listing larger than the whole budget would evict everything and still not fit, so it is
        // simply not cached; those passes re-walk per page exactly as they did before.
        if (items.Length > MaxItems) return;

        lock (_gate)
        {
            if (_snapshots.TryGetValue(id, out var existing)) Remove(id, existing);
            DropExpired();
            while (_snapshots.Count >= MaxSnapshots || _itemCount + items.Length > MaxItems)
            {
                if (!TryEvictOldest()) break;
            }

            _snapshots[id] = new Snapshot(scope, items, _time.GetUtcNow(), ++_stamp);
            _itemCount += items.Length;
        }
    }

    private bool IsExpired(Snapshot snapshot) => _time.GetUtcNow() - snapshot.LastUsedAt > IdleLifetime;

    private void Remove(string id, Snapshot snapshot)
    {
        _snapshots.Remove(id);
        _itemCount -= snapshot.Items.Length;
    }

    private void DropExpired()
    {
        var stale = _snapshots.Where(pair => IsExpired(pair.Value)).Select(pair => pair.Key).ToArray();
        foreach (var id in stale) Remove(id, _snapshots[id]);
    }

    private bool TryEvictOldest()
    {
        string? oldest = null;
        var oldestStamp = long.MaxValue;
        foreach (var pair in _snapshots)
        {
            if (pair.Value.LastUsed >= oldestStamp) continue;
            oldestStamp = pair.Value.LastUsed;
            oldest = pair.Key;
        }

        if (oldest is null) return false;
        Remove(oldest, _snapshots[oldest]);
        return true;
    }

    private sealed class Snapshot(string scope, StorageItem[] items, DateTimeOffset createdAt, long lastUsed)
    {
        public string Scope { get; } = scope;
        public StorageItem[] Items { get; } = items;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        /// <summary>Wall-clock time of the last read, used for idle expiry.</summary>
        public DateTimeOffset LastUsedAt { get; set; } = createdAt;
        /// <summary>Monotonic counter used to pick an eviction victim.</summary>
        public long LastUsed { get; set; } = lastUsed;
    }
}
