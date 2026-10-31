using System.Data.Common;
using CL.Data.Configuration;
using CL.Data.Errors;
using CL.Data.Providers;
using CL.Data.Sql;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Results;
using Microsoft.Data.Sqlite;

namespace CL.Data.Sqlite;

/// <summary>The SQLite provider for CL.Data (Microsoft.Data.Sqlite).</summary>
public sealed class SqliteDataProvider : DataProvider
{
    /// <summary>Provider name used in configuration.</summary>
    public const string ProviderName = "sqlite";

    /// <summary>The shared instance.</summary>
    public static SqliteDataProvider Instance { get; } = new();

    private SqliteDataProvider() { }

    /// <inheritdoc />
    public override string Name => ProviderName;

    /// <inheritdoc />
    public override ProviderCapabilities Capabilities { get; } = new()
    {
        ParameterLimit = 32766,
        MaxIdentifierLength = 1024,
        Returning = true,
        NativeUpsert = true,
        Schemas = false,
        RowLocks = false,
        SkipLocked = false,
        NoWait = false,
        AdvisoryLocks = false,
        DbBatch = false,
        MultipleResultSets = true,
        Savepoints = true,
        TransactionalDdl = true,
        Replicas = false,
        NativeBulkCopy = false,
        Json = true,
        FullText = false,
        IncludeColumns = false,
        FilteredIndexes = true,
    };

    /// <inheritdoc />
    public override SqlDialect Dialect { get; } = new SqliteSqlDialect();

    /// <inheritdoc />
    public override ITypeMapper Types { get; } = new SqliteTypeMapper();

    /// <inheritdoc />
    public override IErrorClassifier Errors { get; } = new SqliteErrorClassifier();

    /// <inheritdoc />
    public override CL.Data.Schema.DdlGenerator Ddl => field ??= new SqliteDdlGenerator(Dialect);

    /// <inheritdoc />
    public override CL.Data.Schema.SchemaIntrospector Introspector { get; } = new SqliteSchemaIntrospector();

    /// <inheritdoc />
    public override string ServerVersionSql => "SELECT sqlite_version()";

    /// <inheritdoc />
    public override CL.Data.Bulk.BulkCopier BulkCopier { get; } = new SqliteBulkCopier();

    /// <inheritdoc />
    public override CL.Data.Telemetry.QueryExplainer Explainer { get; } = new CL.Data.Telemetry.PrefixExplainer("EXPLAIN QUERY PLAN ");

    /// <inheritdoc />
    public override CL.Data.Admin.DatabaseAdmin DatabaseAdmin { get; } = new SqliteDatabaseAdmin();

    /// <inheritdoc />
    public override string? SnapshotSql(string file) => "VACUUM INTO '" + file.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> TableOptions(CL.Data.Mapping.EntityMap map, DataConnectionConfig config)
    {
        var hint = map.Hint<SqliteTableAttribute>();
        var options = new Dictionary<string, string>();
        if (hint?.WithoutRowId == true) options["without_rowid"] = "true";
        if (hint?.Strict == true) options["strict"] = "true";
        return options;
    }

    /// <inheritdoc />
    public override Result<string> BuildConnectionString(DataConnectionConfig config, NodeEndpoint node)
    {
        SqliteConnectionStringBuilder builder;
        try
        {
            builder = new SqliteConnectionStringBuilder(config.ConnectionString ?? "");
        }
        catch (ArgumentException ex)
        {
            return DataErrors.Validation("The SQLite connection string is not valid.", ex.Message);
        }

        var options = config.Sqlite ?? new SqliteOptions();
        var database = config.Database;
        if (!string.IsNullOrWhiteSpace(database))
        {
            if (IsMemory(database))
            {
                // A private :memory: database would be empty on every pooled connection; use a named shared one
                // that lives as long as the data source keeps a connection open.
                builder.DataSource = $"cl-data-{Guid.NewGuid():N}";
                builder.Mode = SqliteOpenMode.Memory;
                builder.Cache = SqliteCacheMode.Shared;
            }
            else
            {
                builder.DataSource = Path.GetFullPath(database);
                builder.Mode = options.ReadOnly ? SqliteOpenMode.ReadOnly
                    : options.CreateFile ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite;
            }
        }

        builder.Pooling = config.Pooling;
        builder.DefaultTimeout = config.CommandTimeoutSeconds;
        if (!string.IsNullOrEmpty(config.Password)) builder.Password = config.Password;
        return builder.ToString();
    }

    /// <inheritdoc />
    public override DbDataSource CreateDataSource(DataConnectionConfig config, NodeEndpoint node, string connectionString) =>
        new SqliteDataSource(connectionString, config.Sqlite ?? new SqliteOptions(),
            !string.IsNullOrWhiteSpace(config.Database) && IsMemory(config.Database));

    /// <inheritdoc />
    public override void Validate(DataConnectionConfig config, string path, List<ConfigFieldError> errors)
    {
        if (config.Replicas.Count > 0)
            errors.Add(new($"{path}.replicas", "SQLite does not support replicas."));
        if (config.Sqlite is { BusyTimeoutMs: < 0 })
            errors.Add(new($"{path}.sqlite.busyTimeoutMs", "Busy timeout cannot be negative."));
        if (!string.IsNullOrWhiteSpace(config.Database) && !IsMemory(config.Database) &&
            config.Sqlite is { CreateFile: true, ReadOnly: false })
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(config.Database));
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                try { Directory.CreateDirectory(directory); }
                catch (Exception ex) { errors.Add(new($"{path}.database", $"Cannot create the database directory: {ex.Message}")); }
            }
        }
    }

    internal static bool IsMemory(string database) =>
        string.Equals(database.Trim(), ":memory:", StringComparison.OrdinalIgnoreCase);
}

/// <summary>SQLite bulk insert: one prepared INSERT executed per row inside a single transaction (SQLite's fastest path).</summary>
public sealed class SqliteBulkCopier : CL.Data.Bulk.BulkCopier
{
    /// <inheritdoc />
    public override Task<long?> CopyAsync(CL.Data.Bulk.BulkCopyRequest request, CancellationToken ct)
    {
        var connection = (SqliteConnection)request.Connection;
        var dialect = request.Database.Provider.Dialect;
        var owned = request.Transaction is null ? connection.BeginTransaction() : null;
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction?)(request.Transaction ?? owned);
            command.CommandTimeout = request.TimeoutSeconds;
            var placeholders = new string[request.Columns.Count];
            var parameters = new SqliteParameter[request.Columns.Count];
            for (var c = 0; c < parameters.Length; c++)
            {
                placeholders[c] = dialect.ParameterName(c);
                parameters[c] = command.Parameters.Add(placeholders[c], SqliteType.Text);
            }
            command.CommandText = $"INSERT INTO {request.QuotedTable} ({string.Join(", ", request.Columns.Select(dialect.QuoteIdentifier))}) VALUES ({string.Join(", ", placeholders)})";
            command.Prepare();

            long total = 0;
            for (var row = 0; row < request.Rows.Count; row++)
            {
                ct.ThrowIfCancellationRequested();
                for (var c = 0; c < parameters.Length; c++)
                {
                    var source = request.Parameter(row, c);
                    if (source is SqliteParameter typed) parameters[c].SqliteType = typed.SqliteType;
                    else parameters[c].DbType = source.DbType;
                    parameters[c].Value = source.Value ?? DBNull.Value;
                }
                total += command.ExecuteNonQuery();
            }
            owned?.Commit();
            return Task.FromResult<long?>(total);
        }
        finally
        {
            owned?.Dispose();
        }
    }
}
