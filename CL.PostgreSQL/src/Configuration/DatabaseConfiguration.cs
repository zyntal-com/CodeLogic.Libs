using CL.PostgreSQL.Models;
using CodeLogic.Core.Configuration;
using Npgsql;

namespace CL.PostgreSQL.Configuration;

/// <summary>
/// Root configuration for <c>CL.PostgreSQL</c>.
/// Serialized to / from <c>config.postgresql.json</c> in the library's config directory.
/// </summary>
[ConfigSection("postgresql")]
public sealed class DatabaseConfiguration : ConfigModelBase
{
    /// <summary>
    /// Named database configurations keyed by connection ID.
    /// </summary>
    public Dictionary<string, PostgreSqlDatabaseConfig> Databases { get; set; } = new()
    {
        ["Default"] = new PostgreSqlDatabaseConfig()
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
/// Per-database PostgreSQL connection settings.
/// </summary>
public sealed class PostgreSqlDatabaseConfig
{
    /// <summary>Whether this database connection is active.</summary>
    [ConfigField(Label = "Enabled", Description = "Turn this connection on or off without removing it.", Group = "General", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>PostgreSQL server hostname or IP address.</summary>
    [ConfigField(Label = "Host", Description = "PostgreSQL server hostname or IP address.", Required = true, Placeholder = "localhost",
        RequiresRestart = true, Group = "Connection", Order = 10)]
    public string Host { get; set; } = "localhost";

    /// <summary>PostgreSQL server port. Default: 5432.</summary>
    [ConfigField(Label = "Port", Min = 1, Max = 65535, RequiresRestart = true, Group = "Connection", Order = 11)]
    public int Port { get; set; } = 5432;

    /// <summary>
    /// Database name. Distinct from a PostgreSQL schema: a connection targets one
    /// database, and schemas are namespaces within it (see DefaultSchema).
    /// </summary>
    [ConfigField(Label = "Database", Description = "Name of the PostgreSQL database to connect to.", Required = true,
        RequiresRestart = true, Group = "Connection", Order = 12)]
    public string Database { get; set; } = string.Empty;

    /// <summary>PostgreSQL username.</summary>
    [ConfigField(Label = "Username", Required = true, RequiresRestart = true, Group = "Connection", Order = 13)]
    public string Username { get; set; } = string.Empty;

    /// <summary>PostgreSQL password.</summary>
    [ConfigField(Label = "Password", InputType = ConfigInputType.Password, Secret = true,
        RequiresRestart = true, Group = "Connection", Order = 14)]
    public string Password { get; set; } = string.Empty;

    /// <summary>Whether connection pooling is enabled. Default: true.</summary>
    [ConfigField(Label = "Enable Pooling", Description = "Use the Npgsql connection pool.",
        RequiresRestart = true, Group = "Pooling", Order = 20, Collapsed = true)]
    public bool EnablePooling { get; set; } = true;

    /// <summary>Minimum number of pooled connections. Default: 1.</summary>
    [ConfigField(Label = "Min Pool Size", Min = 0, RequiresRestart = true, Group = "Pooling", Order = 21, Collapsed = true)]
    public int MinPoolSize { get; set; } = 1;

    /// <summary>Maximum number of pooled connections. Default: 100.</summary>
    [ConfigField(Label = "Max Pool Size", Min = 1, RequiresRestart = true, Group = "Pooling", Order = 22, Collapsed = true)]
    public int MaxPoolSize { get; set; } = 100;

    /// <summary>
    /// How long a pooled connection may sit idle before it is closed, in seconds.
    /// Maps to Npgsql's <c>Connection Idle Lifetime</c>. Default: 300.
    /// </summary>
    [ConfigField(Label = "Connection Lifetime (s)", Min = 0, RequiresRestart = true, Group = "Pooling", Order = 23, Collapsed = true)]
    public int ConnectionLifetime { get; set; } = 300;

    /// <summary>Connection timeout in seconds. Default: 30.</summary>
    [ConfigField(Label = "Connect Timeout (s)", Min = 1, Max = 600, Group = "Timeouts", Order = 30, Collapsed = true)]
    public int ConnectionTimeout { get; set; } = 30;

    /// <summary>Command timeout in seconds. Default: 30.</summary>
    [ConfigField(Label = "Command Timeout (s)", Min = 1, Max = 3600, Group = "Timeouts", Order = 31, Collapsed = true)]
    public int CommandTimeout { get; set; } = 30;

    /// <summary>
    /// TLS negotiation mode. Defaults to <see cref="PostgreSqlSslMode.Prefer"/>, which
    /// encrypts when the server offers it but does not verify the certificate. Use
    /// <see cref="PostgreSqlSslMode.VerifyFull"/> in production: it is the only mode that
    /// authenticates the server and so the only one that resists an active attacker.
    /// </summary>
    [ConfigField(Label = "SSL Mode",
        Description = "Disable / Allow / Prefer / Require / VerifyCA / VerifyFull. Prefer encrypts but does not verify; use VerifyFull in production.",
        RequiresRestart = true, Group = "Security", Order = 40)]
    public PostgreSqlSslMode SslMode { get; set; } = PostgreSqlSslMode.Prefer;

    /// <summary>
    /// Path to a client certificate (PEM) for certificate authentication. Optional.
    /// Maps to Npgsql's <c>SslCertificate</c> connection-string option; pair it with
    /// <see cref="SslKeyPath"/> (Npgsql's <c>SslKey</c>). The CA bundle used to verify the
    /// <i>server</i> is <see cref="SslRootCertificatePath"/> instead.
    /// </summary>
    [ConfigField(Label = "Client Certificate Path",
        Description = "Optional path to a client certificate (PEM). Maps to Npgsql's SslCertificate.",
        RequiresRestart = true, Group = "Security", Order = 41)]
    public string? SslCertificatePath { get; set; }

    /// <summary>Path to the client certificate's private key. Optional.</summary>
    [ConfigField(Label = "Client Key Path", Description = "Optional path to the client certificate private key.",
        RequiresRestart = true, Group = "Security", Order = 42)]
    public string? SslKeyPath { get; set; }

    /// <summary>
    /// Path to a CA root certificate used to verify the server under the VerifyCA and
    /// VerifyFull modes. Falls back to the system trust store when null.
    /// </summary>
    [ConfigField(Label = "Root Certificate Path", Description = "CA bundle used by VerifyCA / VerifyFull.",
        RequiresRestart = true, Group = "Security", Order = 43)]
    public string? SslRootCertificatePath { get; set; }

    /// <summary>
    /// The schema entities map to on this connection, and the <c>search_path</c> applied to
    /// every connection. Default: "public".
    /// <para>
    /// An entity <b>without</b> <c>[Table(Schema = …)]</c> is created in, and every generated
    /// statement qualified with, this schema; an entity that declares one keeps it. The
    /// schema is created (<c>CREATE SCHEMA IF NOT EXISTS</c>) on first sync if missing.
    /// </para>
    /// <para>
    /// Two named connections may configure different schemas — resolution is per connection.
    /// </para>
    /// </summary>
    [ConfigField(Label = "Default Schema",
        Description = "Schema unqualified entities are created in and queried through, and the connection search_path. Created if missing.",
        RequiresRestart = true, Group = "Advanced", Order = 50, Collapsed = true)]
    public string DefaultSchema { get; set; } = "public";

    /// <summary>
    /// Application name reported to the server, visible in <c>pg_stat_activity</c>.
    /// Makes it possible to attribute load to this library. Optional.
    /// </summary>
    [ConfigField(Label = "Application Name", Group = "Advanced", Order = 51, Collapsed = true)]
    public string? ApplicationName { get; set; }

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
    /// Override for the schema-backup directory used by <see cref="Services.BackupManager"/>.
    /// Blank / null keeps the default <c>DataDirectory/backups</c>.
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
    /// on this connection.
    /// </summary>
    [ConfigField(Label = "Cache Enabled Override",
        Description = "Override the global cache switch for this database only. Leave empty to inherit.",
        Group = "Cache", Order = 70, Collapsed = true)]
    public bool? CacheEnabledOverride { get; set; } = null;

    /// <summary>
    /// Per-command timeout in milliseconds, applied to every command the library creates
    /// (rounded up to whole seconds, the unit ADO.NET exposes). 0 means no timeout.
    /// Default: 30000, which matches both <see cref="CommandTimeout"/> and Npgsql's own
    /// 30-second default, so the effective timeout is unchanged unless you change this.
    /// </summary>
    [ConfigField(Label = "Query Timeout (ms)", Min = 0,
        Description = "Per-command timeout applied to commands this library creates. 0 = no timeout.",
        Group = "Timeouts", Order = 32, Collapsed = true)]
    public int QueryTimeoutMs { get; set; } = 30_000;

    /// <summary>
    /// Chunk size for the batched <c>InsertManyAsync</c> / <c>UpsertManyAsync</c> statements,
    /// passed to <see cref="Services.Repository{T}"/> by <c>GetRepository&lt;T&gt;()</c>.
    /// Default: 500 rows, further capped by PostgreSQL's 65535-parameter ceiling.
    /// </summary>
    [ConfigField(Label = "Max Batch Insert Size", Min = 1, Max = 10_000,
        Description = "Rows per batched INSERT / UPSERT, capped by PostgreSQL's 65535-parameter limit.",
        Group = "Performance", Order = 80, Collapsed = true)]
    public int MaxBatchInsertSize { get; set; } = 500;

    /// <summary>
    /// When true, a PostgreSQL unique violation (SQLSTATE 23505) in <c>InsertAsync</c> and
    /// <c>InsertManyAsync</c> returns the distinct error code <c>postgresql.duplicate_key</c> and
    /// logs at Warning instead of Error. When false (the default), the error code is
    /// <c>postgresql.insert_failed</c> / <c>postgresql.bulk_insert_failed</c> and logs at Error.
    /// </summary>
    [ConfigField(Label = "Distinguish Duplicate Key Errors",
        Description = "When true, PostgreSQL unique violations (23505) return error code postgresql.duplicate_key and log at Warning. Default: false.",
        Group = "Advanced", Collapsed = true)]
    public bool DistinguishDuplicateKeyError { get; set; } = false;

    /// <summary>
    /// Advisory ceiling on the number of values in a generated <c>IN (...)</c> list. A wider
    /// list is still sent whole — nothing is chunked and nothing throws — but a warning is
    /// logged once per query build naming the entity and the value count, so an accidental
    /// 50 000-element <c>Contains</c> is visible instead of silent.
    /// </summary>
    [ConfigField(Label = "Max IN-Clause Values", Min = 1, Max = 65_000,
        Description = "Warn (once per query) when a generated IN list is wider than this. The list is still sent whole.",
        Group = "Performance", Order = 81, Collapsed = true)]
    public int MaxInClauseValues { get; set; } = 1_000;

    /// <summary>
    /// <b>Obsolete.</b> Statement caching is Npgsql's concern and is configured on the
    /// connection string via <c>Max Auto Prepare</c> and <c>Auto Prepare Min Usages</c>.
    /// Nothing reads this value.
    /// </summary>
    [Obsolete("Statement caching is configured on the Npgsql connection string " +
              "(Max Auto Prepare / Auto Prepare Min Usages), not here. This value is not read.")]
    [ConfigField(Label = "Prepared Statement Cache Size", Min = 0,
        Description = "Obsolete — set Npgsql's 'Max Auto Prepare' / 'Auto Prepare Min Usages' on the connection string instead.",
        Group = "Performance", Order = 82, Collapsed = true)]
    public int PreparedStatementCacheSize { get; set; } = 256;

    /// <summary>
    /// How many times to automatically retry a single non-transactional statement that
    /// fails with a transient error — SQLSTATE <c>40001</c> (serialization failure),
    /// <c>40P01</c> (deadlock detected) or <c>55P03</c> (lock not available). 0 disables.
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
    /// Threshold for the N+1 detector: publish <c>N1QueryDetectedEvent</c> when the same
    /// normalized query template executes this many times on this connection inside a
    /// one-second rolling window. Fires once per window per template.
    /// <b>0 (the default) disables the detector entirely</b>, at which point it costs a
    /// single bool read per query.
    /// </summary>
    [ConfigField(Label = "N+1 Detector Threshold", Min = 0,
        Description = "Publish N1QueryDetectedEvent when one query template repeats this often within a second. 0 disables.",
        Group = "Observability", Order = 90, Collapsed = true)]
    public int N1DetectorThreshold { get; set; } = 0;

    /// <summary>
    /// When true, a query that crosses <see cref="SlowQueryThresholdMs"/> has
    /// <c>EXPLAIN (FORMAT JSON)</c> run against it on a separate connection and the plan
    /// attached to <c>SlowQueryEvent.ExplainJson</c>.
    /// <para>
    /// Default: <b>false</b>. Strictly best-effort — the plan is fetched off the query path,
    /// never inside the caller's transaction scope, and any failure leaves the event's
    /// payload null rather than surfacing. Statements EXPLAIN cannot accept (DDL, batches)
    /// are skipped.
    /// </para>
    /// </summary>
    [ConfigField(Label = "Capture EXPLAIN On Slow",
        Description = "Run EXPLAIN (FORMAT JSON) for slow queries and attach the plan to SlowQueryEvent. Best-effort; off by default.",
        Group = "Observability", Order = 91, Collapsed = true)]
    public bool CaptureExplainOnSlowQuery { get; set; } = false;

    /// <summary>
    /// Default <c>varchar</c> length for a string property with no explicit
    /// <c>[Column(Size = …)]</c>, threaded into type inference by schema sync.
    /// Default: 255 — the same length inference used before, so nothing changes unless
    /// you change this.
    /// </summary>
    [ConfigField(Label = "Default String Size", Min = 1, Max = 65_535,
        Description = "Default VARCHAR length for string columns without an explicit Size.",
        Group = "Schema Sync", Order = 63, Collapsed = true)]
    public int DefaultStringSize { get; set; } = 255;

    /// <summary>
    /// Builds and returns the Npgsql connection string from the current configuration.
    /// </summary>
    public string BuildConnectionString()
    {
        // Built through NpgsqlConnectionStringBuilder rather than by concatenation, so a
        // value containing ';' or '=' is quoted instead of injecting connection options.
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = Host,
            Port = Port,
            Database = Database,
            Username = Username,
            Password = Password,
            Pooling = EnablePooling,
            MinPoolSize = MinPoolSize,
            MaxPoolSize = MaxPoolSize,
            ConnectionIdleLifetime = ConnectionLifetime,
            Timeout = ConnectionTimeout,
            CommandTimeout = CommandTimeout,
            SslMode = SslMode switch
            {
                PostgreSqlSslMode.Disable    => Npgsql.SslMode.Disable,
                PostgreSqlSslMode.Allow      => Npgsql.SslMode.Allow,
                PostgreSqlSslMode.Require    => Npgsql.SslMode.Require,
                PostgreSqlSslMode.VerifyCA   => Npgsql.SslMode.VerifyCA,
                PostgreSqlSslMode.VerifyFull => Npgsql.SslMode.VerifyFull,
                _                            => Npgsql.SslMode.Prefer
            }
        };

        if (!string.IsNullOrWhiteSpace(DefaultSchema)) builder.SearchPath = DefaultSchema;
        if (!string.IsNullOrWhiteSpace(ApplicationName)) builder.ApplicationName = ApplicationName;
        if (!string.IsNullOrWhiteSpace(SslCertificatePath)) builder.SslCertificate = SslCertificatePath;
        if (!string.IsNullOrWhiteSpace(SslKeyPath)) builder.SslKey = SslKeyPath;
        if (!string.IsNullOrWhiteSpace(SslRootCertificatePath)) builder.RootCertificate = SslRootCertificatePath;

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

        if (MinPoolSize < 0)
            errors.Add("MinPoolSize cannot be negative");

        if (MaxPoolSize < 1 || MaxPoolSize < MinPoolSize)
            errors.Add("MaxPoolSize must be positive and at least MinPoolSize");

        if (ConnectionTimeout < 0 || CommandTimeout < 0)
            errors.Add("Connection and command timeouts cannot be negative");

        if (MaxBatchInsertSize < 1)
            errors.Add("MaxBatchInsertSize must be positive");

        if (string.IsNullOrWhiteSpace(DefaultSchema))
            errors.Add("DefaultSchema is required");
        else if (DefaultSchema.AsSpan().IndexOfAny('"', '\0', '\n') >= 0 || DefaultSchema.Contains('\r'))
            errors.Add("DefaultSchema contains a character that is not permitted in a PostgreSQL identifier");

        if (SslMode is PostgreSqlSslMode.VerifyCA or PostgreSqlSslMode.VerifyFull
            && !string.IsNullOrWhiteSpace(SslRootCertificatePath)
            && !File.Exists(SslRootCertificatePath))
            errors.Add($"Root certificate not found at '{SslRootCertificatePath}'");

        return errors.Count > 0
            ? ConfigValidationResult.Invalid(errors)
            : ConfigValidationResult.Valid();
    }
}

/// <summary>
/// TLS negotiation mode, mirroring libpq's <c>sslmode</c>. Declared here rather than
/// reusing Npgsql's enum so the configuration surface does not leak a provider type.
/// </summary>
public enum PostgreSqlSslMode
{
    /// <summary>Never use TLS.</summary>
    Disable,

    /// <summary>Use TLS only if the server insists.</summary>
    Allow,

    /// <summary>Use TLS when available, without verifying the certificate. The default.</summary>
    Prefer,

    /// <summary>Require TLS, but do not verify the certificate. Encrypts without authenticating.</summary>
    Require,

    /// <summary>Require TLS and verify the certificate chain against a trusted CA.</summary>
    VerifyCA,

    /// <summary>Require TLS, verify the chain, and check the host name matches. Use this in production.</summary>
    VerifyFull
}
