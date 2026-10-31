using System.Data;
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
using Microsoft.Data.SqlClient;

namespace CL.Data.SqlServer;

/// <summary>The SQL Server / Azure SQL provider for CL.Data (Microsoft.Data.SqlClient).</summary>
public sealed class SqlServerDataProvider : DataProvider
{
    /// <summary>Provider name used in configuration.</summary>
    public const string ProviderName = "sqlserver";

    /// <summary>The shared instance.</summary>
    public static SqlServerDataProvider Instance { get; } = new();

    private SqlServerDataProvider() { }

    /// <inheritdoc />
    public override string Name => ProviderName;

    /// <inheritdoc />
    public override int DefaultPort => 1433;

    /// <inheritdoc />
    public override ProviderCapabilities Capabilities { get; } = new()
    {
        ParameterLimit = 2098,          // 2100 minus headroom for parameters added by the provider
        MaxIdentifierLength = 128,
        Returning = true,
        ReturningPreservesOrder = false, // OUTPUT does not promise insert order
        NativeUpsert = false,
        Schemas = true,
        RowLocks = true,
        SkipLocked = true,
        NoWait = true,
        AdvisoryLocks = true,
        DbBatch = false,
        MultipleResultSets = true,
        Savepoints = true,
        TransactionalDdl = true,
        Replicas = true,
        NativeBulkCopy = true,
        Json = true,
        FullText = true,
        IncludeColumns = true,
        FilteredIndexes = true,
    };

    /// <inheritdoc />
    public override SqlDialect Dialect { get; } = new SqlServerSqlDialect();

    /// <inheritdoc />
    public override ITypeMapper Types { get; } = new SqlServerTypeMapper();

    /// <inheritdoc/>
    public override string? GetColumnCollation(ColumnMap column) => column.Hint<SqlServerColumnAttribute>()?.Collation;

    /// <inheritdoc />
    public override IErrorClassifier Errors { get; } = new SqlServerErrorClassifier();

    /// <inheritdoc />
    public override CL.Data.Schema.DdlGenerator Ddl => field ??= new SqlServerDdlGenerator(Dialect);

    /// <inheritdoc />
    public override CL.Data.Schema.SchemaIntrospector Introspector { get; } = new SqlServerSchemaIntrospector();

    /// <inheritdoc />
    public override CL.Data.Routing.ReplicaProbe ReplicaProbe { get; } = new SqlServerReplicaProbe();

    /// <inheritdoc />
    public override string ServerVersionSql => "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128))";

    /// <inheritdoc />
    public override CL.Data.Bulk.BulkCopier BulkCopier { get; } = new SqlServerBulkCopier();

    /// <inheritdoc />
    public override CL.Data.Telemetry.QueryExplainer Explainer { get; } = new SqlServerExplainer();

    /// <inheritdoc />
    public override CL.Data.Admin.DatabaseAdmin DatabaseAdmin { get; } = new SqlServerDatabaseAdmin();

    /// <inheritdoc />
    public override CL.Data.Sql.SqlText TryLockSql(string name) =>
        $"DECLARE @r int; EXEC @r = sp_getapplock @Resource = {name}, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT CASE WHEN @r >= 0 THEN 1 ELSE 0 END";

    /// <inheritdoc />
    public override CL.Data.Sql.SqlText UnlockSql(string name) => $"EXEC sp_releaseapplock @Resource = {name}, @LockOwner = 'Session'";

    /// <inheritdoc />
    public override string NormalizeStoreType(string storeType) => CL.Data.Schema.StoreTypeNormalizer.SqlServer(base.NormalizeStoreType(storeType));

    /// <inheritdoc />
    public override string? GetDefaultSchema(DataConnectionConfig config) => config.SqlServer?.DefaultSchema ?? "dbo";

    /// <inheritdoc />
    public override Result<string> BuildConnectionString(DataConnectionConfig config, NodeEndpoint node)
    {
        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(node.ConnectionString ?? "");
        }
        catch (ArgumentException ex)
        {
            return DataErrors.Validation("The SQL Server connection string is not valid.", ex.Message);
        }

