using System.Data.Common;
using System.Globalization;
using CL.Data.Configuration;
using CL.Data.Errors;
using CL.Data.Mapping;
using CL.Data.Providers;
using CL.Data.Sql;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Results;
using CodeLogic.Framework.Libraries;
using MySqlConnector;

namespace CL.Data.MySql;

/// <summary>The MySQL / MariaDB provider for CL.Data (MySqlConnector).</summary>
public sealed class MySqlDataProvider : DataProvider
{
    /// <summary>Provider name used in configuration.</summary>
    public const string ProviderName = "mysql";

    /// <summary>The shared instance.</summary>
    public static MySqlDataProvider Instance { get; } = new();

    private MySqlDataProvider() { }

    /// <inheritdoc />
    public override string Name => ProviderName;

    /// <inheritdoc />
    public override int DefaultPort => 3306;

    /// <inheritdoc />
    public override ProviderCapabilities Capabilities { get; } = new()
    {
        ParameterLimit = 65535,
        AffectedRowsCountChangesOnly = true,   // UseAffectedRows: insert-ignore needs it to tell duplicates apart
        MaxIdentifierLength = 64,
        Returning = false,
        NativeUpsert = true,
        Schemas = false,
        RowLocks = true,
        SkipLocked = true,
        NoWait = true,
        AdvisoryLocks = true,
        DbBatch = true,
        MultipleResultSets = true,
        Savepoints = true,
        TransactionalDdl = false,
        Replicas = true,
        NativeBulkCopy = true,
        Json = true,
        FullText = true,
        IncludeColumns = false,
        FilteredIndexes = false,
    };

    /// <inheritdoc />
    public override SqlDialect Dialect { get; } = new MySqlSqlDialect();

    /// <inheritdoc />
    public override ITypeMapper Types { get; } = new MySqlTypeMapper();

    /// <inheritdoc />
    public override IErrorClassifier Errors { get; } = new MySqlErrorClassifier();

    /// <inheritdoc />
    public override CL.Data.Schema.DdlGenerator Ddl => field ??= new MySqlDdlGenerator(Dialect);

    /// <inheritdoc />
    public override CL.Data.Schema.SchemaIntrospector Introspector { get; } = new MySqlSchemaIntrospector();

    /// <inheritdoc />
    public override CL.Data.Routing.ReplicaProbe ReplicaProbe { get; } = new MySqlReplicaProbe();

    /// <inheritdoc />
    public override string ServerVersionSql => "SELECT VERSION()";

    /// <inheritdoc />
    public override CL.Data.Bulk.BulkCopier BulkCopier { get; } = new MySqlBulkCopier();

    /// <inheritdoc />
    public override CL.Data.Telemetry.QueryExplainer Explainer { get; } = new CL.Data.Telemetry.PrefixExplainer("EXPLAIN FORMAT=TREE ");

    /// <inheritdoc />
    public override CL.Data.Admin.DatabaseAdmin DatabaseAdmin { get; } = new MySqlDatabaseAdmin();

    // MySQL lock names are limited to 64 characters.
    private static string LockName(string name) => name.Length <= 64 ? name : CL.Data.Schema.SchemaBuilder.Identifier(name, 64);

    /// <inheritdoc />
    public override CL.Data.Sql.SqlText TryLockSql(string name) => $"SELECT GET_LOCK({LockName(name)}, 0)";

    /// <inheritdoc />
    public override CL.Data.Sql.SqlText UnlockSql(string name) => $"SELECT RELEASE_LOCK({LockName(name)})";

    /// <inheritdoc />
    public override string NormalizeStoreType(string storeType) => CL.Data.Schema.StoreTypeNormalizer.MySql(base.NormalizeStoreType(storeType));

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> TableOptions(EntityMap map, DataConnectionConfig config) =>
        MySqlTableOptions.For(map, config);

