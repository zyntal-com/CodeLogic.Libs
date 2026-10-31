using CL.Data.Errors;
using CL.Data.Execution;
using CL.Data.Mapping;
using CL.Data.Sql;
using CodeLogic.Core.Results;

namespace CL.Data.Schema;

/// <summary>
/// CL.Data's own bookkeeping tables (<c>cl_data_schema</c>, <c>cl_data_migrations</c>, <c>cl_data_seeds</c>): their columns use
/// the provider's type mapping, and they are created on first use.
/// </summary>
internal static class SystemTables
{
    /// <summary>A NOT NULL column of <paramref name="clrType"/> as the provider maps it.</summary>
    public static ColumnSchema Column(Database db, string name, Type clrType, DataType dataType, int size = 0)
    {
        var provider = db.Provider;
        var store = provider.Types.GetStoreType(new ColumnMap { PropertyName = name, ClrType = clrType, DataType = dataType, Size = size }, db.Config);
        return new ColumnSchema(name, provider.NormalizeStoreType(store), false);
    }

    /// <summary>A system table definition in the connection's default schema.</summary>
    public static TableSchema Table(Database db, string name, IReadOnlyList<ColumnSchema> columns, IReadOnlyList<string> key) =>
        new(name, db.Provider.Capabilities.Schemas ? db.Provider.GetDefaultSchema(db.Config) : null, columns, key);

    /// <summary>Creates <paramref name="table"/> when it does not exist; true when it was created.</summary>
    public static async ValueTask<Result<bool>> EnsureAsync(Database db, TableSchema table, CancellationToken ct)
    {
        if (db.Provider.Introspector is not { } introspector) return DataErrors.Unsupported($"{db.Provider.Name} has no schema introspector.");
        var existing = await introspector.ReadTableAsync(db, table.Name, table.Schema, ct).ConfigureAwait(false);
        if (existing.IsFailure) return existing.Error!;
        if (existing.Value is not null) return false;
        foreach (var statement in db.Provider.Ddl.Generate(new CreateTableChange(table)))
        {
            var created = await db.ExecuteSqlAsync(SqlText.Raw(statement), ct).ConfigureAwait(false);
            if (created.IsFailure) return DataErrors.SchemaSyncFailed(table.Name, statement, created.Error);
        }
        return true;
    }

    /// <summary>The advisory lock <paramref name="name"/> where the provider has them, or a no-op handle.</summary>
    public static async ValueTask<Result<IAsyncDisposable>> LockAsync(Database db, string name, TimeSpan timeout, CancellationToken ct)
    {
        if (!db.Provider.Capabilities.AdvisoryLocks) return Result<IAsyncDisposable>.Success(NoLock.Instance);
        var acquired = await db.AcquireLockAsync(name, timeout, ct).ConfigureAwait(false);
        return acquired.IsFailure ? acquired.Error! : Result<IAsyncDisposable>.Success(acquired.Value!);
    }

    private sealed class NoLock : IAsyncDisposable
    {
        public static readonly NoLock Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