        var options = config.SqlServer ?? new SqlServerOptions();
        if (!string.IsNullOrWhiteSpace(node.Host))
            builder.DataSource = node.Port is { } port ? string.Create(CultureInfo.InvariantCulture, $"{node.Host},{port}") : node.Host;
        if (!string.IsNullOrWhiteSpace(config.Database)) builder.InitialCatalog = config.Database;

        switch (options.Authentication)
        {
            case SqlServerAuthentication.SqlPassword:
                if (node.Username is not null) builder.UserID = node.Username;
                if (node.Password is not null) builder.Password = node.Password;
                break;
            case SqlServerAuthentication.Integrated:
                builder.IntegratedSecurity = true;
                break;
            case SqlServerAuthentication.EntraDefault:
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryDefault;
                break;
            case SqlServerAuthentication.EntraManagedIdentity:
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryManagedIdentity;
                if (node.Username is not null) builder.UserID = node.Username;   // client id of a user-assigned identity
                break;
            case SqlServerAuthentication.EntraServicePrincipal:
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryServicePrincipal;
                if (node.Username is not null) builder.UserID = node.Username;
                if (node.Password is not null) builder.Password = node.Password;
                break;
        }

        builder.Pooling = config.Pooling;
        builder.MinPoolSize = config.MinPoolSize;
        builder.MaxPoolSize = config.MaxPoolSize;
        builder.LoadBalanceTimeout = config.ConnectionLifetimeSeconds;
        builder.ConnectTimeout = config.ConnectTimeoutSeconds;
        builder.CommandTimeout = config.CommandTimeoutSeconds;
        // Values written in the connection string win over the SqlServer option defaults (like sslMode on the other
        // providers); an explicit sslMode below still applies.
        var written = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = node.ConnectionString ?? "" };
        if (!written.ContainsKey("Encrypt"))
            builder.Encrypt = options.Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional;
        if (!written.ContainsKey("TrustServerCertificate") && !written.ContainsKey("Trust Server Certificate"))
            builder.TrustServerCertificate = options.TrustServerCertificate;
        builder.MultiSubnetFailover = options.MultiSubnetFailover;
        if (!string.IsNullOrWhiteSpace(options.FailoverPartner) && node.IsPrimary) builder.FailoverPartner = options.FailoverPartner;
        if (!string.IsNullOrWhiteSpace(options.ApplicationName)) builder.ApplicationName = options.ApplicationName;
        // Replicas of an availability group are reached through the listener with read-only intent.
        if (!node.IsPrimary) builder.ApplicationIntent = ApplicationIntent.ReadOnly;
        if (config.SslMode == DataSslMode.Disable) builder.Encrypt = SqlConnectionEncryptOption.Optional;
        if (config.SslMode is DataSslMode.VerifyCA or DataSslMode.VerifyFull) builder.TrustServerCertificate = false;
        return builder.ConnectionString;
    }

    /// <inheritdoc />
    public override DbDataSource CreateDataSource(DataConnectionConfig config, NodeEndpoint node, string connectionString) =>
        SqlClientFactory.Instance.CreateDataSource(connectionString);

    /// <inheritdoc />
    public override void Validate(DataConnectionConfig config, string path, List<ConfigFieldError> errors)
    {
        if (config.SqlServer is { DefaultSchema: { Length: 0 } })
            errors.Add(new($"{path}.sqlServer.defaultSchema", "A default schema is required."));
        if (config.FailoverHosts.Count > 0)
            errors.Add(new($"{path}.failoverHosts", "SQL Server uses SqlServer.FailoverPartner or an availability group listener instead of failover hosts."));
    }
}

