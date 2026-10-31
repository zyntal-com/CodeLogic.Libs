using System.Collections.Concurrent;
using CL.Data.Execution;
using CL.Data.Schema;
using CL.Data.Sql;
using CodeLogic.Core.Results;
using SqlKit = CL.Data.Sql.Sql;

namespace CL.Data.Caching;

/// <summary>
/// Carries cache invalidations between nodes, so a write on one server drops the stale results cached on the others.
/// Tags include the connection name, so every node must use the same connection names.
/// </summary>
public interface ICacheInvalidationBus : IAsyncDisposable
{
    /// <summary>Sends <paramref name="tags"/> to the other nodes.</summary>
    ValueTask PublishAsync(IReadOnlyList<string> tags, CancellationToken ct = default);

    /// <summary>Calls <paramref name="onRemote"/> with invalidations published by other nodes.</summary>
    void Subscribe(Action<IReadOnlyList<string>> onRemote);
}

/// <summary>Links caches in one process (several databases or hosts sharing a channel name).</summary>
/// <param name="channel">Channel name; buses with the same name hear each other.</param>
public sealed class InProcessCacheBus(string channel = "default") : ICacheInvalidationBus
{
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<InProcessCacheBus, byte>> Channels = new();
    private Action<IReadOnlyList<string>>? _handler;

    /// <inheritdoc />
    public ValueTask PublishAsync(IReadOnlyList<string> tags, CancellationToken ct = default)
    {
        if (Channels.TryGetValue(channel, out var members))
            foreach (var member in members.Keys)
                if (!ReferenceEquals(member, this)) member._handler?.Invoke(tags);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void Subscribe(Action<IReadOnlyList<string>> onRemote)
    {
        _handler = onRemote;
        Channels.GetOrAdd(channel, _ => new()).TryAdd(this, 0);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Channels.TryGetValue(channel, out var members)) members.TryRemove(this, out _);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Cross-node invalidation through a table (<c>cl_data_cache_bus</c>) in a shared database: no extra infrastructure. Each
/// published invalidation is a row; every node polls for rows written by the others (<see cref="PollAsync"/>, run by
/// <see cref="Start"/>), and old rows are pruned. Use a database without its own cache for the bus.
/// </summary>
public sealed class DatabaseCacheBus : ICacheInvalidationBus
{
    /// <summary>Name of the bus table.</summary>
    public const string Table = "cl_data_cache_bus";

    private const int MaxTagsLength = 4000;
    private readonly Database _db;
    private readonly TimeSpan _retention;
    private Action<IReadOnlyList<string>>? _handler;
    private long _lastSeen = -1;
    private int _polls;
    private bool _ensured;
    private IDisposable? _job;

    /// <summary>A bus over <paramref name="db"/>; rows older than <paramref name="retention"/> (default 10 minutes) are pruned.</summary>
    public DatabaseCacheBus(Database db, TimeSpan? retention = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
        _retention = retention ?? TimeSpan.FromMinutes(10);
    }

    /// <summary>This node's identity in the bus table.</summary>
    public string Node { get; } = Guid.NewGuid().ToString("N");

    /// <inheritdoc />
    public void Subscribe(Action<IReadOnlyList<string>> onRemote) => _handler = onRemote;

    /// <inheritdoc />
    public async ValueTask PublishAsync(IReadOnlyList<string> tags, CancellationToken ct = default)
    {
        if (tags.Count == 0 || (await EnsureAsync(ct).ConfigureAwait(false)).IsFailure) return;
        var text = string.Join('\n', tags);
        if (text.Length > MaxTagsLength) text = tags[0][..(tags[0].IndexOf('|') + 1)] + "*";   // too many tags: drop the whole connection
        await _db.ExecuteSqlAsync(
            $"INSERT INTO {SqlKit.Identifier(Table)} ({SqlKit.Identifier("node")}, {SqlKit.Identifier("tags")}, {SqlKit.Identifier("created_at")}) VALUES ({Node}, {text}, {_db.Options.Time.GetUtcNow()})",
            ct, invalidate: false).ConfigureAwait(false);
    }

    /// <summary>Reads invalidations published by other nodes since the last poll and applies them. Returns how many rows were applied.</summary>
    public async ValueTask<Result<int>> PollAsync(CancellationToken ct = default)
    {
        var ensured = await EnsureAsync(ct).ConfigureAwait(false);
        if (ensured.IsFailure) return ensured.Error!;
        if (_lastSeen < 0)
        {
            // Start after what is already there: older invalidations happened before this node cached anything.
            var max = await _db.SqlScalarAsync<long?>($"SELECT MAX({SqlKit.Identifier("id")}) FROM {SqlKit.Identifier(Table)}", ct).ConfigureAwait(false);
            if (max.IsFailure) return max.Error!;
            _lastSeen = max.Value ?? 0;
            return 0;
        }

        var rows = await _db.SqlReadAsync(
            $"SELECT {SqlKit.Identifier("id")}, {SqlKit.Identifier("node")}, {SqlKit.Identifier("tags")} FROM {SqlKit.Identifier(Table)} WHERE {SqlKit.Identifier("id")} > {_lastSeen} ORDER BY {SqlKit.Identifier("id")}",
            static r => (Id: Convert.ToInt64(r.GetValue(0), System.Globalization.CultureInfo.InvariantCulture), Node: r.GetString(1), Tags: r.GetString(2)), ct).ConfigureAwait(false);
        if (rows.IsFailure) return rows.Error!;

        var applied = 0;
        foreach (var row in rows.Value!)
        {
            _lastSeen = Math.Max(_lastSeen, row.Id);
            if (row.Node == Node) continue;
            _handler?.Invoke(row.Tags.Split('\n'));
            applied++;
        }

        if (++_polls % 100 == 0)
            await _db.ExecuteSqlAsync($"DELETE FROM {SqlKit.Identifier(Table)} WHERE {SqlKit.Identifier("created_at")} < {_db.Options.Time.GetUtcNow() - _retention}",
                ct, invalidate: false).ConfigureAwait(false);
        return applied;
    }

    /// <summary>Polls on <paramref name="scheduler"/> every <paramref name="interval"/> (default 500 ms).</summary>
    public void Start(DataScheduler scheduler, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        _job?.Dispose();
        _job = scheduler.Every("cache-bus-" + Node, interval ?? TimeSpan.FromMilliseconds(500), async ct => await PollAsync(ct).ConfigureAwait(false));
    }

    private async ValueTask<Result<bool>> EnsureAsync(CancellationToken ct)
    {
        if (_ensured) return false;
        var table = SystemTables.Table(_db, Table,
        [
            SystemTables.Column(_db, "id", typeof(long), DataType.BigInt) with { Identity = true },
            SystemTables.Column(_db, "node", typeof(string), DataType.String, 64),
            SystemTables.Column(_db, "tags", typeof(string), DataType.String, MaxTagsLength),
            SystemTables.Column(_db, "created_at", typeof(DateTimeOffset), DataType.DateTimeOffset),
        ], ["id"]);
        var ensured = await SystemTables.EnsureAsync(_db, table, ct).ConfigureAwait(false);
        if (ensured.IsSuccess) _ensured = true;
        return ensured;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _job?.Dispose();
        return ValueTask.CompletedTask;
    }
}
