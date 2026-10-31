using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CodeLogic.Core.Logging;

namespace CL.PostgreSQL.Services;

/// <summary>
/// A named group of cached queries that is kept warm by a background timer.
/// Queries opt in via <c>.SmartCache("poolName")</c>; on first execution they
/// register their refresh factory with the pool. Every <see cref="RefreshEvery"/>
/// the pool re-runs every registered factory and overwrites the cache entry,
/// so subsequent reads never block on the DB. Entries stay warm forever once
/// registered — the pool never evicts them.
/// </summary>
public sealed class SmartCachePool : IAsyncDisposable
{
    /// <summary>Pool name used by <c>.SmartCache(name)</c>. Case-insensitive.</summary>
    public string Name { get; }

    /// <summary>How often the pool re-runs every registered factory.</summary>
    public TimeSpan RefreshEvery { get; }

    /// <summary>Drop an entry after this many consecutive refresh ticks with no read. Default 10.</summary>
    public int MaxIdleFires { get; }

    private readonly ConcurrentDictionary<string, PoolEntry> _entries = new(StringComparer.Ordinal);
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private long _ticksFired;
    private long _ticksFailed;
    private DateTime _lastTickUtc;

    internal SmartCachePool(string name, TimeSpan refreshEvery, int maxIdleFires, ILogger? logger)
    {
        Name = name;
        RefreshEvery = refreshEvery;
        MaxIdleFires = Math.Max(1, maxIdleFires);
        _logger = logger;
    }

    internal void Start()
    {
        if (_loop is not null) return;
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _logger?.Info($"[PostgreSQL] SmartCachePool '{Name}' started (refreshEvery={RefreshEvery.TotalSeconds:0.#}s).");
    }

