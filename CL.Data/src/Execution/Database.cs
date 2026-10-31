using System.Data.Common;
using CL.Data.Configuration;
using CL.Data.Errors;
using CL.Data.Mapping;
using CL.Data.Providers;
using CL.Data.Sql;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Results;

namespace CL.Data.Execution;

/// <summary>Settings for a <see cref="Database"/> that come from the host rather than the connection configuration.</summary>
public sealed record DatabaseOptions
{
    /// <summary>Clock for timestamps, caching and scheduling.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// One configured connection: its provider, its primary and replica nodes, and the entry point for queries,
/// repositories, transactions and raw SQL. Obtain it from <c>DataLibrary.Db(name)</c>, or create it directly
/// with <see cref="Create(string, DataConnectionConfig, DatabaseOptions?)"/> outside the CodeLogic framework.
/// </summary>
public sealed class Database : IAsyncDisposable
{
    private readonly CommandExecutor _executor;

    private Database(string name, DataProvider provider, DataConnectionConfig config, DatabaseOptions options,
        DataNode primary, IReadOnlyList<DataNode> replicas)
    {
        Name = name;
        Provider = provider;
        Config = config;
        Options = options;
        Primary = primary;
        Replicas = replicas;
        _executor = new CommandExecutor(provider, config.CommandTimeoutSeconds);
    }

    /// <summary>Connection name.</summary>
    public string Name { get; }

    /// <summary>The provider.</summary>
    public DataProvider Provider { get; }

    /// <summary>Connection settings.</summary>
    public DataConnectionConfig Config { get; }

    /// <summary>Host settings.</summary>
    public DatabaseOptions Options { get; }

    /// <summary>The primary node.</summary>
    public DataNode Primary { get; }

    /// <summary>Enabled replicas.</summary>
    public IReadOnlyList<DataNode> Replicas { get; }

    /// <summary>Naming policy of this connection.</summary>
    public NamingPolicy NamingPolicy => Config.NamingPolicy;

    internal CommandExecutor Executor => _executor;

    /// <summary>Creates a database for <paramref name="config"/>, resolving its provider from <see cref="DataProviderRegistry"/>.</summary>
    public static Result<Database> Create(string name, DataConnectionConfig config, DatabaseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var provider = DataProviderRegistry.Get(config.ProviderKey);
        return provider.IsSuccess ? Create(name, provider.Value!, config, options) : provider.Error!;
    }

    /// <summary>Creates a database for <paramref name="config"/> with an explicit provider.</summary>
    public static Result<Database> Create(string name, DataProvider provider, DataConnectionConfig config, DatabaseOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(config);

        if (provider.SpiVersion != DataPackage.SpiVersion)
            return DataErrors.ProviderIncompatible(provider.Name, provider.SpiVersion, DataPackage.SpiVersion);

        List<ConfigFieldError> problems = [];
        config.Validate(name, problems);
        provider.Validate(config, name, problems);
        if (problems.Count > 0)
            return DataErrors.Validation($"Connection '{name}' is not configured correctly.",
                string.Join("; ", problems.Select(p => $"{p.Path}: {p.Message}")));

        List<DataNode> created = [];
        try
        {
            var primary = CreateNode(provider, config, NodeEndpoint.Primary(config));
            if (primary.IsFailure) return primary.Error!;
            created.Add(primary.Value!);

            List<DataNode> replicas = [];
            foreach (var replica in config.Replicas.Where(r => r.Enabled))
            {
                var node = CreateNode(provider, config, NodeEndpoint.Replica(config, replica));
                if (node.IsFailure)
                {
                    DisposeAll(created);
                    return node.Error!;
                }
                created.Add(node.Value!);
                replicas.Add(node.Value!);
            }

            return new Database(name, provider, config, options ?? new DatabaseOptions(), primary.Value!, replicas);
        }
        catch (Exception ex)
        {
            DisposeAll(created);
            return DataErrors.Validation($"Connection '{name}' could not be created.", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Raw SQL ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs a query and reads every row. <typeparamref name="T"/> is either a scalar (read from the first column)
    /// or a mapped type (columns matched by name). Interpolated values become parameters.
    /// </summary>
    public ValueTask<Result<List<T>>> SqlQueryAsync<T>(SqlText sql, CancellationToken ct = default) =>
        QueryCoreAsync<T>(sql, ct);

    /// <summary>Runs a statement and returns the number of affected rows.</summary>
    public async ValueTask<Result<int>> ExecuteSqlAsync(SqlText sql, CancellationToken ct = default)
    {
        var spec = new CommandSpec(sql.Render(Provider.Dialect), sql.Parameters);
        return await _executor.ExecuteAsync(Primary, spec, 0,
            static async (cmd, _, token) => await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false),
            null, null, ct).ConfigureAwait(false);
    }

    /// <summary>Runs a query and returns the first column of the first row (default when there is no row or it is NULL).</summary>
    public async ValueTask<Result<T?>> SqlScalarAsync<T>(SqlText sql, CancellationToken ct = default)
    {
        var spec = new CommandSpec(sql.Render(Provider.Dialect), sql.Parameters);
        return await _executor.ExecuteAsync(Primary, spec, 0,
            static async (cmd, _, token) =>
            {
                await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                return await reader.ReadAsync(token).ConfigureAwait(false) && !reader.IsDBNull(0)
                    ? ValueReader<T>.Read(reader, 0)
                    : default;
            },
            null, null, ct).ConfigureAwait(false);
    }

    /// <summary>Checks that the primary answers (health checks).</summary>
    public async ValueTask<Result> PingAsync(CancellationToken ct = default) => await PingAsync(Primary, ct).ConfigureAwait(false);

    /// <summary>Checks that <paramref name="node"/> answers.</summary>
    public async ValueTask<Result> PingAsync(DataNode node, CancellationToken ct = default)
    {
        var result = await _executor.ExecuteAsync(node, new CommandSpec(Provider.PingSql, []), 0,
            static async (cmd, _, token) => await cmd.ExecuteScalarAsync(token).ConfigureAwait(false),
            null, null, ct).ConfigureAwait(false);
        return result.ToResult();
    }

    private async ValueTask<Result<List<T>>> QueryCoreAsync<T>(SqlText sql, CancellationToken ct)
    {
        var accessor = RowReader.AccessorFor<T>();
        if (accessor.IsFailure) return accessor.Error!;

        var spec = new CommandSpec(sql.Render(Provider.Dialect), sql.Parameters);
        return await _executor.ExecuteAsync(Primary, spec, (Accessor: accessor.Value, Policy: NamingPolicy),
            static async (cmd, state, token) =>
            {
                await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                return await RowReader.ReadAllAsync(reader, state.Accessor, state.Policy, token).ConfigureAwait(false);
            },
            null, null, ct).ConfigureAwait(false);
    }

    private static Result<DataNode> CreateNode(DataProvider provider, DataConnectionConfig config, NodeEndpoint endpoint)
    {
        var connectionString = provider.BuildConnectionString(config, endpoint);
        if (connectionString.IsFailure) return connectionString.Error!;
        DbDataSource source = provider.CreateDataSource(config, endpoint, connectionString.Value!);
        return new DataNode(endpoint, source);
    }

    private static void DisposeAll(List<DataNode> nodes)
    {
        foreach (var node in nodes) node.DataSource.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Primary.DisposeAsync().ConfigureAwait(false);
        foreach (var replica in Replicas) await replica.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({Provider.Name})";
}
