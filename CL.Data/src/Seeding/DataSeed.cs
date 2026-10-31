using CL.Data.Errors;
using CL.Data.Execution;
using CL.Data.Schema;
using CL.Data.Sql;
using CodeLogic.Core.Results;
using SqlKit = CL.Data.Sql.Sql;

namespace CL.Data.Seeding;

/// <summary>
/// Reference or starter data that must exist (roles, countries, a first admin). A seed runs once, and again when its
/// <see cref="Version"/> grows; write it to be idempotent (upserts), so re-running is safe.
/// </summary>
public interface IDataSeed
{
    /// <summary>Stable identity, recorded in <c>cl_data_seeds</c>.</summary>
    string Id { get; }

    /// <summary>Run order (lower first; ties by id).</summary>
    int Order { get; }

    /// <summary>Bump to run the seed again.</summary>
    int Version { get; }

    /// <summary>Writes the data. <paramref name="db"/> is bound to the seed's transaction.</summary>
    ValueTask<Result> SeedAsync(Database db, CancellationToken ct);
}

/// <summary>Base class for seeds.</summary>
/// <param name="id">Stable identity.</param>
/// <param name="order">Run order.</param>
/// <param name="version">Version; bump to run again.</param>
public abstract class DataSeed(string id, int order = 0, int version = 1) : IDataSeed
{
    /// <inheritdoc />
    public string Id { get; } = id;

    /// <inheritdoc />
    public int Order { get; } = order;

    /// <inheritdoc />
    public int Version { get; } = version;

    /// <inheritdoc />
    public abstract ValueTask<Result> SeedAsync(Database db, CancellationToken ct);

    /// <summary>A seed that upserts <paramref name="rows"/> (by primary key): the rows exist with these values afterwards.</summary>
    public static IDataSeed Rows<T>(string id, IReadOnlyList<T> rows, int order = 0, int version = 1) => new RowsSeed<T>(id, rows, order, version);

    private sealed class RowsSeed<T>(string id, IReadOnlyList<T> rows, int order, int version) : DataSeed(id, order, version)
    {
        public override async ValueTask<Result> SeedAsync(Database db, CancellationToken ct)
        {
            var upserted = await db.Repository<T>().UpsertManyAsync(rows, ct: ct).ConfigureAwait(false);
            return upserted.IsFailure ? upserted.Error! : Result.Success();
        }
    }
}

/// <summary>Result of a seed run.</summary>
/// <param name="Applied">Seeds that ran, in order.</param>
/// <param name="UpToDate">Seeds already applied at their current version.</param>
public sealed record SeedRunResult(IReadOnlyList<string> Applied, IReadOnlyList<string> UpToDate);

/// <summary>Options for a seed run.</summary>
public sealed record SeedOptions
{
    /// <summary>How long to wait for another process's seed run.</summary>
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Runs seeds that are new or have a higher version than recorded, in order. Each seed commits in one transaction with
/// its row in <c>cl_data_seeds</c>; runs take an advisory lock, so one process seeds at a time. Run it after schema sync.
/// </summary>
public sealed class SeedRunner
{
    /// <summary>Name of the table that records applied seeds.</summary>
    public const string HistoryTable = "cl_data_seeds";

    private const string LockName = "cl_data_seeds";
    private readonly Database _db;
    private readonly IReadOnlyList<IDataSeed> _seeds;
    private readonly SeedOptions _options;

    /// <summary>Creates a runner for <paramref name="seeds"/> on <paramref name="db"/>.</summary>
    public SeedRunner(Database db, IEnumerable<IDataSeed> seeds, SeedOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(seeds);
        _db = db;
        _options = options ?? new SeedOptions();
        _seeds = [.. seeds.OrderBy(s => s.Order).ThenBy(s => s.Id, StringComparer.Ordinal)];
        var duplicate = _seeds.GroupBy(s => s.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"Two seeds share the id '{duplicate.Key}'.", nameof(seeds));
    }

    /// <summary>Runs every seed that is new or has a higher version than recorded. Stops at the first failure.</summary>
    public async ValueTask<Result<SeedRunResult>> RunAsync(CancellationToken ct = default)
    {
        var locked = await SystemTables.LockAsync(_db, LockName, _options.LockTimeout, ct).ConfigureAwait(false);
        if (locked.IsFailure) return locked.Error!;
        await using var held = locked.Value!;

        var ensured = await SystemTables.EnsureAsync(_db, Definition(), ct).ConfigureAwait(false);
        if (ensured.IsFailure) return ensured.Error!;
        var recorded = await _db.SqlReadAsync($"SELECT {SqlKit.Identifier("id")}, {SqlKit.Identifier("version")} FROM {History()}",
            static r => (Id: r.GetString(0), Version: Convert.ToInt32(r.GetValue(1), System.Globalization.CultureInfo.InvariantCulture)), ct).ConfigureAwait(false);
        if (recorded.IsFailure) return recorded.Error!;
        var versions = recorded.Value!.ToDictionary(r => r.Id, r => r.Version, StringComparer.Ordinal);

        List<string> applied = [];
        List<string> upToDate = [];
        foreach (var seed in _seeds)
        {
            if (versions.TryGetValue(seed.Id, out var version) && version >= seed.Version)
            {
                upToDate.Add(seed.Id);
                continue;
            }
            var ran = await RunAsync(seed, ct).ConfigureAwait(false);
            if (ran.IsFailure) return ran.Error!;
            applied.Add(seed.Id);
        }
        return new SeedRunResult(applied, upToDate);
    }

    private async ValueTask<Result> RunAsync(IDataSeed seed, CancellationToken ct)
    {
        var outcome = await _db.InTransactionAsync(async (tx, token) =>
        {
            Result seeded;
            try
            {
                seeded = await seed.SeedAsync(tx.Db, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return DataErrors.Validation($"Seed '{seed.Id}' threw {ex.GetType().Name}.", ex.Message);
            }
            if (seeded.IsFailure) return seeded;

            var removed = await tx.Db.ExecuteSqlAsync($"DELETE FROM {History()} WHERE {SqlKit.Identifier("id")} = {seed.Id}", token).ConfigureAwait(false);
            if (removed.IsFailure) return removed.Error!;
            var inserted = await tx.Db.ExecuteSqlAsync(
                $"INSERT INTO {History()} ({SqlKit.Identifier("id")}, {SqlKit.Identifier("version")}, {SqlKit.Identifier("applied_at")}) VALUES ({seed.Id}, {seed.Version}, {_db.Options.Time.GetUtcNow()})",
                token).ConfigureAwait(false);
            return inserted.IsFailure ? inserted.Error! : Result.Success();
        }, ct: ct).ConfigureAwait(false);

        if (outcome.IsFailure) return DataErrors.Validation($"Seed '{seed.Id}' failed.", outcome.Error!.Message);
        return Result.Success();
    }

    private TableSchema Definition() => SystemTables.Table(_db, HistoryTable,
    [
        SystemTables.Column(_db, "id", typeof(string), DataType.String, 200),
        SystemTables.Column(_db, "version", typeof(int), DataType.Int),
        SystemTables.Column(_db, "applied_at", typeof(DateTimeOffset), DataType.DateTimeOffset),
    ], ["id"]);

    private static SqlIdentifier History() => SqlKit.Identifier(HistoryTable);
}
