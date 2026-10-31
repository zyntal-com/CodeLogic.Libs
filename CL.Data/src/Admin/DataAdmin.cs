using CL.Data.Errors;
using CL.Data.Execution;
using CL.Data.Mapping;
using CL.Data.Sql;
using CodeLogic.Core.Results;

namespace CL.Data.Admin;

/// <summary>Administration of one connection: databases and tables (<c>db.Admin</c>).</summary>
public sealed class DataAdmin
{
    private readonly Database _db;

    internal DataAdmin(Database db) => _db = db;

    private DatabaseAdmin? Server => _db.Provider.DatabaseAdmin;

    // ── Databases ───────────────────────────────────────────────────────────

    /// <summary>The database this connection points at.</summary>
    public Result<string> DatabaseName => Server is { } admin ? admin.ConfiguredDatabase(_db.Provider, _db.Config) : Unsupported();

    /// <summary>True when <paramref name="database"/> (default: the configured one) exists.</summary>
    public async ValueTask<Result<bool>> DatabaseExistsAsync(string? database = null, CancellationToken ct = default)
    {
        if (Server is not { } admin) return Unsupported();
        var name = database ?? DatabaseName;
        return name.IsFailure ? name.Error! : await admin.ExistsAsync(_db.Provider, _db.Config, name.Value!, ct).ConfigureAwait(false);
    }

    /// <summary>Creates the configured database when it is missing; true when it was created.</summary>
    public async ValueTask<Result<bool>> EnsureDatabaseAsync(CancellationToken ct = default)
    {
        var exists = await DatabaseExistsAsync(null, ct).ConfigureAwait(false);
        if (exists.IsFailure) return exists.Error!;
        if (exists.Value) return false;
        var created = await CreateDatabaseAsync(DatabaseName.Value!, ct).ConfigureAwait(false);
        return created.IsFailure ? created.Error! : true;
    }

    /// <summary>Creates <paramref name="database"/> on the same server (a no-op when it exists).</summary>
    public ValueTask<Result> CreateDatabaseAsync(string database, CancellationToken ct = default) =>
        Server is { } admin ? admin.CreateAsync(_db.Provider, _db.Config, database, ct) : ValueTask.FromResult<Result>(Unsupported());

    /// <summary>
    /// Drops <paramref name="database"/>, closing other sessions where the server allows it. Name the database
    /// explicitly, even the configured one: there is no default here.
    /// </summary>
    public ValueTask<Result> DropDatabaseAsync(string database, CancellationToken ct = default) =>
        Server is { } admin ? admin.DropAsync(_db.Provider, _db.Config, database, ct) : ValueTask.FromResult<Result>(Unsupported());

    // ── Tables ──────────────────────────────────────────────────────────────

    /// <summary>Tables in <paramref name="schema"/> (default: the connection's).</summary>
    public ValueTask<Result<IReadOnlyList<string>>> ListTablesAsync(string? schema = null, CancellationToken ct = default) =>
        _db.Provider.Introspector is { } introspector ? introspector.ListTablesAsync(_db, schema, ct) : ValueTask.FromResult<Result<IReadOnlyList<string>>>(Unsupported());

    /// <summary>True when the table exists.</summary>
    public async ValueTask<Result<bool>> TableExistsAsync(string table, string? schema = null, CancellationToken ct = default)
    {
        if (_db.Provider.Introspector is not { } introspector) return Unsupported();
        var read = await introspector.ReadTableAsync(_db, table, schema, ct).ConfigureAwait(false);
        return read.IsFailure ? read.Error! : read.Value is not null;
    }

    /// <summary>True when the table of <typeparamref name="T"/> exists.</summary>
    public async ValueTask<Result<bool>> TableExistsAsync<T>(CancellationToken ct = default)
    {
        var target = Target<T>();
        return target.IsFailure ? target.Error! : await TableExistsAsync(target.Value.Table, target.Value.Schema, ct).ConfigureAwait(false);
    }

    /// <summary>Drops the table if it exists.</summary>
    public async ValueTask<Result> DropTableAsync(string table, string? schema = null, CancellationToken ct = default)
    {
        var dropped = await _db.ExecuteSqlAsync(SqlText.Raw("DROP TABLE IF EXISTS " + _db.Provider.Dialect.QuoteTable(table, schema)), ct).ConfigureAwait(false);
        if (dropped.IsFailure) return dropped.Error!;
        // The schema sync records tables by their schema-qualified name; forget this one so the next sync recreates it.
        var syncedSchema = _db.Provider.Capabilities.Schemas ? schema ?? _db.Provider.GetDefaultSchema(_db.Config) : null;
        return await _db.Schema.ForgetAsync(table, syncedSchema, ct).ConfigureAwait(false);
    }

    /// <summary>Drops the table of <typeparamref name="T"/> if it exists.</summary>
    public async ValueTask<Result> DropTableAsync<T>(CancellationToken ct = default)
    {
        var target = Target<T>();
        return target.IsFailure ? target.Error! : await DropTableAsync(target.Value.Table, target.Value.Schema, ct).ConfigureAwait(false);
    }

    /// <summary>Removes every row and resets identity counters where the database does (TRUNCATE; DELETE on SQLite).</summary>
    public async ValueTask<Result> TruncateAsync(string table, string? schema = null, CancellationToken ct = default)
    {
        var done = await _db.ExecuteSqlAsync(SqlText.Raw(_db.Provider.Dialect.RenderTruncate(_db.Provider.Dialect.QuoteTable(table, schema))), ct).ConfigureAwait(false);
        return done.IsFailure ? done.Error! : Result.Success();
    }

    /// <summary>Removes every row of <typeparamref name="T"/>'s table.</summary>
    public async ValueTask<Result> TruncateAsync<T>(CancellationToken ct = default)
    {
        var target = Target<T>();
        return target.IsFailure ? target.Error! : await TruncateAsync(target.Value.Table, target.Value.Schema, ct).ConfigureAwait(false);
    }

    /// <summary>The server's version text (for example <c>8.4.3</c>, <c>18.0</c>, <c>16.0.4135.4</c>, <c>3.46.1</c>).</summary>
    public async ValueTask<Result<string>> ServerVersionAsync(CancellationToken ct = default)
    {
        var version = await _db.SqlScalarAsync<string>(SqlText.Raw(_db.Provider.ServerVersionSql), ct).ConfigureAwait(false);
        return version.IsFailure ? version.Error! : version.Value ?? "";
    }

    /// <summary>The quoted (and schema-qualified) table of <typeparamref name="T"/>, for provider-specific statements.</summary>
    public Result<string> QuotedTable<T>()
    {
        var target = Target<T>();
        return target.IsFailure ? target.Error! : _db.Provider.Dialect.QuoteTable(target.Value.Table, target.Value.Schema);
    }

    private Result<(string Table, string? Schema)> Target<T>()
    {
        var accessor = EntityAccessorRegistry.Get<T>();
        if (accessor.IsFailure) return accessor.Error!;
        var map = accessor.Value!.Map;
        if (!map.IsTable) return DataErrors.Unsupported($"'{map.ClrType.Name}' is a [DataRow] type and has no table.");
        var schema = _db.Provider.Capabilities.Schemas ? map.Schema ?? _db.Provider.GetDefaultSchema(_db.Config) : null;
        return (map.GetNames(_db.Config.NamingPolicy).Table, schema);
    }

    private Error Unsupported() => DataErrors.Unsupported($"{_db.Provider.Name} does not support this administration operation.");
}
