using System.Data.Common;
using CL.Data.Configuration;
using CL.Data.Mapping;
using CL.Data.Sql;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Results;

namespace CL.Data.Providers;

/// <summary>What a provider supports. Features a provider lacks are emulated by CL.Data or rejected with <c>data.unsupported</c>.</summary>
public sealed record ProviderCapabilities
{
    /// <summary>Most bound parameters in one command.</summary>
    public int ParameterLimit { get; init; } = 2100;
    /// <summary>Longest identifier the server accepts.</summary>
    public int MaxIdentifierLength { get; init; } = 63;
    /// <summary><c>RETURNING</c>/<c>OUTPUT</c> on writes.</summary>
    public bool Returning { get; init; }
    /// <summary>Multi-row <c>RETURNING</c> yields rows in insert order (SQL Server's <c>OUTPUT</c> does not promise this).</summary>
    public bool ReturningPreservesOrder { get; init; } = true;
    /// <summary>
    /// The affected-row count of an UPDATE counts only rows whose values changed, not rows matched (MySQL with
    /// <c>UseAffectedRows</c>). An entity update reporting no rows is then confirmed with an existence check.
    /// </summary>
    public bool AffectedRowsCountChangesOnly { get; init; }
    /// <summary>Native single-statement upsert.</summary>
    public bool NativeUpsert { get; init; }
    /// <summary>Schemas (namespaces) inside a database.</summary>
    public bool Schemas { get; init; }
    /// <summary>Row locks (<c>FOR UPDATE</c> or table hints).</summary>
    public bool RowLocks { get; init; }
    /// <summary>Skip rows locked by others (<c>SKIP LOCKED</c>/<c>READPAST</c>).</summary>
    public bool SkipLocked { get; init; }
    /// <summary>Fail immediately on a held lock (<c>NOWAIT</c>).</summary>
    public bool NoWait { get; init; }
    /// <summary>Named advisory locks across connections.</summary>
    public bool AdvisoryLocks { get; init; }
    /// <summary>Real <see cref="DbBatch"/> (one round trip).</summary>
    public bool DbBatch { get; init; }
    /// <summary>Multiple result sets from one command.</summary>
    public bool MultipleResultSets { get; init; } = true;
    /// <summary>Savepoints inside transactions.</summary>
    public bool Savepoints { get; init; } = true;
    /// <summary>DDL can run inside a transaction and roll back.</summary>
    public bool TransactionalDdl { get; init; }
    /// <summary>Read replicas.</summary>
    public bool Replicas { get; init; }
    /// <summary>Native bulk copy.</summary>
    public bool NativeBulkCopy { get; init; }
    /// <summary>JSON query functions.</summary>
    public bool Json { get; init; }
    /// <summary>Full-text search.</summary>
    public bool FullText { get; init; }
    /// <summary>Covering (INCLUDE) index columns.</summary>
    public bool IncludeColumns { get; init; }
    /// <summary>Partial (filtered) indexes.</summary>
    public bool FilteredIndexes { get; init; }
}

/// <summary>One server endpoint of a connection: the primary or a replica, with credentials resolved.</summary>
/// <param name="Name"><c>primary</c> or the replica name.</param>
/// <param name="IsPrimary">True for the primary.</param>
/// <param name="ConnectionString">Base connection string, when configured.</param>
/// <param name="Host">Host, when configured.</param>
/// <param name="Port">Port, when configured.</param>
/// <param name="Username">User name.</param>
/// <param name="Password">Password.</param>
/// <param name="Weight">Read weight (replicas).</param>
public sealed record NodeEndpoint(string Name, bool IsPrimary, string? ConnectionString, string? Host, int? Port,
    string? Username, string? Password, int Weight = 1)
{
    /// <summary>Name of the primary node.</summary>
    public const string PrimaryName = "primary";

    /// <summary>The primary endpoint of <paramref name="config"/>.</summary>
    public static NodeEndpoint Primary(DataConnectionConfig config) =>
        new(PrimaryName, true, config.ConnectionString, config.Host, config.Port, config.Username, config.Password);

    /// <summary>A replica endpoint; unset values fall back to the primary's.</summary>
    public static NodeEndpoint Replica(DataConnectionConfig config, ReplicaConfig replica) =>
        new(replica.Name, false,
            replica.ConnectionString ?? (replica.Host is null ? config.ConnectionString : null),
            replica.Host ?? config.Host, replica.Port ?? config.Port,
            replica.Username ?? config.Username, replica.Password ?? config.Password, replica.Weight);
}

/// <summary>The outcome of classifying a provider exception.</summary>
/// <param name="Error">The mapped error.</param>
/// <param name="IsTransient">True when retrying the same operation may succeed (deadlock, failover, connection reset).</param>
public readonly record struct ErrorClassification(Error Error, bool IsTransient);

/// <summary>Maps driver exceptions to CL.Data errors.</summary>
public interface IErrorClassifier
{
    /// <summary>Classifies <paramref name="exception"/>. Messages must not contain parameter values or secrets.</summary>
    ErrorClassification Classify(Exception exception);
}

/// <summary>Maps neutral column types to native types and creates typed parameters.</summary>
public interface ITypeMapper
{
    /// <summary>Native column type for DDL (for example <c>VARCHAR(320)</c>).</summary>
    string GetStoreType(ColumnMap column, DataConnectionConfig config);

    /// <summary>Creates a parameter for <paramref name="value"/>. Providers may return a generic typed parameter to avoid boxing.</summary>
    DbParameter CreateParameter<TValue>(string name, TValue value, ColumnMap? column);
}

