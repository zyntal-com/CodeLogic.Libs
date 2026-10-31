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
using Npgsql;
using NpgsqlTypes;

namespace CL.Data.PostgreSql;

/// <summary>The PostgreSQL provider for CL.Data (Npgsql).</summary>
public sealed class PostgreSqlDataProvider : DataProvider
{
    /// <summary>Provider name used in configuration.</summary>
    public const string ProviderName = "postgresql";

    /// <summary>The shared instance.</summary>
    public static PostgreSqlDataProvider Instance { get; } = new();

    private PostgreSqlDataProvider() { }

    /// <inheritdoc />
    public override string Name => ProviderName;

    /// <inheritdoc />
    public override int DefaultPort => 5432;

    /// <inheritdoc />
    public override ProviderCapabilities Capabilities { get; } = new()
    {
        ParameterLimit = 65535,
        MaxIdentifierLength = 63,
        Returning = true,
        ReturningPreservesOrder = true,
        NativeUpsert = true,
        Schemas = true,
        RowLocks = true,
        SkipLocked = true,
        NoWait = true,
        AdvisoryLocks = true,
        DbBatch = true,
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
    public override SqlDialect Dialect { get; } = new PostgreSqlSqlDialect();

    /// <inheritdoc />
    public override ITypeMapper Types { get; } = new PostgreSqlTypeMapper();

    /// <inheritdoc />
    public override IErrorClassifier Errors { get; } = new PostgreSqlErrorClassifier();

    /// <inheritdoc />
    public override CL.Data.Schema.DdlGenerator Ddl => field ??= new PostgreSqlDdlGenerator(Dialect);

    /// <inheritdoc />
    public override CL.Data.Schema.SchemaIntrospector Introspector { get; } = new PostgreSqlSchemaIntrospector();

    /// <inheritdoc />
    public override CL.Data.Sql.SqlText TryLockSql(string name) => $"SELECT pg_try_advisory_lock(hashtextextended({name}, 0))";

    /// <inheritdoc />
    public override CL.Data.Sql.SqlText UnlockSql(string name) => $"SELECT pg_advisory_unlock(hashtextextended({name}, 0))";

    /// <inheritdoc />
    public override string? GetDefaultSchema(DataConnectionConfig config) => config.PostgreSql?.DefaultSchema ?? "public";

    /// <inheritdoc />
    public override string NormalizeStoreType(string storeType)
    {
        var type = CL.Data.Schema.StoreTypeNormalizer.PostgreSqlTimes(base.NormalizeStoreType(storeType));
        foreach (var (alias, canonical) in TypeAliases)
        {
            if (type == alias) return canonical;
            if (type.StartsWith(alias + "(", StringComparison.Ordinal)) return canonical + type[alias.Length..];
        }
        return type;
    }

    // Identity pseudo-types and the long names pg_catalog reports, mapped to the names the type mapper writes.
    private static readonly (string Alias, string Canonical)[] TypeAliases =
    [
        ("bigserial", "bigint"), ("serial", "integer"), ("smallserial", "smallint"),
        ("int8", "bigint"), ("int4", "integer"), ("int2", "smallint"), ("int", "integer"),
        ("character varying", "varchar"), ("character", "char"), ("bool", "boolean"),
        ("timestamp with time zone", "timestamptz"), ("timestamp without time zone", "timestamp"),
        ("time without time zone", "time"), ("float8", "double precision"), ("float4", "real"), ("decimal", "numeric"),
    ];

    /// <inheritdoc />
    public override Result<string> BuildConnectionString(DataConnectionConfig config, NodeEndpoint node)
    {
        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(node.ConnectionString ?? "");
        }
        catch (ArgumentException ex)
        {
            return DataErrors.Validation("The PostgreSQL connection string is not valid.", ex.Message);
        }

        var options = config.PostgreSql ?? new PostgreSqlOptions();
        if (!string.IsNullOrWhiteSpace(node.Host))
        {
            var hosts = node.IsPrimary ? [node.Host, .. config.FailoverHosts] : new List<string> { node.Host };
            builder.Host = string.Join(",", hosts);
            // With several hosts, the primary connection must land on the writable server.
            if (hosts.Count > 1) builder.TargetSessionAttributes = "primary";
        }
        if (node.Port is { } port) builder.Port = port;
        if (!string.IsNullOrWhiteSpace(config.Database)) builder.Database = config.Database;
        if (node.Username is not null) builder.Username = node.Username;
        if (node.Password is not null) builder.Password = node.Password;

        builder.Pooling = config.Pooling;
        builder.MinPoolSize = config.MinPoolSize;
        builder.MaxPoolSize = config.MaxPoolSize;
        builder.ConnectionLifetime = config.ConnectionLifetimeSeconds;
        builder.Timeout = Math.Min(config.ConnectTimeoutSeconds, 1024);
        builder.CommandTimeout = config.CommandTimeoutSeconds;
        builder.MaxAutoPrepare = config.PrepareStatements ? options.MaxAutoPrepare : 0;
        builder.AutoPrepareMinUsages = Math.Max(1, options.AutoPrepareMinUsages);
        builder.Multiplexing = options.Multiplexing;

        switch (config.SslMode)
        {
            case DataSslMode.Disable: builder.SslMode = SslMode.Disable; break;
            case DataSslMode.Prefer: builder.SslMode = SslMode.Prefer; break;
            case DataSslMode.Require: builder.SslMode = SslMode.Require; break;
            case DataSslMode.VerifyCA: builder.SslMode = SslMode.VerifyCA; break;
            case DataSslMode.VerifyFull: builder.SslMode = SslMode.VerifyFull; break;
        }
        if (!string.IsNullOrWhiteSpace(config.SslCaCertificatePath)) builder.RootCertificate = config.SslCaCertificatePath;
        return builder.ConnectionString;
    }

