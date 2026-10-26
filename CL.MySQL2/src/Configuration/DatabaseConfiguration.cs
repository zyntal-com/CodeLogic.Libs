using CL.MySQL2.Models;
using CodeLogic.Core.Configuration;
using MySqlConnector;

namespace CL.MySQL2.Configuration;

/// <summary>
/// Root configuration for <c>CL.MySQL2</c>.
/// Serialized to / from <c>config.mysql.json</c> in the library's config directory.
/// </summary>
[ConfigSection("mysql")]
public sealed class DatabaseConfiguration : ConfigModelBase
{
    /// <summary>
    /// Named database configurations keyed by connection ID.
    /// </summary>
    public Dictionary<string, MySqlDatabaseConfig> Databases { get; set; } = new()
    {
        ["Default"] = new MySqlDatabaseConfig()
    };

    /// <summary>
    /// Validates all configured databases.
    /// </summary>
    public override ConfigValidationResult Validate()
    {
        var errors = new List<string>();

        if (Databases.Count == 0)
            errors.Add("At least one database configuration is required");

        foreach (var kvp in Databases)
        {
            var result = kvp.Value.Validate();
            if (!result.IsValid)
                errors.Add($"Database '{kvp.Key}': {string.Join(", ", result.Errors)}");
        }

        return errors.Count > 0
            ? ConfigValidationResult.Invalid(errors)
            : ConfigValidationResult.Valid();
    }
}