    /// <summary>
    /// Runs <paramref name="warmUp"/> as a fire-and-forget task so the pool's
    /// hot queries are populated before any user request hits them. Inside
    /// <paramref name="warmUp"/>, just call the queries that should be warm
    /// (with their normal <c>.SmartCache(...)</c> decoration) — they
    /// auto-register with this pool as they always do.
    /// <para>
    /// Exceptions are caught and logged so a slow or broken warm-up never
    /// crashes startup. The pool stays lazy if the warm-up fails: queries
    /// still register on their first real read.
    /// </para>
    /// </summary>
    public void WarmUp(Func<Task> warmUp)
    {
        if (warmUp is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await warmUp().ConfigureAwait(false);
                sw.Stop();
                _logger?.Info($"[PostgreSQL] SmartCachePool '{Name}' warm-up complete: {_entries.Count} entries primed in {sw.ElapsedMilliseconds}ms.");
            }
            catch (Exception ex)
            {
                _logger?.Warning($"[PostgreSQL] SmartCachePool '{Name}' warm-up faulted: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Registers a query in the pool's refresh list, or touches the existing
    /// entry's last-read timestamp + resets its idle counter. Called from
    /// terminal methods on the query builder when <c>.SmartCache(...)</c> is set.
    /// </summary>
    internal void RegisterOrTouch(
        string connectionId,
        string tableName,
        string sql,
        Dictionary<string, object?> parms,
        Func<CancellationToken, Task<object?>> refreshFactory)
    {
        var entryId = ComputeEntryId(connectionId, tableName, sql, parms);
        var entry = _entries.GetOrAdd(entryId, _ => new PoolEntry
        {
            EntryId       = entryId,
            ConnectionId  = connectionId,
            TableName     = tableName,
            Sql           = sql,
            Parms         = [with(parms)],
            RefreshFactory = refreshFactory,
        });
        Interlocked.Exchange(ref entry.LastReadUtcTicks, DateTime.UtcNow.Ticks);
        Interlocked.Exchange(ref entry.ConsecutiveIdleFires, 0);
    }

    /// <summary>
    /// Runs every registered factory once. Public so callers can warm the
    /// pool on startup (or trigger an out-of-schedule refresh after a deploy).
    /// </summary>
    public async Task RefreshNowAsync(CancellationToken ct = default)
    {
        await TickAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns <c>true</c> if this pool has any active refresh entries for the
    /// given table name. Used to suppress mutation-triggered cache invalidation
    /// for tables whose freshness is managed by the pool's background refresh.
    /// </summary>
    public bool HasEntriesForTable(string tableName) =>
        _entries.Values.Any(e => string.Equals(e.TableName, tableName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Snapshot of pool stats for diagnostics.</summary>
    public SmartCachePoolStats GetStats()
    {
        return new SmartCachePoolStats(
            Name,
            RefreshEvery,
            MaxIdleFires,
            _entries.Count,
            Interlocked.Read(ref _ticksFired),
            Interlocked.Read(ref _ticksFailed),
            _lastTickUtc);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        // First tick after one full interval — never block startup. Callers
        // wanting an immediate warm-up call RefreshNowAsync() explicitly.
        try { await Task.Delay(RefreshEvery, ct).ConfigureAwait(false); }
        catch (TaskCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Warning($"[PostgreSQL] SmartCachePool '{Name}' tick faulted: {ex.Message}");
            }

            try { await Task.Delay(RefreshEvery, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        _lastTickUtc = DateTime.UtcNow;
        Interlocked.Increment(ref _ticksFired);

        // Single-flight across nodes: only the lease holder hits the DB this tick. With a
        // shared cache store (Redis) the others read the entry the leader writes. The
        // single-node default coordinator always grants the lease, so behaviour is
        // unchanged off-cluster. Idle accounting below still runs on every node so unread
        // entries retire locally regardless of who refreshes.
        var isLeader = await QueryCache.Coordinator
            .TryAcquireRefreshLeaseAsync(Name, RefreshEvery, ct).ConfigureAwait(false);

        foreach (var kv in _entries)
        {
            if (ct.IsCancellationRequested) return;
            var entry = kv.Value;

            var idle = Interlocked.Increment(ref entry.ConsecutiveIdleFires);
            if (idle > MaxIdleFires)
            {
                _entries.TryRemove(kv);
                continue;
            }

            if (!isLeader) continue; // a peer owns the refresh this tick

            try
            {
                var result = await entry.RefreshFactory(ct).ConfigureAwait(false);
                if (result is null) continue;

                // Cache key is rebuilt each tick: table-version bumps from
                // mutations mean the old key is unreachable; we write to
                // the current key so the next read is warm.
                var cacheKey = QueryCache.BuildCacheKey(
                    entry.ConnectionId, entry.TableName, entry.Sql, entry.Parms);

                // If the cache key changed since our last write (the table
                // version was bumped by a mutation), evict the previous key
                // ourselves. The QueryCache.Invalidate path also sweeps by
                // tableName as a safety net, but this targeted eviction is
                // O(1) and avoids leaving orphans even on cache backends
                // that can't enumerate (e.g. Redis without SCAN).
                var lastKey = entry.LastCacheKey;
                if (lastKey is not null && !string.Equals(lastKey, cacheKey, StringComparison.Ordinal))
                    await QueryCache.EvictAsync(lastKey).ConfigureAwait(false);

                // TTL = 2x refresh interval. Survives one missed refresh
                // before falling back to cache-aside cold read.
                var ttl = TimeSpan.FromMilliseconds(RefreshEvery.TotalMilliseconds * 2);
                await QueryCache.SetDirectAsync(cacheKey, result, ttl, entry.TableName, ct)
                    .ConfigureAwait(false);

                entry.LastCacheKey = cacheKey;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _ticksFailed);
                _logger?.Warning($"[PostgreSQL] SmartCachePool '{Name}' refresh failed for {entry.TableName}/{entry.EntryId[..8]}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Marks a touched entry to reset its idle counter. Called by the
    /// query builder on every cache hit so an actively-read entry never
    /// drifts toward eviction.
    /// </summary>
    internal void Touch(string connectionId, string tableName, string sql, Dictionary<string, object?> parms)
    {
        var entryId = ComputeEntryId(connectionId, tableName, sql, parms);
        if (_entries.TryGetValue(entryId, out var entry))
        {
            Interlocked.Exchange(ref entry.LastReadUtcTicks, DateTime.UtcNow.Ticks);
            Interlocked.Exchange(ref entry.ConsecutiveIdleFires, 0);
        }
    }

    private static string ComputeEntryId(string connectionId, string tableName, string sql, Dictionary<string, object?> parms)
    {
        // Stable across table-version bumps (unlike cache keys). Identifies
        // a logical query within the pool so reads and refreshes refer to
        // the same entry.
        var sb = new StringBuilder();
        sb.Append(connectionId).Append('|').Append(tableName).Append('|').Append(sql);
        foreach (var kv in parms.OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append('|').Append(kv.Key).Append('=').Append(kv.Value is byte[] b ? Convert.ToHexString(b) : kv.Value?.ToString() ?? "NULL");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }
        _cts.Dispose();
        _entries.Clear();
    }

    private sealed class PoolEntry
    {
        public required string EntryId { get; init; }
        public required string ConnectionId { get; init; }
        public required string TableName { get; init; }
        public required string Sql { get; init; }
        public required Dictionary<string, object?> Parms { get; init; }
        public required Func<CancellationToken, Task<object?>> RefreshFactory { get; init; }
        public long LastReadUtcTicks;
        public int ConsecutiveIdleFires;

        /// <summary>
        /// Most recent cache key written by this entry's refresh tick.
        /// Used to evict orphans when the table-version bumps between
        /// ticks: tick N writes to key V<sub>N</sub>, a mutation bumps the
        /// version, tick N+1 writes to V<sub>N+1</sub> — we evict V<sub>N</sub>
        /// in the same tick so it doesn't linger in the cache store.
        /// </summary>
        public string? LastCacheKey;
    }
}

/// <summary>Diagnostic snapshot of a pool's state.</summary>
public sealed record SmartCachePoolStats(
    string Name,
    TimeSpan RefreshEvery,
    int MaxIdleFires,
    int EntryCount,
    long TicksFired,
    long TicksFailed,
    DateTime LastTickUtc);
