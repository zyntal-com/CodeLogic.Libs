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
        FullText = true,
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