    /// <inheritdoc />
    public override Result<string> BuildConnectionString(DataConnectionConfig config, NodeEndpoint node)
    {
        MySqlConnectionStringBuilder builder;
        try
        {
            builder = new MySqlConnectionStringBuilder(node.ConnectionString ?? "");
        }
        catch (ArgumentException ex)
        {
            return DataErrors.Validation("The MySQL connection string is not valid.", ex.Message);
        }

        var options = config.MySql ?? new MySqlOptions();
        if (!string.IsNullOrWhiteSpace(node.Host))
        {
            // Extra hosts give driver-level failover of the primary.
            var hosts = node.IsPrimary ? [node.Host, .. config.FailoverHosts] : new List<string> { node.Host };
            builder.Server = string.Join(",", hosts);
            if (hosts.Count > 1) builder.LoadBalance = MySqlLoadBalance.FailOver;
        }
        if (node.Port is { } port) builder.Port = (uint)port;
        if (!string.IsNullOrWhiteSpace(config.Database)) builder.Database = config.Database;
        if (node.Username is not null) builder.UserID = node.Username;
        if (node.Password is not null) builder.Password = node.Password;

        builder.Pooling = config.Pooling;
        builder.MinimumPoolSize = (uint)config.MinPoolSize;
        builder.MaximumPoolSize = (uint)config.MaxPoolSize;
        builder.ConnectionLifeTime = (uint)config.ConnectionLifetimeSeconds;
        builder.ConnectionTimeout = (uint)config.ConnectTimeoutSeconds;
        builder.DefaultCommandTimeout = (uint)config.CommandTimeoutSeconds;
        builder.CharacterSet = options.CharacterSet;
        builder.AllowPublicKeyRetrieval = options.AllowPublicKeyRetrieval;
        builder.AllowLoadLocalInfile = options.AllowLoadLocalInfile;
        builder.IgnorePrepare = !config.PrepareStatements;

        // Fixed by CL.Data: affected rows (not found rows) so insert-ignore can tell duplicates apart; GUIDs as
        // BINARY(16) in RFC byte order (time-ordered for version 7); date/times read as UTC.
        builder.UseAffectedRows = true;
        builder.GuidFormat = MySqlGuidFormat.Binary16;
        builder.DateTimeKind = MySqlDateTimeKind.Utc;

        switch (config.SslMode)
        {
            case DataSslMode.Disable: builder.SslMode = MySqlSslMode.None; break;
            case DataSslMode.Prefer: builder.SslMode = MySqlSslMode.Preferred; break;
            case DataSslMode.Require: builder.SslMode = MySqlSslMode.Required; break;
            case DataSslMode.VerifyCA: builder.SslMode = MySqlSslMode.VerifyCA; break;
            case DataSslMode.VerifyFull: builder.SslMode = MySqlSslMode.VerifyFull; break;
        }
        if (!string.IsNullOrWhiteSpace(config.SslCaCertificatePath)) builder.SslCa = config.SslCaCertificatePath;
        return builder.ConnectionString;
    }

    /// <inheritdoc />
    public override DbDataSource CreateDataSource(DataConnectionConfig config, NodeEndpoint node, string connectionString) =>
        new MySqlDataSource(connectionString);

    /// <inheritdoc />
    public override object? ReadLastInsertId(DbCommand command) =>
        command is MySqlCommand { LastInsertedId: > 0 } mysql ? mysql.LastInsertedId : null;

    /// <inheritdoc />
    public override void Validate(DataConnectionConfig config, string path, List<ConfigFieldError> errors)
    {
        if (config.MySql is { CharacterSet: { Length: 0 } })
            errors.Add(new($"{path}.mySql.characterSet", "A character set is required."));
    }
}