    /// <inheritdoc />
    public override DbDataSource CreateDataSource(DataConnectionConfig config, NodeEndpoint node, string connectionString) =>
        NpgsqlDataSource.Create(connectionString);

    /// <inheritdoc />
    public override void Validate(DataConnectionConfig config, string path, List<ConfigFieldError> errors)
    {
        if (config.PostgreSql is { DefaultSchema: { Length: 0 } })
            errors.Add(new($"{path}.postgreSql.defaultSchema", "A default schema is required."));
    }
}

/// <summary>
/// PostgreSQL type mapping: native <c>boolean</c>, <c>uuid</c>, <c>timestamptz</c> (UTC), <c>jsonb</c> and <c>bytea</c>.
/// Parameters for JSON columns are typed <c>jsonb</c>; time values are sent as UTC (Npgsql rejects other offsets for
/// <c>timestamptz</c>).
/// </summary>
public sealed class PostgreSqlTypeMapper : ITypeMapper
{
    /// <inheritdoc />
    public string GetStoreType(ColumnMap column, DataConnectionConfig config)
    {
        if (column.Hint<PostgresColumnAttribute>()?.Type is { } hinted) return hinted;
        if (column.NativeType is { } native) return native;
        var size = column.Size > 0 ? column.Size : config.DefaultStringSize;
        var fraction = column.Precision is > 0 and <= 6 ? column.Precision : 6;
        var identity = column.Has(ColumnRoles.Identity);
        return column.DataType switch
        {
            DataType.Boolean => "boolean",
            DataType.TinyInt or DataType.SmallInt => identity ? "smallserial" : "smallint",
            DataType.Int => identity ? "serial" : "integer",
            DataType.BigInt => identity ? "bigserial" : "bigint",
            DataType.Decimal => string.Create(CultureInfo.InvariantCulture,
                $"numeric({(column.Precision > 0 ? column.Precision : 18)},{(column.Scale >= 0 ? column.Scale : 2)})"),
            DataType.Real => "real",
            DataType.Double => "double precision",
            DataType.String when column.Size < 0 => "text",
            DataType.String => string.Create(CultureInfo.InvariantCulture, $"varchar({size})"),
            DataType.FixedString => string.Create(CultureInfo.InvariantCulture, $"char({(column.Size > 0 ? column.Size : 1)})"),
            DataType.Text or DataType.Xml => column.DataType == DataType.Xml ? "xml" : "text",
            DataType.Binary or DataType.FixedBinary or DataType.Blob => "bytea",
            DataType.Guid => "uuid",
            DataType.Date => "date",
            DataType.Time => string.Create(CultureInfo.InvariantCulture, $"time({fraction})"),
            DataType.DateTime or DataType.DateTimeOffset => string.Create(CultureInfo.InvariantCulture, $"timestamptz({fraction})"),
            DataType.Interval => "interval",
            DataType.Json => "jsonb",
            DataType.RowVersion => "bigint",
            DataType.Geometry or DataType.Geography => "bytea",
            _ => string.Create(CultureInfo.InvariantCulture, $"varchar({size})"),
        };
    }