/// <summary>
/// SQL Server type mapping. <c>DateTime</c> binds as <c>datetime2</c> (SqlClient would pick the 3.33 ms <c>datetime</c>),
/// <c>DateTimeOffset</c> as <c>datetimeoffset</c> in UTC, and strings in fixed size buckets (4000 or max) so the server
/// reuses query plans.
/// </summary>
public sealed class SqlServerTypeMapper : ITypeMapper
{
    /// <inheritdoc />
    public string GetStoreType(ColumnMap column, DataConnectionConfig config)
    {
        if (column.Hint<SqlServerColumnAttribute>()?.Type is { } hinted) return hinted;
        if (column.NativeType is { } native) return native;
        var size = column.Size > 0 ? column.Size : config.DefaultStringSize;
        var fraction = column.Precision is > 0 and <= 7 ? column.Precision : 7;
        return column.DataType switch
        {
            DataType.Boolean => "bit",
            DataType.TinyInt => "tinyint",
            DataType.SmallInt => "smallint",
            DataType.Int => "int",
            DataType.BigInt => "bigint",
            DataType.Decimal => string.Create(CultureInfo.InvariantCulture,
                $"decimal({(column.Precision > 0 ? column.Precision : 18)},{(column.Scale >= 0 ? column.Scale : 2)})"),
            DataType.Real => "real",
            DataType.Double => "float",
            DataType.String when column.Size < 0 || size > 4000 => "nvarchar(max)",
            DataType.String => string.Create(CultureInfo.InvariantCulture, $"nvarchar({size})"),
            DataType.FixedString => string.Create(CultureInfo.InvariantCulture, $"nchar({(column.Size > 0 ? column.Size : 1)})"),
            DataType.Text or DataType.Json => "nvarchar(max)",
            DataType.Binary when column.Size is > 0 and <= 8000 => string.Create(CultureInfo.InvariantCulture, $"varbinary({column.Size})"),
            DataType.FixedBinary => string.Create(CultureInfo.InvariantCulture, $"binary({(column.Size > 0 ? column.Size : 16)})"),
            DataType.Binary or DataType.Blob => "varbinary(max)",
            DataType.Guid => "uniqueidentifier",
            DataType.Date => "date",
            DataType.Time => string.Create(CultureInfo.InvariantCulture, $"time({fraction})"),
            DataType.DateTime => string.Create(CultureInfo.InvariantCulture, $"datetime2({fraction})"),
            DataType.DateTimeOffset => string.Create(CultureInfo.InvariantCulture, $"datetimeoffset({fraction})"),
            DataType.Interval => "time(7)",
            DataType.Xml => "xml",
            DataType.RowVersion => "rowversion",
            DataType.Geometry => "geometry",
            DataType.Geography => "geography",
            _ => string.Create(CultureInfo.InvariantCulture, $"nvarchar({size})"),
        };
    }

    /// <inheritdoc />
    public DbParameter CreateParameter<TValue>(string name, TValue value, ColumnMap? column)
    {
        switch (value)
        {
            case null:
                return new SqlParameter(name, DBNull.Value);
            case string s:
                return new SqlParameter(name, SqlDbType.NVarChar, s.Length <= 4000 ? 4000 : -1) { Value = s };
            case DateTime dt:
                return new SqlParameter(name, SqlDbType.DateTime2)
                {
                    Value = dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt,
                };
            case DateTimeOffset dto:
                return new SqlParameter(name, SqlDbType.DateTimeOffset) { Value = dto.ToUniversalTime() };
            case byte[] bytes:
                return new SqlParameter(name, SqlDbType.VarBinary, bytes.Length <= 8000 ? 8000 : -1) { Value = bytes };
            case decimal m when column is { Precision: > 0 }:
                return new SqlParameter(name, SqlDbType.Decimal)
                {
                    Value = m, Precision = (byte)column.Precision, Scale = (byte)Math.Max(0, column.Scale),
                };
            default:
                return new SqlParameter(name, value);
        }
    }
}

