using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using CL.Common.Hashing;
using CL.Data.Errors;
using CL.Data.Events;
using CL.Data.Execution;
using CL.Data.Mapping;
using CL.Data.Schema;
using CL.Data.Sql;
using CodeLogic.Core.Results;
using SqlKit = CL.Data.Sql.Sql;

namespace CL.Data.Migrations;

/// <summary>A migration that is registered and not yet applied.</summary>
/// <param name="Version">Version.</param>
/// <param name="Description">Description.</param>
public sealed record PendingMigration(MigrationVersion Version, string Description);

/// <summary>A migration recorded in <c>cl_data_migrations</c>.</summary>
/// <param name="Id">Version as stored (<c>1.2.0/003</c>).</param>
/// <param name="Description">Description when applied.</param>
/// <param name="AppliedAt">When it was applied.</param>
/// <param name="ChecksumChanged">True when the registered migration's checksum differs from the recorded one (edited after release).</param>
/// <param name="Registered">False when no registered migration has this id.</param>
public sealed record AppliedMigration(string Id, string Description, DateTimeOffset AppliedAt, bool ChecksumChanged, bool Registered);

/// <summary>Result of a migrate or rollback run.</summary>
/// <param name="Applied">Versions applied (or rolled back), in the order they ran.</param>
/// <param name="ChecksumChanged">Applied migrations whose registered checksum no longer matches the recorded one.</param>
public sealed record MigrationRunResult(IReadOnlyList<MigrationVersion> Applied, IReadOnlyList<string> ChecksumChanged);

/// <summary>Options for a <see cref="MigrationRunner"/>.</summary>
public sealed record MigrationOptions
{
    /// <summary>Only migrations at or below this application version run (null: all).</summary>
    public string? AppVersion { get; init; }

    /// <summary>How long to wait for another process's migration run.</summary>
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Applies and rolls back migrations. Each migration runs in its own transaction together with its row in
/// <c>cl_data_migrations</c>, so where the database has transactional DDL a failed migration leaves no trace. Runs
/// hold an advisory lock, so one process migrates at a time; the applied set is re-read under the lock.
/// </summary>
public sealed class MigrationRunner
{
    /// <summary>Name of the table that records applied migrations.</summary>
    public const string HistoryTable = "cl_data_migrations";

    private const string LockName = "cl_data_migrations";
    private readonly Database _db;
    private readonly List<IMigration> _migrations;
    private readonly MigrationOptions _options;

    /// <summary>Creates a runner for <paramref name="migrations"/> on <paramref name="db"/>.</summary>
    public MigrationRunner(Database db, IEnumerable<IMigration> migrations, MigrationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(migrations);
        _db = db;
        _options = options ?? new MigrationOptions();
        _migrations = [.. migrations.OrderBy(m => m.Version)];
        var duplicate = _migrations.GroupBy(m => m.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Two migrations share version {duplicate.Key}: {string.Join(", ", duplicate.Select(m => m.GetType().Name))}.", nameof(migrations));
    }

    /// <summary>
    /// Every concrete <see cref="IMigration"/> in <paramref name="assembly"/> with a public parameterless
    /// constructor. Uses reflection; register migrations explicitly for trimmed or AOT apps.
    /// </summary>
    [RequiresUnreferencedCode("Scans the assembly for migration types; register migrations explicitly when trimming.")]
    public static IReadOnlyList<IMigration> FromAssembly(Assembly assembly) =>
        assembly.GetTypes()
            .Where(t => typeof(IMigration).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false } && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (IMigration)Activator.CreateInstance(t)!)
            .ToArray();

    /// <summary>Registered migrations not yet applied (within <see cref="MigrationOptions.AppVersion"/>), in apply order.</summary>
    public async ValueTask<Result<IReadOnlyList<PendingMigration>>> PendingAsync(CancellationToken ct = default)
    {
        var applied = await ReadHistoryAsync(ct).ConfigureAwait(false);
        if (applied.IsFailure) return applied.Error!;
        return Pending(applied.Value!).Select(m => new PendingMigration(m.Version, m.Description)).ToArray();
    }

