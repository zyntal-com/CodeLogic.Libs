using CL.Data.Configuration;
using CL.Data.Errors;
using CL.Data.Providers;
using CL.Data.Sql;
using CodeLogic.Core.Results;

namespace CL.Data.Admin;

/// <summary>
/// Server-level database operations of a provider: does a database exist, create it, drop it. Server providers
/// derive from <see cref="ServerDatabaseAdmin"/>; SQLite works on files.
/// </summary>
public abstract class DatabaseAdmin
{
    /// <summary>The database the connection points at (name, or file path for SQLite).</summary>
    public abstract Result<string> ConfiguredDatabase(DataProvider provider, DataConnectionConfig config);

    /// <summary>True when <paramref name="database"/> exists.</summary>
    public abstract ValueTask<Result<bool>> ExistsAsync(DataProvider provider, DataConnectionConfig config, string database, CancellationToken ct);

    /// <summary>Creates <paramref name="database"/> (a no-op when it exists).</summary>
    public abstract ValueTask<Result> CreateAsync(DataProvider provider, DataConnectionConfig config, string database, CancellationToken ct);

    /// <summary>Drops <paramref name="database"/>, closing other sessions where the server allows it (a no-op when it is missing).</summary>
    public abstract ValueTask<Result> DropAsync(DataProvider provider, DataConnectionConfig config, string database, CancellationToken ct);
}

/// <summary>
/// Database administration through SQL on a server-level connection (<c>master</c>, <c>postgres</c>, or no
/// database on MySQL). Derived classes supply the connection-string rewrite and the statements.
/// </summary>
public abstract class ServerDatabaseAdmin : DatabaseAdmin
{
    /// <summary>The database to connect to for server-level statements (null: none).</summary>
    protected abstract string? ServerDatabase { get; }

    /// <summary>The database named in <paramref name="connectionString"/>.</summary>
    protected abstract string? DatabaseOf(string connectionString);

    /// <summary><paramref name="connectionString"/> pointing at <paramref name="database"/> (null: none).</summary>
    protected abstract string WithDatabase(string connectionString, string? database);

    /// <summary>A query returning a positive number when <paramref name="database"/> exists.</summary>
    protected abstract SqlText ExistsSql(string database);

    /// <summary>Statements that create <paramref name="database"/>.</summary>
    protected abstract IReadOnlyList<string> CreateSql(string database, DataConnectionConfig config);

    /// <summary>Statements that drop <paramref name="database"/> if it exists.</summary>
    protected abstract IReadOnlyList<string> DropSql(string database);

    /// <inheritdoc />
    public override Result<string> ConfiguredDatabase(DataProvider provider, DataConnectionConfig config)
    {
        var cs = provider.BuildConnectionString(config, NodeEndpoint.Primary(config));
        if (cs.IsFailure) return cs.Error!;
        return DatabaseOf(cs.Value!) is { Length: > 0 } name ? name : DataErrors.Validation("The connection does not name a database.");
    }

    /// <inheritdoc />
    public override async ValueTask<Result<bool>> ExistsAsync(DataProvider provider, DataConnectionConfig config, string database, CancellationToken ct)
    {
        var count = await ServerAsync(provider, config, ct, async (connection, token) =>
        {
            var sql = ExistsSql(database);
            await using var command = connection.CreateCommand();
            command.CommandText = sql.Render(provider.Dialect);
            for (var i = 0; i < sql.Parameters.Count; i++)
                command.Parameters.Add(sql.Parameters[i].Create(provider.Types, provider.Dialect.ParameterName(i)));
            return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false) ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
        }).ConfigureAwait(false);
        return count.IsFailure ? count.Error! : count.Value > 0;
    }

    /// <inheritdoc />
    public override async ValueTask<Result> CreateAsync(DataProvider provider, DataConnectionConfig config, string database, CancellationToken ct)
    {
        var exists = await ExistsAsync(provider, config, database, ct).ConfigureAwait(false);
        if (exists.IsFailure) return exists.Error!;
        if (exists.Value) return Result.Success();
        return await RunAsync(provider, config, CreateSql(database, config), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override ValueTask<Result> DropAsync(DataProvider provider, DataConnectionConfig config, string database, CancellationToken ct) =>
        RunAsync(provider, config, DropSql(database), ct);

    private async ValueTask<Result> RunAsync(DataProvider provider, DataConnectionConfig config, IReadOnlyList<string> statements, CancellationToken ct)
    {
        var ran = await ServerAsync(provider, config, ct, async (connection, token) =>
        {
            foreach (var statement in statements)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = statement;
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            return 0;
        }).ConfigureAwait(false);
        return ran.IsFailure ? ran.Error! : Result.Success();
    }

    private async ValueTask<Result<T>> ServerAsync<T>(DataProvider provider, DataConnectionConfig config, CancellationToken ct,
        Func<System.Data.Common.DbConnection, CancellationToken, Task<T>> work)
    {
        var node = NodeEndpoint.Primary(config);
        var cs = provider.BuildConnectionString(config, node);
        if (cs.IsFailure) return cs.Error!;
        try
        {
            await using var source = provider.CreateDataSource(config, node, WithDatabase(cs.Value!, ServerDatabase));
            await using var connection = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
            return await work(connection, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested) return DataErrors.Cancelled();
            return provider.Errors.Classify(ex).Error;
        }
    }

    /// <summary>Rejects names that cannot be quoted safely as identifiers.</summary>
    protected static string Checked(string database)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        if (database.Length > 128 || database.Any(c => char.IsControl(c)))
            throw new ArgumentException($"'{database}' is not a valid database name.", nameof(database));
        return database;
    }
}