/// <summary>Maps SQL Server error numbers to CL.Data errors.</summary>
public sealed class SqlServerErrorClassifier : IErrorClassifier
{
    // Connection and Azure SQL errors that a retry can fix.
    private static readonly HashSet<int> Transient =
    [
        -2, 20, 64, 121, 233, 1205, 4060, 4221, 10053, 10054, 10060, 10928, 10929, 40143, 40197, 40501, 40540, 40613,
        42108, 42109, 49918, 49919, 49920,
    ];

    /// <inheritdoc />
    public ErrorClassification Classify(Exception exception)
    {
        if (exception is not SqlException sql)
            return new(DataErrors.FromException(exception), exception is TimeoutException);

        var details = $"SQL Server error {sql.Number}: {sql.Message}";
        return sql.Number switch
        {
            2627 or 2601 => new(DataErrors.DuplicateKey("A row with the same key already exists.", details), false),
            547 when sql.Message.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase) || sql.Message.Contains("REFERENCE", StringComparison.OrdinalIgnoreCase)
                => new(DataErrors.ForeignKeyViolation("A foreign key constraint rejected the change.", details), false),
            547 or 515 => new(DataErrors.ConstraintViolation("A constraint rejected the change.", details), false),
            1205 or 3960 => new(DataErrors.Deadlock(details), true),
            1222 => new(DataErrors.LockNotAcquired("A requested row is locked by another transaction.", details), false),
            -2 => new(DataErrors.Timeout("The SQL Server command timed out.", details), true),
            18456 or 229 or 230 or 262 or 916 => new(DataErrors.AccessDenied("The database refused the credentials or permission.", details), false),
            3906 => new(DataErrors.Unavailable("The SQL Server database is read-only.", details), true),
            _ when Transient.Contains(sql.Number) => new(DataErrors.Unavailable("SQL Server is temporarily unavailable.", details), true),
            _ => new(DataErrors.ProviderError("The SQL Server operation failed.", details), sql.IsTransient),
        };
    }
}

/// <summary>CodeLogic library that registers the SQL Server provider with CL.Data.</summary>
public sealed class SqlServerDataLibrary : ILibrary
{
    /// <inheritdoc />
    public LibraryManifest Manifest { get; } = new()
    {
        Id = "CL.Data.SqlServer",
        Name = "CL.Data SQL Server provider",
        Version = CL.Internal.InternalLibraryVersion.Current,
        Description = "SQL Server / Azure SQL provider for CL.Data (Microsoft.Data.SqlClient).",
        Author = "Zyntal.com",
        Tags = ["database", "sqlserver", "mssql", "cl.data"],
        Dependencies = [LibraryDependency.Required("CL.Data")],
    };

    /// <inheritdoc />
    public Task OnConfigureAsync(LibraryContext context)
    {
        var registered = DataProviderRegistry.Register(SqlServerDataProvider.Instance);
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
    public Task<HealthStatus> HealthCheckAsync() => Task.FromResult(HealthStatus.Healthy("SQL Server provider registered"));

    /// <inheritdoc />
    public void Dispose() { }
}

/// <summary>
/// SQL Server replica lag from <c>sys.dm_hadr_database_replica_states</c> (<c>secondary_lag_seconds</c>, SQL Server 2016+)
/// for the current database on the local replica. No row: not in an availability group.
/// </summary>
public sealed class SqlServerReplicaProbe : CL.Data.Routing.ReplicaProbe
{
    /// <inheritdoc />
    public override string Sql =>
        "SELECT secondary_lag_seconds, synchronization_state_desc FROM sys.dm_hadr_database_replica_states " +
        "WHERE is_local = 1 AND database_id = DB_ID()";