/// <summary>
/// Per-database MySQL connection settings.
/// </summary>
public sealed class MySqlDatabaseConfig
{
    /// <summary>Whether this database connection is active.</summary>
    [ConfigField(Label = "Enabled", Description = "Turn this connection on or off without removing it.", Group = "General", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>MySQL server hostname or IP address.</summary>
    [ConfigField(Label = "Host", Description = "MySQL server hostname or IP address.", Required = true, Placeholder = "localhost",
        RequiresRestart = true, Group = "Connection", Order = 10)]
    public string Host { get; set; } = "localhost";

    /// <summary>MySQL server port. Default: 3306.</summary>
    [ConfigField(Label = "Port", Min = 1, Max = 65535, RequiresRestart = true, Group = "Connection", Order = 11)]
    public int Port { get; set; } = 3306;

    /// <summary>Database (schema) name.</summary>
    [ConfigField(Label = "Database", Description = "Name of the MySQL schema to use.", Required = true,
        RequiresRestart = true, Group = "Connection", Order = 12)]
    public string Database { get; set; } = string.Empty;

    /// <summary>MySQL username.</summary>
    [ConfigField(Label = "Username", Required = true, RequiresRestart = true, Group = "Connection", Order = 13)]
    public string Username { get; set; } = string.Empty;

    /// <summary>MySQL password.</summary>
    [ConfigField(Label = "Password", InputType = ConfigInputType.Password, Secret = true,
        RequiresRestart = true, Group = "Connection", Order = 14)]
    public string Password { get; set; } = string.Empty;

    /// <summary>Whether connection pooling is enabled. Default: true.</summary>
    [ConfigField(Label = "Enable Pooling", Description = "Use the MySqlConnector connection pool.",
        RequiresRestart = true, Group = "Pooling", Order = 20, Collapsed = true)]
    public bool EnablePooling { get; set; } = true;

    /// <summary>Minimum number of pooled connections. Default: 1.</summary>
    [ConfigField(Label = "Min Pool Size", Min = 0, RequiresRestart = true, Group = "Pooling", Order = 21, Collapsed = true)]
    public int MinPoolSize { get; set; } = 1;

    /// <summary>Maximum number of pooled connections. Default: 100.</summary>
    [ConfigField(Label = "Max Pool Size", Min = 1, RequiresRestart = true, Group = "Pooling", Order = 22, Collapsed = true)]
    public int MaxPoolSize { get; set; } = 100;

    /// <summary>Maximum connection lifetime in seconds. Default: 300.</summary>
    [ConfigField(Label = "Connection Lifetime (s)", Min = 0, RequiresRestart = true, Group = "Pooling", Order = 23, Collapsed = true)]
    public int ConnectionLifetime { get; set; } = 300;

    /// <summary>Connection timeout in seconds. Default: 30.</summary>
    [ConfigField(Label = "Connect Timeout (s)", Min = 1, Max = 600, Group = "Timeouts", Order = 30, Collapsed = true)]
    public int ConnectionTimeout { get; set; } = 30;

    /// <summary>Command timeout in seconds. Default: 30.</summary>
    [ConfigField(Label = "Command Timeout (s)", Min = 1, Max = 3600, Group = "Timeouts", Order = 31, Collapsed = true)]
    public int CommandTimeout { get; set; } = 30;

    /// <summary>Whether to require SSL/TLS for connections. Default: false.</summary>
    [ConfigField(Label = "Enable SSL/TLS", RequiresRestart = true, Group = "Security", Order = 40)]
    public bool EnableSsl { get; set; } = false;

    /// <summary>
    /// Path to a PEM certificate-authority file used to verify the server's certificate.
    /// Optional; only honoured when <see cref="EnableSsl"/> is true, in which case it is written
    /// to the connection string as MySqlConnector's <c>SslCa</c> and the SSL mode is raised to
    /// <see cref="MySqlSslMode.VerifyCA"/> (a CA is pointless without verification).
    /// Blank leaves the connection at <c>SslMode=Required</c> with no chain validation.
    /// </summary>
    [ConfigField(Label = "SSL CA Certificate Path",
        Description = "Optional PEM CA file used to verify the server certificate. Applied only when SSL is enabled; raises SSL mode to VerifyCA.",
        RequiresRestart = true, Group = "Security", Order = 41)]
    public string? SslCertificatePath { get; set; }

    /// <summary>Whether to allow servers without RSA public key. Default: false.</summary>
    [ConfigField(Label = "Allow Public Key Retrieval", Description = "Needed for some MySQL 8+ configurations.",
        RequiresRestart = true, Group = "Security", Order = 42)]
    public bool AllowPublicKeyRetrieval { get; set; } = false;

    /// <summary>Connection character set. Default: "utf8mb4".</summary>
    [ConfigField(Label = "Character Set", RequiresRestart = true, Group = "Advanced", Order = 50, Collapsed = true)]
    public string CharacterSet { get; set; } = "utf8mb4";

    /// <summary>
    /// Default collation. Default: "utf8mb4_unicode_ci". Not written into the connection string;
    /// table collation comes from <c>[Table(Collation = ...)]</c>. Used, with
    /// <see cref="CharacterSet"/>, as the default when the library creates the database.
    /// </summary>
    [ConfigField(Label = "Collation", RequiresRestart = true, Group = "Advanced", Order = 51, Collapsed = true)]
    public string Collation { get; set; } = "utf8mb4_unicode_ci";

    /// <summary>
    /// When true, the library creates <see cref="Database"/> (with <see cref="CharacterSet"/> and
    /// <see cref="Collation"/>) during initialization if the server does not have it yet. Needs
    /// a user allowed to run CREATE DATABASE. Default: false.
    /// </summary>
    [ConfigField(Label = "Create Database If Missing",
        Description = "Create the database at startup when it does not exist. Requires CREATE privilege.",
        RequiresRestart = true, Group = "Advanced", Order = 52, Collapsed = true)]
    public bool CreateDatabaseIfMissing { get; set; } = false;

    /// <summary>
    /// Primary schema sync knob. See <see cref="Models.SyncMode"/>.
    /// Default: <see cref="Models.SyncMode.Production"/> (safe/additive, never drops).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><b>Developer</b> — aggressive rolling updates; drops removed columns/indexes/FKs without asking (every boot).</item>
    ///   <item><b>Production</b> — default. Additive only; never drops. A change needing a drop is deferred and flagged <c>DriftPending</c>.</item>
    ///   <item><b>Migration</b> — deliberate one-shot destructive reconcile (with backup). Idempotent; warns to switch back to Production once done.</item>
    /// </list>
    /// In all modes, models whose stored CRC in <c>__schema_state</c> matches the current model are skipped entirely.
    /// </remarks>
    [ConfigField(Label = "Sync Mode",
        Description = "How schema sync reconciles the DB with entity models. Production = additive only, never drops (default). Developer = drops removed columns/indexes without asking. Migration = one-shot destructive reconcile, then switch back to Production.",
        Group = "Schema Sync", Order = 59)]
    public SyncMode SyncMode { get; set; } = SyncMode.Production;

    /// <summary>
    /// Legacy. Lower-level sync aggressiveness. Retained for back-compat; <see cref="SyncMode"/>
    /// takes precedence and maps onto this. See <see cref="Models.SchemaSyncLevel"/>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><b>None</b> — no sync at all.</item>
    ///   <item><b>Safe</b> — add missing columns/indexes/FKs + modify existing columns. Never drops.</item>
    ///   <item><b>Additive</b> — Safe + drop indexes and foreign keys no longer in the model.</item>
    ///   <item><b>Full</b> — Additive + drop columns no longer in the model. Development only.</item>
    /// </list>
    /// </remarks>
    [ConfigField(Label = "Schema Sync Level (legacy)",
        Description = "Deprecated — use Sync Mode. Lower-level knob: Safe = additive only. Additive = also drops removed indexes/FKs. Full = also drops removed columns.",
        Group = "Schema Sync", Order = 60, Collapsed = true)]
    public SchemaSyncLevel SchemaSyncLevel { get; set; } = SchemaSyncLevel.Safe;

    /// <summary>
    /// Legacy flag. When true, sync operates at <see cref="Models.SchemaSyncLevel.Full"/>
    /// regardless of <see cref="SchemaSyncLevel"/>. Prefer setting <see cref="SyncMode"/> directly.
    /// </summary>
    [ConfigField(Label = "Allow Destructive Sync (legacy)",
        Description = "Deprecated — use Sync Mode = Developer/Migration instead.",
        Group = "Schema Sync", Order = 61, Collapsed = true)]
    public bool AllowDestructiveSync { get; set; } = false;

    /// <summary>
    /// True when this database is configured for the one-shot <see cref="Models.SyncMode.Migration"/> mode.
    /// </summary>
    public bool IsMigrationMode => SyncMode == SyncMode.Migration;

    /// <summary>
    /// Effective internal sync level. An explicitly chosen <see cref="SyncMode.Developer"/> or
    /// <see cref="SyncMode.Migration"/> always maps to <see cref="SchemaSyncLevel.Full"/>. Otherwise
    /// (<see cref="SyncMode.Production"/>, the default) the legacy <see cref="SchemaSyncLevel"/> /
    /// <see cref="AllowDestructiveSync"/> knobs are honored for back-compat — so an old config with
    /// <c>SchemaSyncLevel = Full</c> still behaves destructively, while a fresh Production config
    /// (default <see cref="SchemaSyncLevel.Safe"/>) never drops.
    /// </summary>
    public SchemaSyncLevel EffectiveSyncLevel =>
        SyncMode switch
        {
            SyncMode.Developer => SchemaSyncLevel.Full,
            SyncMode.Migration => SchemaSyncLevel.Full,
            _ => AllowDestructiveSync ? SchemaSyncLevel.Full : SchemaSyncLevel
        };

    /// <summary>
    /// Directory for schema backup files. Blank (the default) keeps
    /// <see cref="Services.BackupManager"/> writing to <c>DataDirectory/backups</c>; when set,
    /// backups for this connection are written to (and read back from) the given directory.
    /// </summary>
    [ConfigField(Label = "Backup Directory", Description = "Override where schema backups are stored. Blank = default data/backups folder.",
        Group = "Schema Sync", Order = 62, Collapsed = true)]
    public string? BackupDirectory { get; set; }

    /// <summary>Queries exceeding this threshold (ms) are logged as slow queries. Default: 1000.</summary>
    [ConfigField(Label = "Slow Query Threshold (ms)", Min = 0,
        Description = "Queries taking longer than this are logged as slow.",
        Group = "Advanced", Order = 52, Collapsed = true)]
    public int SlowQueryThresholdMs { get; set; } = 1000;

    /// <summary>
    /// Per-database override for the global cache switch. Null (the default) inherits
    /// <see cref="CacheConfiguration.Enabled"/>; a non-null value wins over it for queries
    /// running on this connection.
    /// </summary>
    [ConfigField(Label = "Cache Enabled Override",
        Description = "Override the global cache switch for this database only. Leave empty to inherit.",
        Group = "Cache", Order = 70, Collapsed = true)]
    public bool? CacheEnabledOverride { get; set; } = null;

    /// <summary>
    /// Default per-query timeout in milliseconds, applied as <c>CommandTimeout</c> on the
    /// commands the repository, query builder, projections, joins and the raw-SQL helpers
    /// create (rounded up to whole seconds — MySqlConnector's unit). 0 leaves each command at
    /// the connection string's <see cref="CommandTimeout"/>. Default 30000 matches that 30s
    /// default, so the effective timeout is unchanged unless you change this.
    /// </summary>
    [ConfigField(Label = "Query Timeout (ms)", Min = 0,
        Description = "Per-query command timeout in ms (rounded up to seconds). 0 = use the connection's Command Timeout.",
        Group = "Timeouts", Order = 32, Collapsed = true)]
    public int QueryTimeoutMs { get; set; } = 30_000;

    /// <summary>
    /// Chunk size for <c>InsertManyAsync</c> / <c>UpsertManyAsync</c>: rows per batched
    /// statement. Passed to every <c>Repository&lt;T&gt;</c> created by
    /// <c>MySQL2Library.GetRepository&lt;T&gt;</c>. Default: 500.
    /// </summary>
    [ConfigField(Label = "Max Batch Insert Size", Min = 1, Max = 10_000,
        Description = "Number of rows per batched INSERT statement.",
        Group = "Performance", Order = 80, Collapsed = true)]
    public int MaxBatchInsertSize { get; set; } = 500;

    /// <summary>
    /// When true, MySQL error 1062 (ER_DUP_ENTRY) in <c>InsertAsync</c> and <c>InsertManyAsync</c>
    /// returns the distinct error code <c>mysql.duplicate_key</c> and logs at Warning instead of Error.
    /// When false (the default), error code is <c>mysql.insert_failed</c> / <c>mysql.bulk_insert_failed</c>
    /// and logs at Error.
    /// </summary>
    [ConfigField(Label = "Distinguish Duplicate Key Errors",
        Description = "When true, MySQL duplicate key errors (1062) return error code mysql.duplicate_key and log at Warning. Default: false.",
        Group = "Advanced", Order = 53, Collapsed = true)]
    public bool DistinguishDuplicateKeyError { get; set; } = false;

    /// <summary>
    /// Advisory ceiling on the number of values in a generated <c>IN (...)</c> clause. Nothing is
    /// chunked or rejected — a collection <c>Contains</c> still emits one parameter per value —
    /// but a translation that exceeds this logs one warning naming the entity and the count.
    /// Default: 1000.
    /// </summary>
    [ConfigField(Label = "Max IN-Clause Values", Min = 1, Max = 65_000,
        Description = "Warn (once per query build) when a generated IN (...) list exceeds this many values. Nothing is chunked or rejected.",
        Group = "Performance", Order = 81, Collapsed = true)]
    public int MaxInClauseValues { get; set; } = 1_000;

    /// <summary>
    /// Obsolete and ignored. Statement caching is the provider's concern: set MySqlConnector's
    /// <c>IgnorePrepare=false</c> on the connection string instead. Retained so existing config
    /// files keep deserializing.
    /// </summary>
    [Obsolete("Statement caching is configured on the MySqlConnector connection string (IgnorePrepare=false); this value is ignored.")]
    [ConfigField(Label = "Prepared Statement Cache Size", Min = 0,
        Description = "Obsolete and ignored — configure statement caching on the MySqlConnector connection string (IgnorePrepare=false).",
        Group = "Performance", Order = 82, Collapsed = true)]
    public int PreparedStatementCacheSize { get; set; } = 256;

    /// <summary>
    /// How many times to automatically retry a single non-transactional statement that
    /// fails with a transient error (deadlock 1213, lock-wait timeout 1205). 0 disables.
    /// Default: 3. Statements inside an explicit transaction scope are never auto-retried —
    /// the whole transaction must be retried by the caller.
    /// </summary>
    [ConfigField(Label = "Transient Retry Count", Min = 0, Max = 10,
        Description = "Auto-retry deadlock / lock-wait-timeout on single statements. 0 disables.",
        Group = "Performance", Order = 83, Collapsed = true)]
    public int TransientRetryCount { get; set; } = 3;

    /// <summary>
    /// Base backoff in milliseconds for transient retries; the delay grows exponentially
    /// (base × 2^attempt) with a little random jitter. Default: 50.
    /// </summary>
    [ConfigField(Label = "Transient Retry Base Delay (ms)", Min = 0, Max = 10_000,
        Description = "Base backoff for transient retries; grows exponentially with jitter.",
        Group = "Performance", Order = 84, Collapsed = true)]
    public int TransientRetryBaseDelayMs { get; set; } = 50;

    /// <summary>
    /// Publish <c>N1QueryDetectedEvent</c> when the same normalized query template executes this
    /// many times on this connection within a one-second rolling window. The event fires once per
    /// window, not on every further execution. 0 (the default) disables detection entirely, and
    /// costs nothing on the query path.
    /// </summary>
    [ConfigField(Label = "N+1 Detector Threshold", Min = 0,
        Description = "Publish N1QueryDetectedEvent when one query template repeats this many times within a second. 0 disables.",
        Group = "Observability", Order = 90, Collapsed = true)]
    public int N1DetectorThreshold { get; set; } = 0;

    /// <summary>
    /// When true, a slow query additionally runs <c>EXPLAIN FORMAT=JSON</c> on a separate pooled
    /// connection (never the caller's transaction) and attaches the plan to
    /// <c>SlowQueryEvent.ExplainJson</c>. Strictly best-effort: statements that cannot be
    /// explained (DDL, multi-statement batches) are skipped and any failure is swallowed — the
    /// event still publishes, with a null payload. Default: false.
    /// </summary>
    [ConfigField(Label = "Capture EXPLAIN On Slow",
        Description = "On a slow query, run EXPLAIN FORMAT=JSON on a separate connection and attach the plan to SlowQueryEvent.",
        Group = "Observability", Order = 91, Collapsed = true)]
    public bool CaptureExplainOnSlowQuery { get; set; } = false;

    /// <summary>
    /// Default VARCHAR length used by type inference when a string property has no explicit
    /// <c>[Column(Size = …)]</c>. Applied process-wide at library initialization from the
    /// <c>Default</c> database (or the first enabled one) — DDL generation is not
    /// connection-scoped. Default: 255.
    /// </summary>
    [ConfigField(Label = "Default String Size", Min = 1, Max = 65_535,
        Description = "Default VARCHAR length for string columns without an explicit Size.",
        Group = "Schema Sync", Order = 63, Collapsed = true)]
    public int DefaultStringSize { get; set; } = 255;

    /// <summary>
    /// Builds and returns the MySqlConnector connection string from the current configuration.
    /// </summary>
    public string BuildConnectionString()
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = Host,
            Port = (uint)Port,
            Database = Database,
            UserID = Username,
            Password = Password,
            Pooling = EnablePooling,
            MinimumPoolSize = (uint)MinPoolSize,
            MaximumPoolSize = (uint)MaxPoolSize,
            ConnectionLifeTime = (uint)ConnectionLifetime,
            ConnectionTimeout = (uint)ConnectionTimeout,
            DefaultCommandTimeout = (uint)CommandTimeout,
            AllowPublicKeyRetrieval = AllowPublicKeyRetrieval,
            CharacterSet = CharacterSet,
            SslMode = EnableSsl ? MySqlSslMode.Required : MySqlSslMode.None
        };

        // A CA path is only meaningful when TLS is on, and only if the chain is actually
        // verified — so providing one raises Required to VerifyCA. With SSL off the path is
        // ignored rather than handed to the driver as a contradictory option.
        if (EnableSsl && !string.IsNullOrWhiteSpace(SslCertificatePath))
        {
            builder.SslCa = SslCertificatePath;
            builder.SslMode = MySqlSslMode.VerifyCA;
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// Validates the per-database configuration.
    /// </summary>
    public ConfigValidationResult Validate()
    {
        if (!Enabled)
            return ConfigValidationResult.Valid();

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Host))
            errors.Add("Host is required");

        if (Port is < 1 or > 65535)
            errors.Add("Port must be between 1 and 65535");

        if (string.IsNullOrWhiteSpace(Database))
            errors.Add("Database name is required");

        if (string.IsNullOrWhiteSpace(Username))
            errors.Add("Username is required");

        // Bounds the other two libraries already check. Without them an inverted pool range
        // or a negative timeout is accepted here and only fails later, as a driver error at
        // connection time rather than a configuration error at startup.
        if (MinPoolSize < 0)
            errors.Add("MinPoolSize cannot be negative");

        if (MaxPoolSize < 1 || MaxPoolSize < MinPoolSize)
            errors.Add("MaxPoolSize must be positive and at least MinPoolSize");

        if (ConnectionTimeout < 0 || CommandTimeout < 0)
            errors.Add("Connection and command timeouts cannot be negative");

        if (MaxBatchInsertSize < 1)
            errors.Add("MaxBatchInsertSize must be positive");

        return errors.Count > 0
            ? ConfigValidationResult.Invalid(errors)
            : ConfigValidationResult.Valid();
    }
}