    /// <inheritdoc />
    public DbParameter CreateParameter<TValue>(string name, TValue value, ColumnMap? column)
    {
        var parameter = value switch
        {
            null => new NpgsqlParameter(name, DBNull.Value),
            DateTimeOffset dto => new NpgsqlParameter<DateTimeOffset>(name, dto.ToUniversalTime()),
            DateTime dt => new NpgsqlParameter<DateTime>(name, dt.Kind switch
            {
                DateTimeKind.Utc => dt,
                DateTimeKind.Local => dt.ToUniversalTime(),
                _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
            }),
            _ => new NpgsqlParameter<TValue>(name, value),
        };
        if (column?.DataType == DataType.Json) parameter.NpgsqlDbType = NpgsqlDbType.Jsonb;
        return parameter;
    }
}

/// <summary>Maps PostgreSQL SQLSTATE codes to CL.Data errors.</summary>
public sealed class PostgreSqlErrorClassifier : IErrorClassifier
{
    /// <inheritdoc />
    public ErrorClassification Classify(Exception exception)
    {
        if (exception is PostgresException pg)
        {
            var details = $"PostgreSQL {pg.SqlState}: {pg.MessageText}" + (pg.ConstraintName is null ? "" : $" (constraint {pg.ConstraintName})");
            return pg.SqlState switch
            {
                "23505" => new(DataErrors.DuplicateKey("A row with the same key already exists.", details), false),
                "23503" => new(DataErrors.ForeignKeyViolation("A foreign key constraint rejected the change.", details), false),
                "23502" or "23514" or "23P01" => new(DataErrors.ConstraintViolation("A constraint rejected the change.", details), false),
                // A serialization failure is retried like a deadlock: the transaction can succeed on a new attempt.
                "40P01" or "40001" => new(DataErrors.Deadlock(details), true),
                "55P03" => new(DataErrors.LockNotAcquired("A requested row is locked by another transaction.", details), false),
                "57014" => new(DataErrors.Timeout("The PostgreSQL statement was cancelled or timed out.", details), true),
                "28000" or "28P01" or "42501" => new(DataErrors.AccessDenied("The database refused the credentials or permission.", details), false),
                "25006" => new(DataErrors.Unavailable("The PostgreSQL server is read-only (a standby or failover in progress).", details), true),
                "57P01" or "57P02" or "57P03" or "53300" => new(DataErrors.Unavailable("The PostgreSQL server is not accepting connections.", details), true),
                _ when pg.SqlState.StartsWith("08", StringComparison.Ordinal) =>
                    new(DataErrors.Unavailable("The PostgreSQL server could not be reached.", details), true),
                _ => new(DataErrors.ProviderError("The PostgreSQL operation failed.", details), pg.IsTransient),
            };
        }

        if (exception is NpgsqlException npgsql)
        {
            return npgsql.InnerException is TimeoutException
                ? new(DataErrors.Timeout("The PostgreSQL operation timed out.", npgsql.Message), true)
                : new(DataErrors.Unavailable("The PostgreSQL server could not be reached.", npgsql.Message), npgsql.IsTransient);
        }

        return new(DataErrors.FromException(exception), exception is TimeoutException);
    }
}

/// <summary>CodeLogic library that registers the PostgreSQL provider with CL.Data.</summary>
public sealed class PostgreSqlDataLibrary : ILibrary
{
    /// <inheritdoc />
    public LibraryManifest Manifest { get; } = new()
    {
        Id = "CL.Data.PostgreSql",
        Name = "CL.Data PostgreSQL provider",
        Version = CL.Internal.InternalLibraryVersion.Current,
        Description = "PostgreSQL provider for CL.Data (Npgsql).",
        Author = "Zyntal.com",
        Tags = ["database", "postgresql", "cl.data"],
    };

    /// <inheritdoc />
    public Task OnConfigureAsync(LibraryContext context)
    {
        var registered = DataProviderRegistry.Register(PostgreSqlDataProvider.Instance);
        if (registered.IsFailure) throw new InvalidOperationException(registered.Error!.ToString());
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnInitializeAsync(LibraryContext context) => Task.CompletedTask;

    /// <inheritdoc />
    public Task OnStartAsync(LibraryContext context) => Task.CompletedTask;

    /// <inheritdoc />
    public Task OnStopAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public Task<HealthStatus> HealthCheckAsync() => Task.FromResult(HealthStatus.Healthy("PostgreSQL provider registered"));

    /// <inheritdoc />
    public void Dispose() { }
}