/// <summary>
/// MySQL type mapping: <c>TINYINT(1)</c> booleans, <c>DATETIME(6)</c> UTC time values, <c>BINARY(16)</c> GUIDs, native
/// <c>JSON</c>; unsigned integers through <c>[MySqlColumn(Unsigned = true)]</c>.
/// </summary>
public sealed class MySqlTypeMapper : ITypeMapper
{
    /// <inheritdoc />
    public string GetStoreType(ColumnMap column, DataConnectionConfig config)
    {
        var hint = column.Hint<MySqlColumnAttribute>();
        if (hint?.Type is { } hinted) return hinted;
        if (column.NativeType is { } native) return native;
        var unsigned = hint?.Unsigned == true ? " UNSIGNED" : "";
        var size = column.Size > 0 ? column.Size : config.DefaultStringSize;
        var fraction = column.Precision is >= 0 and <= 6 ? column.Precision : 6;
        return column.DataType switch
        {
            DataType.Boolean => "TINYINT(1)",
            DataType.TinyInt => "TINYINT" + unsigned,
            DataType.SmallInt => "SMALLINT" + unsigned,
            DataType.Int => "INT" + unsigned,
            DataType.BigInt => "BIGINT" + unsigned,
            DataType.Decimal => string.Create(CultureInfo.InvariantCulture,
                $"DECIMAL({(column.Precision > 0 ? column.Precision : 18)},{(column.Scale >= 0 ? column.Scale : 2)})"),
            DataType.Real => "FLOAT",
            DataType.Double => "DOUBLE",
            DataType.String when column.Size < 0 => "LONGTEXT",
            DataType.String => string.Create(CultureInfo.InvariantCulture, $"VARCHAR({size})"),
            DataType.FixedString => string.Create(CultureInfo.InvariantCulture, $"CHAR({(column.Size > 0 ? column.Size : 1)})"),
            DataType.Text => "LONGTEXT",
            DataType.Binary when column.Size > 0 => string.Create(CultureInfo.InvariantCulture, $"VARBINARY({column.Size})"),
            DataType.FixedBinary => string.Create(CultureInfo.InvariantCulture, $"BINARY({(column.Size > 0 ? column.Size : 16)})"),
            DataType.Binary or DataType.Blob => "LONGBLOB",
            DataType.Guid => "BINARY(16)",
            DataType.Date => "DATE",
            DataType.Time => string.Create(CultureInfo.InvariantCulture, $"TIME({fraction})"),
            DataType.DateTime or DataType.DateTimeOffset => string.Create(CultureInfo.InvariantCulture, $"DATETIME({fraction})"),
            DataType.Interval => "TIME(6)",
            DataType.Json => "JSON",
            DataType.Xml => "LONGTEXT",
            DataType.RowVersion => "BIGINT",
            DataType.Geometry or DataType.Geography => "GEOMETRY",
            _ => string.Create(CultureInfo.InvariantCulture, $"VARCHAR({size})"),
        };
    }

    /// <inheritdoc />
    public DbParameter CreateParameter<TValue>(string name, TValue value, ColumnMap? column) =>
        new MySqlParameter(name, value switch
        {
            null => DBNull.Value,
            DateTimeOffset dto => dto.UtcDateTime,
            DateTime { Kind: DateTimeKind.Local } dt => dt.ToUniversalTime(),
            _ => value,
        });
}

/// <summary>Maps MySQL server error numbers to CL.Data errors.</summary>
public sealed class MySqlErrorClassifier : IErrorClassifier
{
    /// <inheritdoc />
    public ErrorClassification Classify(Exception exception)
    {
        if (exception is not MySqlException mysql)
            return new(DataErrors.FromException(exception), exception is TimeoutException);

        var details = $"MySQL error {mysql.Number} ({mysql.SqlState}): {mysql.Message}";
        return mysql.Number switch
        {
            1062 or 1586 => new(DataErrors.DuplicateKey("A row with the same key already exists.", details), false),
            1451 or 1452 or 1216 or 1217 => new(DataErrors.ForeignKeyViolation("A foreign key constraint rejected the change.", details), false),
            1048 or 3819 or 1364 => new(DataErrors.ConstraintViolation("A constraint rejected the change.", details), false),
            1213 => new(DataErrors.Deadlock(details), true),
            1205 => new(DataErrors.LockTimeout(details), true),
            3572 => new(DataErrors.LockNotAcquired("A requested row is locked by another transaction.", details), false),
            1044 or 1045 or 1142 or 1143 or 1227 => new(DataErrors.AccessDenied("The database refused the credentials or permission.", details), false),
            1317 => new(DataErrors.Cancelled(details), false),
            1040 or 1042 or 1043 or 1047 or 1053 or 1077 or 1152 or 1158 or 1159 or 1160 or 1161 or 2002 or 2003 or 2006 or 2013
                => new(DataErrors.Unavailable("The MySQL server could not be reached.", details), true),
            1290 when mysql.Message.Contains("read-only", StringComparison.OrdinalIgnoreCase) || mysql.Message.Contains("read only", StringComparison.OrdinalIgnoreCase)
                => new(DataErrors.Unavailable("The MySQL server is read-only (a replica or failover in progress).", details), true),
            _ when mysql.ErrorCode == MySqlErrorCode.CommandTimeoutExpired => new(DataErrors.Timeout("The MySQL command timed out.", details), true),
            _ when mysql.ErrorCode == MySqlErrorCode.UnableToConnectToHost => new(DataErrors.Unavailable("The MySQL server could not be reached.", details), true),
            _ => new(DataErrors.ProviderError("The MySQL operation failed.", details), mysql.IsTransient),
        };
    }
}