    /// <summary>Migrations recorded as applied, oldest first, with checksum checks against the registered ones.</summary>
    public async ValueTask<Result<IReadOnlyList<AppliedMigration>>> AppliedAsync(CancellationToken ct = default)
    {
        var applied = await ReadHistoryAsync(ct).ConfigureAwait(false);
        if (applied.IsFailure) return applied.Error!;
        var registered = _migrations.ToDictionary(m => m.Version.ToString(), StringComparer.Ordinal);
        return applied.Value!.Values
            .OrderBy(r => MigrationVersion.Parse(r.Id))
            .Select(r => new AppliedMigration(r.Id, r.Description, r.AppliedAt,
                registered.TryGetValue(r.Id, out var m) && Checksum(m) != r.Checksum, registered.ContainsKey(r.Id)))
            .ToArray();
    }

    /// <summary>Applies every pending migration in order. Stops at the first failure (earlier ones stay applied).</summary>
    public async ValueTask<Result<MigrationRunResult>> MigrateAsync(CancellationToken ct = default)
    {
        var locked = await LockAsync(ct).ConfigureAwait(false);
        if (locked.IsFailure) return locked.Error!;
        await using var held = locked.Value!;

        var history = await ReadHistoryAsync(ct).ConfigureAwait(false);   // under the lock: a peer may just have finished
        if (history.IsFailure) return history.Error!;

        List<MigrationVersion> applied = [];
        foreach (var migration in Pending(history.Value!))
        {
            var started = Stopwatch.GetTimestamp();
            var ran = await RunAsync(migration, up: true, ct).ConfigureAwait(false);
            if (ran.IsFailure) return ran.Error!;
            applied.Add(migration.Version);
            _db.Publish(new MigrationAppliedEvent(_db.Name, _db.Provider.Name, _db.Options.Time.GetUtcNow(),
                migration.Version.ToString(), migration.Description, false, Stopwatch.GetElapsedTime(started)));
        }
        return new MigrationRunResult(applied, ChangedChecksums(history.Value!));
    }

    /// <summary>
    /// Rolls back every applied migration newer than <paramref name="target"/>, newest first. Refuses before changing
    /// anything when one of them cannot roll back.
    /// </summary>
    public async ValueTask<Result<MigrationRunResult>> RollbackAsync(MigrationVersion target, CancellationToken ct = default)
    {
        var locked = await LockAsync(ct).ConfigureAwait(false);
        if (locked.IsFailure) return locked.Error!;
        await using var held = locked.Value!;

        var history = await ReadHistoryAsync(ct).ConfigureAwait(false);
        if (history.IsFailure) return history.Error!;
        var toRevert = _migrations.Where(m => m.Version > target && history.Value!.ContainsKey(m.Version.ToString())).Reverse().ToArray();
        var blocked = toRevert.Where(m => !m.CanRollback).Select(m => m.Version.ToString()).ToArray();
        if (blocked.Length > 0)
            return DataErrors.Unsupported($"Rollback to {target} needs migrations that cannot roll back: {string.Join(", ", blocked)}.");

        List<MigrationVersion> reverted = [];
        foreach (var migration in toRevert)
        {
            var started = Stopwatch.GetTimestamp();
            var ran = await RunAsync(migration, up: false, ct).ConfigureAwait(false);
            if (ran.IsFailure) return ran.Error!;
            reverted.Add(migration.Version);
            _db.Publish(new MigrationAppliedEvent(_db.Name, _db.Provider.Name, _db.Options.Time.GetUtcNow(),
                migration.Version.ToString(), migration.Description, true, Stopwatch.GetElapsedTime(started)));
        }
        return new MigrationRunResult(reverted, ChangedChecksums(history.Value!));
    }

    // ── Running one migration ───────────────────────────────────────────────