/// <summary>
/// A database provider: the bridge between CL.Data and one ADO.NET driver. Provider packages
/// (CodeLogic.Data.MySql, .PostgreSql, .SqlServer, .Sqlite) derive from it and register an instance
/// with <see cref="DataProviderRegistry"/>.
/// </summary>
public abstract class DataProvider
{
    /// <summary>Provider name used in configuration (<c>"provider": "mysql"</c>). Lower case.</summary>
    public abstract string Name { get; }

    /// <summary>Contract version the provider implements; must equal <see cref="DataPackage.SpiVersion"/>.</summary>
    public virtual int SpiVersion => DataPackage.SpiVersion;

    /// <summary>Feature support.</summary>
    public abstract ProviderCapabilities Capabilities { get; }

    /// <summary>SQL dialect.</summary>
    public abstract SqlDialect Dialect { get; }

    /// <summary>Type mapping and parameter creation.</summary>
    public abstract ITypeMapper Types { get; }

    /// <summary>Exception classification.</summary>
    public abstract IErrorClassifier Errors { get; }

    /// <summary>
    /// A query that tries to take the advisory lock <paramref name="name"/> without waiting and returns a positive
    /// number (or true) when it got it. Only called when <see cref="ProviderCapabilities.AdvisoryLocks"/> is set.
    /// </summary>
    public virtual CL.Data.Sql.SqlText TryLockSql(string name) => throw new NotSupportedException($"{Name} has no advisory locks.");

    /// <summary>A statement that releases the advisory lock <paramref name="name"/>.</summary>
    public virtual CL.Data.Sql.SqlText UnlockSql(string name) => throw new NotSupportedException($"{Name} has no advisory locks.");

    /// <summary>A statement that writes a consistent copy of the whole database to <paramref name="file"/> (SQLite), or null.</summary>
    public virtual string? SnapshotSql(string file) => null;

    /// <summary>Server-level database administration (exists/create/drop), or null.</summary>
    public virtual CL.Data.Admin.DatabaseAdmin? DatabaseAdmin => null;

    /// <summary>Catalog reader for schema sync and drift detection (null when the provider has none).</summary>
    public virtual CL.Data.Schema.SchemaIntrospector? Introspector => null;

    /// <summary>DDL generation for schema sync. Default: standard SQL through <see cref="Dialect"/>.</summary>
    public virtual CL.Data.Schema.DdlGenerator Ddl => field ??= new CL.Data.Schema.DdlGenerator(Dialect);

    /// <summary>Default server port (0 when not applicable).</summary>
    public virtual int DefaultPort => 0;

    /// <summary>Native bulk copy; null when the provider has none (CL.Data then batches multi-row inserts).</summary>
    public virtual CL.Data.Bulk.BulkCopier? BulkCopier => null;

    /// <summary>Captures execution plans of slow reads (<c>CaptureExplainOnSlowQuery</c>); null when unsupported.</summary>
    public virtual CL.Data.Telemetry.QueryExplainer? Explainer => null;

    /// <summary>Measures replica lag for read routing; null when the provider has no replicas (SQLite).</summary>
    public virtual CL.Data.Routing.ReplicaProbe? ReplicaProbe => null;

    /// <summary>
    /// Builds the driver connection string for <paramref name="node"/> of <paramref name="config"/>: the
    /// configured connection string (if any) as the base, then discrete settings and provider options.
    /// </summary>
    public abstract Result<string> BuildConnectionString(DataConnectionConfig config, NodeEndpoint node);

    /// <summary>Creates the pooled data source for <paramref name="node"/>, from the connection string <see cref="BuildConnectionString"/> produced.</summary>
    public abstract DbDataSource CreateDataSource(DataConnectionConfig config, NodeEndpoint node, string connectionString);

    /// <summary>Provider-specific configuration checks; add problems to <paramref name="errors"/> under <paramref name="path"/>.</summary>
    public virtual void Validate(DataConnectionConfig config, string path, List<ConfigFieldError> errors) { }

    /// <summary>Statement that checks a connection works (health checks).</summary>
    public virtual string PingSql => "SELECT 1";

    /// <summary>Query returning the server version as text (<c>DataAdmin.ServerVersionAsync</c>).</summary>
    public virtual string ServerVersionSql => "SELECT version()";

    /// <summary>
    /// Normalizes a native type for comparison between the entity model and the database (lower case, single spaces,
    /// provider aliases such as <c>character varying</c> → <c>varchar</c>).
    /// </summary>
    public virtual string NormalizeStoreType(string storeType) =>
        string.Join(' ', storeType.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Replace(", ", ",", StringComparison.Ordinal);

    /// <summary>Provider table options for <paramref name="map"/> (engine, charset, ...), stored in <c>TableSchema.Options</c>.</summary>
    public virtual IReadOnlyDictionary<string, string> TableOptions(EntityMap map, DataConnectionConfig config) => new Dictionary<string, string>();

    /// <summary>Schema for tables without an explicit one (null when the provider has no schemas).</summary>
    public virtual string? GetDefaultSchema(DataConnectionConfig config) => null;

    /// <summary>
    /// The identity generated by the last INSERT on <paramref name="command"/>'s connection, for providers without
    /// <c>RETURNING</c> (MySQL's <c>LastInsertedId</c>). Null when unavailable.
    /// </summary>
    public virtual object? ReadLastInsertId(DbCommand command) => null;

    /// <inheritdoc />
    public override string ToString() => Name;
}