/// <summary>CodeLogic library that registers the MySQL provider with CL.Data.</summary>
public sealed class MySqlDataLibrary : ILibrary
{
    /// <inheritdoc />
    public LibraryManifest Manifest { get; } = new()
    {
        Id = "CL.Data.MySql",
        Name = "CL.Data MySQL provider",
        Version = CL.Internal.InternalLibraryVersion.Current,
        Description = "MySQL / MariaDB provider for CL.Data (MySqlConnector).",
        Author = "Zyntal.com",
        Tags = ["database", "mysql", "cl.data"],
        Dependencies = [LibraryDependency.Required("CL.Data")],
    };

    /// <inheritdoc />
    public Task OnConfigureAsync(LibraryContext context)
    {
        var registered = DataProviderRegistry.Register(MySqlDataProvider.Instance);
        registered.ThrowIfFailure();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnInitializeAsync(LibraryContext context) => Task.CompletedTask;

    /// <inheritdoc />
    public Task OnStartAsync(LibraryContext context) => Task.CompletedTask;

    /// <inheritdoc />
    public Task OnStopAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public Task<HealthStatus> HealthCheckAsync() => Task.FromResult(HealthStatus.Healthy("MySQL provider registered"));

    /// <inheritdoc />
    public void Dispose() { }
}

/// <summary>
/// MySQL replica lag from <c>SHOW REPLICA STATUS</c> (<c>Seconds_Behind_Source</c>, MySQL 8.0.22+). No row: not a replica.
/// A NULL lag means the replication threads are stopped.
/// </summary>
public sealed class MySqlReplicaProbe : CL.Data.Routing.ReplicaProbe
{
    /// <inheritdoc />
    public override string Sql => "SHOW REPLICA STATUS";

    /// <inheritdoc />
    public override ValueTask<long?> ReadLagMsAsync(System.Data.Common.DbDataReader reader, CancellationToken ct) =>
        ValueTask.FromResult<long?>(Number(reader, "Seconds_Behind_Source") is { } seconds ? (long)(seconds * 1000) : Broken);
}

/// <summary>
/// MySQL bulk copy through <c>MySqlBulkCopy</c> (<c>LOAD DATA LOCAL INFILE</c>). Needs <c>MySql.AllowLoadLocalInfile</c> and the
/// server's <c>local_infile</c>; when either is off, CL.Data falls back to batched multi-row inserts.
/// </summary>
public sealed class MySqlBulkCopier : CL.Data.Bulk.BulkCopier
{
    private volatile bool _serverRefused;

    /// <inheritdoc />
    public override async Task<long?> CopyAsync(CL.Data.Bulk.BulkCopyRequest request, CancellationToken ct)
    {
        if (_serverRefused || request.Database.Config.MySql?.AllowLoadLocalInfile != true) return null;
        var bulk = new MySqlBulkCopy((MySqlConnection)request.Connection, (MySqlTransaction?)request.Transaction)
        {
            DestinationTableName = request.QuotedTable,
            BulkCopyTimeout = request.TimeoutSeconds,
        };
        for (var i = 0; i < request.Columns.Count; i++)
            bulk.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(i, request.Columns[i]));   // MySqlBulkCopy quotes names itself
        try
        {
            var result = await bulk.WriteToServerAsync(request.CreateReader(), ct).ConfigureAwait(false);
            return result.RowsInserted;
        }
        catch (MySqlException ex) when (ex.Number is 3948 or 1148)
        {
            // The server has local_infile off: remember it and batch inserts from now on.
            _serverRefused = true;
            return null;
        }
    }
}
