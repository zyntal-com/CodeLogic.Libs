using System.Globalization;
using CL.Data.Errors;
using CL.Data.Execution;
using CL.Data.Mapping;
using CL.Data.Providers;
using CL.Data.Schema;
using CL.Data.Sql;
using CodeLogic.Core.Results;

namespace CL.Data.Migrations;

/// <summary>
/// Orders migrations: by application version (semantic, <c>1.10.0</c> after <c>1.9.0</c>), then by order within it.
/// Written as <c>1.2.0/003</c>, which is also the id stored in <c>cl_data_migrations</c>.
/// </summary>
/// <param name="AppVersion">Application version the migration ships with.</param>
/// <param name="Order">Order within that version.</param>
public readonly record struct MigrationVersion(string AppVersion, int Order) : IComparable<MigrationVersion>
{
    /// <inheritdoc />
    public int CompareTo(MigrationVersion other)
    {
        var byVersion = CompareSemVer(AppVersion, other.AppVersion);
        return byVersion != 0 ? byVersion : Order.CompareTo(other.Order);
    }

    /// <summary>True when this version ships with <paramref name="appVersion"/> or earlier.</summary>
    public bool IsAtOrBelow(string appVersion) => CompareSemVer(AppVersion, appVersion) <= 0;

    /// <summary>Parses <c>1.2.0/003</c>.</summary>
    public static MigrationVersion Parse(string text)
    {
        var slash = text.LastIndexOf('/');
        if (slash <= 0) throw new FormatException($"'{text}' is not a migration version (expected 'appVersion/order').");
        return new MigrationVersion(text[..slash], int.Parse(text[(slash + 1)..], CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{AppVersion}/{Order:D3}");

    /// <summary>Compares the numeric parts of two versions; pre-release and build suffixes are ignored.</summary>
    public static int CompareSemVer(string a, string b)
    {
        static int[] Parse(string s) =>
            (s ?? "0").Split('-', '+')[0].Split('.').Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0).ToArray();

        var pa = Parse(a);
        var pb = Parse(b);
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    /// <summary>Less than.</summary>
    public static bool operator <(MigrationVersion left, MigrationVersion right) => left.CompareTo(right) < 0;
    /// <summary>Greater than.</summary>
    public static bool operator >(MigrationVersion left, MigrationVersion right) => left.CompareTo(right) > 0;
    /// <summary>Less than or equal.</summary>
    public static bool operator <=(MigrationVersion left, MigrationVersion right) => left.CompareTo(right) <= 0;
    /// <summary>Greater than or equal.</summary>
    public static bool operator >=(MigrationVersion left, MigrationVersion right) => left.CompareTo(right) >= 0;
}

/// <summary>A versioned schema or data change.</summary>
public interface IMigration
{
    /// <summary>Version (ordering and id).</summary>
    MigrationVersion Version { get; }

    /// <summary>What the migration does. Part of its checksum, so editing it after release is detected.</summary>
    string Description { get; }

    /// <summary>True when <see cref="DownAsync"/> is implemented.</summary>
    bool CanRollback { get; }

    /// <summary>Applies the migration. Runs in a transaction; a failure rolls it back.</summary>
    ValueTask<Result> UpAsync(IMigrationContext context, CancellationToken ct);

    /// <summary>Reverts the migration.</summary>
    ValueTask<Result> DownAsync(IMigrationContext context, CancellationToken ct);
}

/// <summary>Base class for migrations. Override <see cref="DownAsync"/> and <see cref="CanRollback"/> to support rollback.</summary>
/// <param name="appVersion">Application version the migration ships with.</param>
/// <param name="order">Order within that version.</param>
/// <param name="description">What the migration does.</param>
public abstract class Migration(string appVersion, int order, string description) : IMigration
{
    /// <inheritdoc />
    public MigrationVersion Version { get; } = new(appVersion, order);

    /// <inheritdoc />
    public string Description { get; } = description;

    /// <inheritdoc />
    public virtual bool CanRollback => false;

    /// <inheritdoc />
    public abstract ValueTask<Result> UpAsync(IMigrationContext context, CancellationToken ct);

    /// <inheritdoc />
    public virtual ValueTask<Result> DownAsync(IMigrationContext context, CancellationToken ct) =>
        ValueTask.FromResult<Result>(DataErrors.Unsupported($"Migration {Version} ({GetType().Name}) does not support rollback."));
}

/// <summary>What a migration can do. Everything runs inside the migration's transaction.</summary>
public interface IMigrationContext
{
    /// <summary>The database, bound to the migration's transaction: repositories, queries and raw SQL all join it.</summary>
    Database Db { get; }

    /// <summary>The provider (for provider-specific SQL).</summary>
    DataProvider Provider { get; }

    /// <summary>Runs a statement; interpolated values become parameters.</summary>
    ValueTask<Result<int>> ExecuteAsync(SqlText sql, CancellationToken ct = default);

    /// <summary>Applies one schema change through the provider's DDL generator.</summary>
    ValueTask<Result> ApplyAsync(SchemaChange change, CancellationToken ct = default);

    /// <summary>Reads a table from the catalog (inside the transaction, so earlier changes are visible where DDL is transactional).</summary>
    ValueTask<Result<TableSchema?>> ReadTableAsync(string table, string? schema = null, CancellationToken ct = default);

    /// <summary>Brings the table of <typeparamref name="T"/> in line with its model at <paramref name="level"/> and returns what was planned.</summary>
    ValueTask<Result<SchemaPlan>> SyncTableAsync<T>(SchemaSyncLevel level = SchemaSyncLevel.Safe, CancellationToken ct = default);
}

internal sealed class MigrationContext(Database db) : IMigrationContext
{
    public Database Db { get; } = db;

    public DataProvider Provider => Db.Provider;

    public ValueTask<Result<int>> ExecuteAsync(SqlText sql, CancellationToken ct = default) => Db.ExecuteSqlAsync(sql, ct);

    public async ValueTask<Result> ApplyAsync(SchemaChange change, CancellationToken ct = default)
    {
        foreach (var statement in Provider.Ddl.Generate(change))
        {
            var executed = await Db.ExecuteSqlAsync(SqlText.Raw(statement), ct).ConfigureAwait(false);
            if (executed.IsFailure) return DataErrors.SchemaSyncFailed(change.Table, $"{change.Describe()}: {statement}", executed.Error);
        }
        return Result.Success();
    }

    public ValueTask<Result<TableSchema?>> ReadTableAsync(string table, string? schema = null, CancellationToken ct = default) =>
        Provider.Introspector is { } introspector
            ? introspector.ReadTableAsync(Db, table, schema, ct)
            : ValueTask.FromResult<Result<TableSchema?>>(DataErrors.Unsupported($"{Provider.Name} has no schema introspector."));

    public async ValueTask<Result<SchemaPlan>> SyncTableAsync<T>(SchemaSyncLevel level = SchemaSyncLevel.Safe, CancellationToken ct = default)
    {
        var accessor = EntityAccessorRegistry.Get<T>();
        if (accessor.IsFailure) return accessor.Error!;
        var desired = SchemaBuilder.FromMap(accessor.Value!.Map, Provider, Db.Config);
        if (desired.IsFailure) return desired.Error!;
        var actual = await ReadTableAsync(desired.Value!.Name, desired.Value.Schema, ct).ConfigureAwait(false);
        if (actual.IsFailure) return actual.Error!;
        var plan = SchemaDiff.Compare(desired.Value, actual.Value, level);
        foreach (var change in plan.Apply)
        {
            var applied = await ApplyAsync(change, ct).ConfigureAwait(false);
            if (applied.IsFailure) return applied.Error!;
        }
        return plan;
    }
}
