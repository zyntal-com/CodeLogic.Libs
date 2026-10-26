using CL.MSSQL.Models;
using CodeLogic.Core.Configuration;
using Microsoft.Data.SqlClient;

namespace CL.MSSQL.Configuration;

[ConfigSection("mssql")]
public sealed class DatabaseConfiguration : ConfigModelBase
{
    public Dictionary<string, SqlServerDatabaseConfig> Databases { get; set; } = new()
    {
        ["Default"] = new()
    };

    public override ConfigValidationResult Validate()
    {
        var errors = new List<string>();
        if (Databases.Count == 0) errors.Add("At least one database configuration is required");
        foreach (var (name, database) in Databases)
        {
            var result = database.Validate();
            if (!result.IsValid) errors.Add($"Database '{name}': {string.Join(", ", result.Errors)}");
        }
        return errors.Count == 0 ? ConfigValidationResult.Valid() : ConfigValidationResult.Invalid(errors);
    }
}

/// <summary>Authentication mode used by structured SQL Server configuration.</summary>
public enum SqlServerAuthenticationMode
{
    SqlLogin,
    IntegratedSecurity
}

/// <summary>Per-database SQL Server and Azure SQL settings.</summary>
public sealed class SqlServerDatabaseConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>An authoritative provider connection string. When set, all structured connection fields are ignored.</summary>
    [ConfigField(Label = "Connection String", InputType = ConfigInputType.Password, Secret = true, RequiresRestart = true, Group = "Connection")]
    public string? ConnectionString { get; set; }

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1433;
    public string? Instance { get; set; }
    public string Database { get; set; } = string.Empty;
    public SqlServerAuthenticationMode AuthenticationMode { get; set; } = SqlServerAuthenticationMode.SqlLogin;
    public string Username { get; set; } = string.Empty;

    [ConfigField(Label = "Password", InputType = ConfigInputType.Password, Secret = true, RequiresRestart = true, Group = "Connection")]
    public string Password { get; set; } = string.Empty;

    public bool Encrypt { get; set; } = true;
    public bool TrustServerCertificate { get; set; }
    public bool EnablePooling { get; set; } = true;
    public int MinPoolSize { get; set; }
    public int MaxPoolSize { get; set; } = 100;
    public int ConnectionLifetime { get; set; } = 300;
    public int ConnectionTimeout { get; set; } = 30;
    public int CommandTimeout { get; set; } = 30;
    public string DefaultSchema { get; set; } = "dbo";
    public SyncMode SyncMode { get; set; } = SyncMode.Production;
    public SchemaSyncLevel SchemaSyncLevel { get; set; } = SchemaSyncLevel.Safe;
    public bool AllowDestructiveSync { get; set; }
    public bool IsMigrationMode => SyncMode == SyncMode.Migration;
    public SchemaSyncLevel EffectiveSyncLevel => SyncMode switch
    {
        SyncMode.Developer or SyncMode.Migration => SchemaSyncLevel.Full,
        _ => AllowDestructiveSync ? SchemaSyncLevel.Full : SchemaSyncLevel
    };
    /// <summary>
    /// Directory schema backups are written to. <c>null</c> keeps the default
    /// <c>&lt;DataDirectory&gt;/backups</c>. A relative path is resolved against the data
    /// directory; an absolute path is used as given.
    /// </summary>
    [ConfigField(Label = "Backup Directory",
        Description = "Where schema backups are written. Empty uses <DataDirectory>/backups.",
        Group = "Schema")]
    public string? BackupDirectory { get; set; }

    /// <summary>Elapsed milliseconds at or above which a query raises <c>SlowQueryEvent</c>.</summary>
    [ConfigField(Label = "Slow Query Threshold (ms)", Min = 0,
        Description = "Queries at or above this elapsed time raise SlowQueryEvent.",
        Group = "Observability")]
    public int SlowQueryThresholdMs { get; set; } = 1000;

    /// <summary>
    /// Per-database override of the global <c>mssql.cache.Enabled</c> switch. <c>null</c>
    /// means "no override" and the global switch decides; <c>true</c>/<c>false</c> wins over it
    /// for queries running on this connection id.
    /// </summary>
    [ConfigField(Label = "Cache Enabled Override",
        Description = "Overrides the global cache switch for this database. Empty = no override.",
        Group = "Cache")]
    public bool? CacheEnabledOverride { get; set; }

    /// <summary>
    /// Command timeout applied to the query commands this library issues on this connection
    /// (repositories, the query builder, projections, joins and grouped queries), in
    /// milliseconds. It overrides the connection string's <c>Command Timeout</c> for those
    /// commands; the default of 30 s matches the provider default. 0 disables the override.
    /// </summary>
    [ConfigField(Label = "Query Timeout (ms)", Min = 0,
        Description = "Command timeout for library-issued query commands. 0 leaves the connection-string value in place.",
        Group = "Performance")]
    public int QueryTimeoutMs { get; set; } = 30_000;

    /// <summary>
    /// Largest number of rows the repository sends per multi-row INSERT/UPSERT statement.
    /// Also capped dynamically so a batch stays under SQL Server's 2,100-parameter limit.
    /// </summary>
    [ConfigField(Label = "Max Batch Insert Size", Min = 1,
        Description = "Rows per multi-row INSERT/UPSERT. Capped further by the 2,100-parameter limit.",
        Group = "Performance")]
    public int MaxBatchInsertSize { get; set; } = 500;

    /// <summary>
    /// When true, a SQL Server duplicate key error (2627 / 2601) in <c>InsertAsync</c> and
    /// <c>InsertManyAsync</c> returns the distinct error code <c>mssql.duplicate_key</c> and logs
    /// at Warning instead of Error. When false (the default), the error code is
    /// <c>mssql.insert_failed</c> / <c>mssql.bulk_insert_failed</c> and logs at Error.
    /// </summary>
    [ConfigField(Label = "Distinguish Duplicate Key Errors",
        Description = "When true, SQL Server duplicate key errors (2627/2601) return error code mssql.duplicate_key and log at Warning. Default: false.",
        Group = "Advanced", Collapsed = true)]
    public bool DistinguishDuplicateKeyError { get; set; } = false;

    /// <summary>
    /// Advisory ceiling on the length of a generated <c>IN (...)</c> list. Exceeding it does
    /// not fail or chunk the query — it logs one warning per query build naming the entity and
    /// the value count, so an unbounded <c>Contains</c> shows up before the server rejects it.
    /// </summary>
    [ConfigField(Label = "Max IN Clause Values", Min = 1,
        Description = "Advisory cap for generated IN (...) lists. Exceeding it logs one warning per query build; nothing is chunked or rejected.",
        Group = "Performance")]
    public int MaxInClauseValues { get; set; } = 1_000;

    /// <summary>
    /// Not used by this library. Statement caching is the provider's concern: set
    /// <c>Max Pool Size</c> / <c>Enlist</c> and related options on the connection string, and
    /// note that <c>Microsoft.Data.SqlClient</c> has no client-side prepared-statement cache
    /// to size — server-side plan caching is automatic.
    /// </summary>
    [Obsolete("Not applied. Statement caching is handled by SQL Server's plan cache and by " +
              "Microsoft.Data.SqlClient itself; there is no client-side statement cache to size. " +
              "Configure pooling on the connection string instead.")]
    [ConfigField(Label = "Prepared Statement Cache Size", Min = 0,
        Description = "Obsolete. Not applied — Microsoft.Data.SqlClient has no client-side statement cache to size.",
        Group = "Performance", Collapsed = true)]
    public int PreparedStatementCacheSize { get; set; } = 256;

    /// <summary>Deadlock / lock-timeout / Azure transient retries. 0 disables retrying.</summary>
    [ConfigField(Label = "Transient Retry Count", Min = 0, Max = 255,
        Description = "Retries for deadlock, lock timeout and Azure transient errors. 0 disables.",
        Group = "Resilience")]
    public int TransientRetryCount { get; set; } = 3;

    /// <summary>Base backoff for a transient retry; grows exponentially with jitter.</summary>
    [ConfigField(Label = "Transient Retry Base Delay (ms)", Min = 0,
        Description = "Base backoff between transient retries; exponential with jitter.",
        Group = "Resilience")]
    public int TransientRetryBaseDelayMs { get; set; } = 50;

    /// <summary>
    /// Executions of one normalized statement, on this connection, within a one-second window
    /// that raise <c>N1QueryDetectedEvent</c>. 0 (the default) disables the detector entirely —
    /// nothing is counted and nothing is allocated on the query path.
    /// </summary>
    [ConfigField(Label = "N+1 Detector Threshold", Min = 0,
        Description = "Repeats of one statement within a second that raise N1QueryDetectedEvent. 0 disables the detector.",
        Group = "Observability")]
    public int N1DetectorThreshold { get; set; }

    /// <summary>
    /// When a query crosses <see cref="SlowQueryThresholdMs"/>, attach the estimated ShowPlan
    /// XML from SQL Server's plan cache to <c>SlowQueryEvent.ExplainJson</c>. Strictly
    /// best-effort and run on its own connection, never inside the caller's transaction: if the
    /// plan is not in cache, or the lookup fails, the event still publishes with a null payload.
    /// </summary>
    [ConfigField(Label = "Capture Explain On Slow Query",
        Description = "Attach best-effort estimated ShowPlan XML to SlowQueryEvent. Never fails the query.",
        Group = "Observability")]
    public bool CaptureExplainOnSlowQuery { get; set; } = true;

    /// <summary>
    /// Size used for a string column that has no explicit <c>[Column(Size = ...)]</c> when
    /// schema sync infers its type — an unsized <c>string</c> becomes <c>nvarchar(n)</c>.
    /// Changing it alters the generated DDL and therefore the schema CRC, so existing tables
    /// are reconciled on the next sync.
    /// </summary>
    [ConfigField(Label = "Default String Size", Min = 1, Max = 4000,
        Description = "nvarchar length inferred for a string column with no explicit Size.",
        Group = "Schema")]
    public int DefaultStringSize { get; set; } = 255;

    /// <summary>
    /// When true, the library creates the configured database (the <c>Initial Catalog</c> of
    /// the built connection string, so a raw <see cref="ConnectionString"/> works too) during
    /// initialization if the server does not have it yet. The database gets the server's
    /// default collation. Needs a login allowed to run CREATE DATABASE. Default: false.
    /// </summary>
    [ConfigField(Label = "Create Database If Missing",
        Description = "Create the database at startup when it does not exist. Requires CREATE DATABASE permission.",
        RequiresRestart = true, Group = "Advanced", Collapsed = true)]
    public bool CreateDatabaseIfMissing { get; set; } = false;

    /// <summary>
    /// The database this configuration connects to: the <c>Initial Catalog</c> of
    /// <see cref="BuildConnectionString"/>, which a raw <see cref="ConnectionString"/> may set
    /// independently of <see cref="Database"/>.
    /// </summary>
    internal string EffectiveDatabase => new SqlConnectionStringBuilder(BuildConnectionString()).InitialCatalog;

    public string BuildConnectionString()
    {
        if (!string.IsNullOrWhiteSpace(ConnectionString)) return ConnectionString;

        var server = string.IsNullOrWhiteSpace(Instance)
            ? $"{Host},{Port}"
            : $"{Host}\\{Instance}";
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = Database,
            IntegratedSecurity = AuthenticationMode == SqlServerAuthenticationMode.IntegratedSecurity,
            Encrypt = Encrypt,
            TrustServerCertificate = TrustServerCertificate,
            Pooling = EnablePooling,
            MinPoolSize = MinPoolSize,
            MaxPoolSize = MaxPoolSize,
            LoadBalanceTimeout = ConnectionLifetime,
            ConnectTimeout = ConnectionTimeout,
            CommandTimeout = CommandTimeout,
            ConnectRetryCount = TransientRetryCount
        };
        if (AuthenticationMode == SqlServerAuthenticationMode.SqlLogin)
        {
            builder.UserID = Username;
            builder.Password = Password;
        }
        return builder.ConnectionString;
    }

    public ConfigValidationResult Validate()
    {
        if (!Enabled || !string.IsNullOrWhiteSpace(ConnectionString)) return ConfigValidationResult.Valid();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Host)) errors.Add("Host is required");
        if (Port is < 1 or > 65535) errors.Add("Port must be between 1 and 65535");
        if (string.IsNullOrWhiteSpace(Database)) errors.Add("Database name is required");
        if (AuthenticationMode == SqlServerAuthenticationMode.SqlLogin && string.IsNullOrWhiteSpace(Username))
            errors.Add("Username is required for SQL login authentication");
        if (MinPoolSize < 0) errors.Add("MinPoolSize cannot be negative");
        if (MaxPoolSize < 1 || MaxPoolSize < MinPoolSize) errors.Add("MaxPoolSize must be positive and at least MinPoolSize");
        if (ConnectionTimeout < 0 || CommandTimeout < 0) errors.Add("Connection and command timeouts cannot be negative");
        if (TransientRetryCount is < 0 or > 255) errors.Add("TransientRetryCount must be between 0 and 255");
        if (TransientRetryBaseDelayMs < 0) errors.Add("TransientRetryBaseDelayMs cannot be negative");
        if (MaxBatchInsertSize < 1) errors.Add("MaxBatchInsertSize must be positive");
        if (MaxInClauseValues < 1) errors.Add("MaxInClauseValues must be positive");
        if (QueryTimeoutMs < 0) errors.Add("QueryTimeoutMs cannot be negative");
        if (N1DetectorThreshold < 0) errors.Add("N1DetectorThreshold cannot be negative");
        if (DefaultStringSize is < 1 or > 4000) errors.Add("DefaultStringSize must be between 1 and 4000");
        if (string.IsNullOrWhiteSpace(DefaultSchema)) errors.Add("DefaultSchema is required");
        return errors.Count == 0 ? ConfigValidationResult.Valid() : ConfigValidationResult.Invalid(errors);
    }
}