    private async ValueTask<Result> RunAsync(IMigration migration, bool up, CancellationToken ct)
    {
        var id = migration.Version.ToString();
        var ddl = _db.Provider.Ddl;
        var begun = await DataTransaction.BeginAsync(_db, null, ct, ddl.SessionSetup, ddl.SessionTeardown).ConfigureAwait(false);
        if (begun.IsFailure) return DataErrors.MigrationFailed(id, "could not start the transaction", begun.Error);
        await using var tx = begun.Value!;
        var context = new MigrationContext(tx.Db);

        Result outcome;
        try
        {
            outcome = up ? await migration.UpAsync(context, ct).ConfigureAwait(false) : await migration.DownAsync(context, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DataErrors.MigrationFailed(id, $"{ex.GetType().Name}: {ex.Message}");
        }
        if (outcome.IsFailure) return DataErrors.MigrationFailed(id, outcome.Error!.Message, outcome.Error);

        if (ddl.IntegrityCheckSql is { } check)
        {
            var violations = await tx.Db.SqlReadAsync(SqlText.Raw(check), static _ => 0, SqlIntent.ReadPrimary, ct).ConfigureAwait(false);
            if (violations.IsFailure) return DataErrors.MigrationFailed(id, "integrity check failed", violations.Error);
            if (violations.Value!.Count > 0) return DataErrors.MigrationFailed(id, "rows violate a foreign key after the migration; it was rolled back");
        }

        var recorded = up
            ? await tx.Db.ExecuteSqlAsync(
                $"INSERT INTO {History()} ({SqlKit.Identifier("id")}, {SqlKit.Identifier("description")}, {SqlKit.Identifier("checksum")}, {SqlKit.Identifier("applied_at")}) VALUES ({id}, {Truncate(migration.Description, 500)}, {Checksum(migration)}, {_db.Options.Time.GetUtcNow()})",
                ct).ConfigureAwait(false)
            : await tx.Db.ExecuteSqlAsync($"DELETE FROM {History()} WHERE {SqlKit.Identifier("id")} = {id}", ct).ConfigureAwait(false);
        if (recorded.IsFailure) return DataErrors.MigrationFailed(id, "could not record the migration", recorded.Error);

        var committed = await tx.CommitAsync(ct).ConfigureAwait(false);
        return committed.IsFailure ? DataErrors.MigrationFailed(id, "commit failed", committed.Error) : Result.Success();
    }

    // ── History ─────────────────────────────────────────────────────────────

    private sealed record HistoryRow(string Id, string Description, string Checksum, DateTimeOffset AppliedAt);

    private async ValueTask<Result<Dictionary<string, HistoryRow>>> ReadHistoryAsync(CancellationToken ct)
    {
        var ensured = await EnsureHistoryAsync(ct).ConfigureAwait(false);
        if (ensured.IsFailure) return ensured.Error!;
        var rows = await _db.SqlReadAsync(
            $"SELECT {SqlKit.Identifier("id")}, {SqlKit.Identifier("description")}, {SqlKit.Identifier("checksum")}, {SqlKit.Identifier("applied_at")} FROM {History()}",
            static r => new HistoryRow(r.GetString(0), r.GetString(1), r.GetString(2), ValueReader<DateTimeOffset>.Read(r, 3)), SqlIntent.ReadPrimary, ct).ConfigureAwait(false);
        if (rows.IsFailure) return rows.Error!;
        return rows.Value!.ToDictionary(r => r.Id, StringComparer.Ordinal);
    }

    private async ValueTask<Result> EnsureHistoryAsync(CancellationToken ct)
    {
        var ensured = await SystemTables.EnsureAsync(_db, HistoryDefinition(), ct).ConfigureAwait(false);
        return ensured.IsFailure ? ensured.Error! : Result.Success();
    }

    private TableSchema HistoryDefinition() => SystemTables.Table(_db, HistoryTable,
    [
        SystemTables.Column(_db, "id", typeof(string), DataType.String, 64),
        SystemTables.Column(_db, "description", typeof(string), DataType.String, 500),
        SystemTables.Column(_db, "checksum", typeof(string), DataType.String, 32),
        SystemTables.Column(_db, "applied_at", typeof(DateTimeOffset), DataType.DateTimeOffset),
    ], ["id"]);

    private SqlIdentifier History() => SqlKit.Identifier(HistoryTable);

    private ValueTask<Result<IAsyncDisposable>> LockAsync(CancellationToken ct) => SystemTables.LockAsync(_db, LockName, _options.LockTimeout, ct);

    private IEnumerable<IMigration> Pending(Dictionary<string, HistoryRow> applied) =>
        _migrations.Where(m => !applied.ContainsKey(m.Version.ToString()) && (_options.AppVersion is not { } app || m.Version.IsAtOrBelow(app)));

    private string[] ChangedChecksums(Dictionary<string, HistoryRow> applied) =>
        _migrations.Where(m => applied.TryGetValue(m.Version.ToString(), out var row) && row.Checksum != Checksum(m))
            .Select(m => m.Version.ToString()).ToArray();

    /// <summary>XxHash128 of version and description.</summary>
    internal static string Checksum(IMigration migration) =>
        FastHash.ToHex(FastHash.XxHash128($"{migration.Version}|{migration.Description}".AsSpan()));

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

}
