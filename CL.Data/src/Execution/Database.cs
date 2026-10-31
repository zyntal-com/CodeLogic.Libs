using System.Data.Common;
using CL.Data.Configuration;
using CL.Data.Errors;
using System.Diagnostics;
using CL.Data.Mapping;
using CL.Data.Pipeline;
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

    /// <summary>Application interceptors, run around every command (outermost).</summary>
    public IReadOnlyList<IDataInterceptor> Interceptors { get; init; } = [];

    /// <summary>Additional middleware, run inside the interceptors in this order.</summary>
    public IReadOnlyList<ICommandMiddleware> Middleware { get; init; } = [];

    /// <summary>Current user for <c>[CreatedBy]</c>/<c>[UpdatedBy]</c>.</summary>
    public CL.Data.Writes.IDataUserAccessor? UserAccessor { get; init; }

    /// <summary>Current tenant for <c>[TenantKey]</c> entities.</summary>
    public CL.Data.Writes.IDataTenantAccessor? TenantAccessor { get; init; }
}

/// <summary>
/// One configured connection: its provider, its primary and replica nodes, and the entry point for queries,
/// repositories, transactions and raw SQL. Obtain it from <c>DataLibrary.Db(name)</c>, or create it directly
/// with <see cref="Create(string, DataConnectionConfig, DatabaseOptions?)"/> outside the CodeLogic framework.
/// </summary>
public sealed partial class Database : IAsyncDisposable
{
    private readonly CommandExecutor _executor;
    private readonly CommandPipeline _pipeline;
    private readonly DataTransaction? _transaction;

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
        List<ICommandMiddleware> middleware = [];
        if (options.Interceptors.Count > 0) middleware.Add(new InterceptorMiddleware(options.Interceptors));
        middleware.AddRange(options.Middleware);
        _pipeline = new CommandPipeline(middleware);
    }

    /// <summary>A view of <paramref name="parent"/> whose commands all run in <paramref name="transaction"/>.</summary>
    private Database(Database parent, DataTransaction transaction)
    {
        Name = parent.Name;
        Provider = parent.Provider;
        Config = parent.Config;
        Options = parent.Options;
        Primary = parent.Primary;
        Replicas = parent.Replicas;
        _executor = parent._executor;
        _pipeline = parent._pipeline;
        _transaction = transaction;
    }

    /// <summary>The transaction this view is bound to, or null for the connection itself.</summary>
    public DataTransaction? Transaction => _transaction;

    internal Database BoundTo(DataTransaction transaction) => new(this, transaction);

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

    /// <summary>The command pipeline of this database.</summary>
    public CommandPipeline Pipeline => _pipeline;

    /// <summary>
    /// Runs a command through the pipeline and then on its node (the primary unless routing chose another),
    /// recording the elapsed time.
    /// </summary>
    internal ValueTask<Result<TResult>> RunAsync<TState, TResult>(CommandContext context, TState state,
        Func<DbCommand, TState, CancellationToken, ValueTask<TResult>> body, Func<TResult, long>? rows = null,
        System.Data.IsolationLevel? transaction = null)
    {
        if (_transaction is not null) context.InTransaction = true;
        return _pipeline.ExecuteAsync(context, async ctx =>
        {
            var started = Stopwatch.GetTimestamp();
            var node = ctx.Node ?? Primary;
            var result = _transaction is { } tx
                ? await tx.ExecuteAsync(ctx.Spec, state, body, ctx.CancellationToken).ConfigureAwait(false)
                : transaction is { } isolation
                    ? await ExecuteInTransactionAsync(node, ctx, state, body, isolation).ConfigureAwait(false)
                    : await _executor.ExecuteAsync(node, ctx.Spec, state, body, null, null, ctx.CancellationToken).ConfigureAwait(false);
            ctx.Elapsed = Stopwatch.GetElapsedTime(started);
            if (rows is not null && result.IsSuccess) ctx.Rows = rows(result.Value!);
            return result;
        });
    }

    private CommandContext Context(SqlText sql, CommandKind kind, CancellationToken ct, bool isWrite = false) =>
        new(this, new CommandSpec(sql.Render(Provider.Dialect), sql.Parameters), kind, ct) { IsWrite = isWrite };

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

    /// <summary>Runs a query and builds each row with <paramref name="read"/> (catalog queries, custom readers).</summary>
    public ValueTask<Result<List<T>>> SqlReadAsync<T>(SqlText sql, Func<DbDataReader, T> read, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        return RunAsync(Context(sql, CommandKind.Query, ct), read,
            static async (cmd, build, token) =>
            {
                List<T> rows = [];
                await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false)) rows.Add(build(reader));
                return rows;
            },
            static list => list.Count);
    }

    /// <summary>Runs a statement and returns the number of affected rows. Raw statements are treated as writes.</summary>
    public async ValueTask<Result<int>> ExecuteSqlAsync(SqlText sql, CancellationToken ct = default)
    {
        return await RunAsync(Context(sql, CommandKind.NonQuery, ct, isWrite: true), 0,
            static async (cmd, _, token) => await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false),
            static affected => affected).ConfigureAwait(false);
    }

    /// <summary>Runs a query and returns the first column of the first row (default when there is no row or it is NULL).</summary>
    public ValueTask<Result<T?>> SqlScalarAsync<T>(SqlText sql, CancellationToken ct = default) =>
        RunAsync(Context(sql, CommandKind.Scalar, ct), 0,
            static async (cmd, _, token) =>
            {
                await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                return await reader.ReadAsync(token).ConfigureAwait(false) && !reader.IsDBNull(0)
                    ? ValueReader<T>.Read(reader, 0)
                    : default;
            });

    /// <summary>Checks that the primary answers (health checks).</summary>
    public async ValueTask<Result> PingAsync(CancellationToken ct = default) => await PingAsync(Primary, ct).ConfigureAwait(false);

    /// <summary>Checks that <paramref name="node"/> answers. Bypasses the pipeline, so health checks see the node itself.</summary>
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

        return await RunAsync(Context(sql, CommandKind.Query, ct), (Accessor: accessor.Value, Policy: NamingPolicy),
            static async (cmd, state, token) =>
            {
                await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                return await RowReader.ReadAllAsync(reader, state.Accessor, state.Policy, token).ConfigureAwait(false);
            },
            static list => list.Count).ConfigureAwait(false);
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
        if (_transaction is not null) return;   // a transaction view owns no data sources
        await Primary.DisposeAsync().ConfigureAwait(false);
        foreach (var replica in Replicas) await replica.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({Provider.Name})";
}