    /// <inheritdoc />
    public override ValueTask<long?> ReadLagMsAsync(System.Data.Common.DbDataReader reader, CancellationToken ct)
    {
        var state = reader.GetOrdinal("synchronization_state_desc") is var i && !reader.IsDBNull(i) ? reader.GetString(i) : null;
        if (state is "NOT SYNCHRONIZING") return ValueTask.FromResult<long?>(Broken);
        return ValueTask.FromResult<long?>(Number(reader, "secondary_lag_seconds") is { } seconds ? (long)(seconds * 1000) : 0);
    }
}

/// <summary>
/// SQL Server plans through <c>SET SHOWPLAN_TEXT</c>: the statement is compiled, not executed. Showplan returns nothing for a
/// parameterized RPC call, so the parameters are declared as variables of the same types in a plain batch.
/// </summary>
public sealed class SqlServerExplainer : CL.Data.Telemetry.QueryExplainer
{
    /// <inheritdoc />
    public override async Task<string?> ExplainAsync(CL.Data.Telemetry.ExplainSession session, CancellationToken ct)
    {
        var batch = new System.Text.StringBuilder();
        foreach (var parameter in session.Parameters().OfType<SqlParameter>())
        {
            var name = parameter.ParameterName.StartsWith('@') ? parameter.ParameterName : "@" + parameter.ParameterName;
            batch.Append("DECLARE ").Append(name).Append(' ').Append(TypeOf(parameter)).Append(";\n");
        }
        batch.Append(session.Sql);

        await session.ExecuteAsync("SET SHOWPLAN_TEXT ON", ct).ConfigureAwait(false);
        try
        {
            var lines = await session.ReadAsync(batch.ToString(), withParameters: false, ct).ConfigureAwait(false);
            return string.Join('\n', lines.Where(l => !l.StartsWith("DECLARE", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            await session.ExecuteAsync("SET SHOWPLAN_TEXT OFF", CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static string TypeOf(SqlParameter p) => p.SqlDbType switch
    {
        System.Data.SqlDbType.NVarChar or System.Data.SqlDbType.VarChar or System.Data.SqlDbType.NChar or System.Data.SqlDbType.Char
            or System.Data.SqlDbType.VarBinary or System.Data.SqlDbType.Binary =>
            $"{p.SqlDbType.ToString().ToLowerInvariant()}({(p.Size is > 0 and <= 8000 ? p.Size.ToString(CultureInfo.InvariantCulture) : "max")})",
        System.Data.SqlDbType.Decimal => $"decimal({(p.Precision > 0 ? p.Precision : (byte)38)},{p.Scale})",
        System.Data.SqlDbType.DateTime2 or System.Data.SqlDbType.DateTimeOffset or System.Data.SqlDbType.Time =>
            $"{p.SqlDbType.ToString().ToLowerInvariant()}({(p.Scale > 0 ? p.Scale : (byte)7)})",
        System.Data.SqlDbType.Structured or System.Data.SqlDbType.Udt or System.Data.SqlDbType.Variant => "sql_variant",
        _ => p.SqlDbType.ToString().ToLowerInvariant(),
    };
}

/// <summary>SQL Server bulk copy through <c>SqlBulkCopy</c> (streamed, constraints checked, identity kept on request).</summary>
public sealed class SqlServerBulkCopier : CL.Data.Bulk.BulkCopier
{
    /// <inheritdoc />
    public override async Task<long?> CopyAsync(CL.Data.Bulk.BulkCopyRequest request, CancellationToken ct)
    {
        var options = SqlBulkCopyOptions.CheckConstraints | (request.KeepIdentity ? SqlBulkCopyOptions.KeepIdentity : SqlBulkCopyOptions.Default);
        using var bulk = new SqlBulkCopy((SqlConnection)request.Connection, options, (SqlTransaction?)request.Transaction)
        {
            DestinationTableName = request.QuotedTable,
            BulkCopyTimeout = request.TimeoutSeconds,
            EnableStreaming = true,
        };
        foreach (var column in request.Columns) bulk.ColumnMappings.Add(column, column);
        await using var reader = request.CreateReader();
        await bulk.WriteToServerAsync(reader, ct).ConfigureAwait(false);
        return bulk.RowsCopied64;
    }
}
