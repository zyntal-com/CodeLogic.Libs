using CL.Data.Mapping;
using CL.Data.Routing;
using CL.Data.Schema;
using CodeLogic.Core.Configuration;

namespace CL.Data.Configuration;

/// <summary>TLS requirement for a connection. Providers map it to their driver's setting.</summary>
public enum DataSslMode
{
    /// <summary>Let the driver decide (its default).</summary>
    Default,
    /// <summary>No TLS.</summary>
    Disable,
    /// <summary>Use TLS when the server offers it.</summary>
    Prefer,
    /// <summary>Require TLS, without validating the server certificate.</summary>
    Require,
    /// <summary>Require TLS and validate the certificate chain against the CA.</summary>
    VerifyCA,
    /// <summary>Require TLS, validate the chain and the host name.</summary>
    VerifyFull,
}

/// <summary>
/// One named database connection: provider, server, pooling, schema policy, performance,
/// resilience and replica settings. Provider-specific options live in <see cref="MySql"/>,
/// <see cref="PostgreSql"/>, <see cref="SqlServer"/> and <see cref="Sqlite"/>.
/// </summary>
public sealed class DataConnectionConfig
{
    // ── General ─────────────────────────────────────────────────────────────

    /// <summary>Turn this connection on or off without removing it.</summary>
    [ConfigField(Label = "Enabled", Group = "General", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>Provider name: <c>mysql</c>, <c>postgresql</c>, <c>sqlserver</c>, <c>sqlite</c>, or a third-party provider.</summary>
    [ConfigField(Label = "Provider", Required = true, InputType = ConfigInputType.Select,
        AllowedValues = "mysql,postgresql,sqlserver,sqlite", RequiresRestart = true, Group = "General", Order = 1)]
    public string Provider { get; set; } = "";

    // ── Connection ──────────────────────────────────────────────────────────

    /// <summary>
    /// Full driver connection string. When set, it is the base, and the discrete fields below only
    /// override what they set explicitly.
    /// </summary>
    [ConfigField(Label = "Connection String", InputType = ConfigInputType.Password, Secret = true,
        RequiresRestart = true, Group = "Connection", Order = 10)]
    public string? ConnectionString { get; set; }

    /// <summary>Server host name or IP address (not used by SQLite).</summary>
    [ConfigField(Label = "Host", Placeholder = "localhost", RequiresRestart = true, Group = "Connection", Order = 11)]
    public string? Host { get; set; }

    /// <summary>Server port; null uses the provider's default port.</summary>
    [ConfigField(Label = "Port", Min = 1, Max = 65535, RequiresRestart = true, Group = "Connection", Order = 12)]
    public int? Port { get; set; }

    /// <summary>
    /// Additional hosts (<c>host</c> or <c>host:port</c>) for driver-level failover of the primary, where the
    /// driver supports it (MySqlConnector, Npgsql; SQL Server uses <c>SqlServer.FailoverPartner</c>).
    /// </summary>
    [ConfigField(Label = "Failover Hosts", RequiresRestart = true, Group = "Connection", Order = 13, Collapsed = true)]
    public List<string> FailoverHosts { get; set; } = [];

    /// <summary>Database (schema) name; for SQLite the database file path, or <c>:memory:</c>.</summary>
    [ConfigField(Label = "Database", RequiresRestart = true, Group = "Connection", Order = 14)]
    public string? Database { get; set; }

    /// <summary>User name.</summary>
    [ConfigField(Label = "Username", RequiresRestart = true, Group = "Connection", Order = 15)]
    public string? Username { get; set; }

    /// <summary>Password.</summary>
    [ConfigField(Label = "Password", InputType = ConfigInputType.Password, Secret = true,
        RequiresRestart = true, Group = "Connection", Order = 16)]
    public string? Password { get; set; }

    /// <summary>TLS requirement.</summary>
    [ConfigField(Label = "SSL Mode", RequiresRestart = true, Group = "Security", Order = 20)]
    public DataSslMode SslMode { get; set; } = DataSslMode.Default;

    /// <summary>CA certificate file used with <see cref="DataSslMode.VerifyCA"/> and <see cref="DataSslMode.VerifyFull"/>.</summary>
    [ConfigField(Label = "SSL CA Certificate Path", RequiresRestart = true, Group = "Security", Order = 21)]
    public string? SslCaCertificatePath { get; set; }

    // ── Pooling and timeouts ────────────────────────────────────────────────

    /// <summary>Use the driver's connection pool.</summary>
    [ConfigField(Label = "Pooling", RequiresRestart = true, Group = "Pooling", Order = 30, Collapsed = true)]
    public bool Pooling { get; set; } = true;

    /// <summary>Connections kept open per node; also opened at start (warm pool).</summary>
    [ConfigField(Label = "Min Pool Size", Min = 0, RequiresRestart = true, Group = "Pooling", Order = 31, Collapsed = true)]
    public int MinPoolSize { get; set; } = 1;

    /// <summary>Maximum open connections per node.</summary>
    [ConfigField(Label = "Max Pool Size", Min = 1, RequiresRestart = true, Group = "Pooling", Order = 32, Collapsed = true)]
    public int MaxPoolSize { get; set; } = 100;

    /// <summary>Recycle pooled connections older than this many seconds (0 = never).</summary>
    [ConfigField(Label = "Connection Lifetime (s)", Min = 0, RequiresRestart = true, Group = "Pooling", Order = 33, Collapsed = true)]
    public int ConnectionLifetimeSeconds { get; set; } = 300;

    /// <summary>Seconds to wait when opening a connection.</summary>
    [ConfigField(Label = "Connect Timeout (s)", Min = 1, Max = 600, Group = "Timeouts", Order = 40, Collapsed = true)]
    public int ConnectTimeoutSeconds { get; set; } = 15;

    /// <summary>Default seconds a command may run.</summary>
    [ConfigField(Label = "Command Timeout (s)", Min = 1, Max = 3600, Group = "Timeouts", Order = 41, Collapsed = true)]
    public int CommandTimeoutSeconds { get; set; } = 30;

    // ── Mapping and schema ──────────────────────────────────────────────────

    /// <summary>How names are derived when attributes give none.</summary>
    [ConfigField(Label = "Naming Policy", RequiresRestart = true, Group = "Schema", Order = 50)]
    public NamingPolicy NamingPolicy { get; set; } = NamingPolicy.AsIs;

    /// <summary>How differences between entities and tables are handled.</summary>
    [ConfigField(Label = "Sync Mode", Group = "Schema", Order = 51)]
    public SyncMode SyncMode { get; set; } = SyncMode.Production;

    /// <summary>Overrides the sync level implied by <see cref="SyncMode"/>.</summary>
    [ConfigField(Label = "Schema Sync Level Override", Group = "Schema", Order = 52, Collapsed = true)]
    public SchemaSyncLevel? SchemaSyncLevel { get; set; }

    /// <summary>The sync level in effect.</summary>
    public SchemaSyncLevel EffectiveSyncLevel => SchemaSyncLevel ?? SyncMode.DefaultLevel();

    /// <summary>Create the database at startup when it does not exist.</summary>
    [ConfigField(Label = "Create Database If Missing", RequiresRestart = true, Group = "Schema", Order = 53)]
    public bool CreateDatabaseIfMissing { get; set; }

    /// <summary>Where schema backups go before a table is altered; null uses the library's data directory.</summary>
    [ConfigField(Label = "Backup Directory", Group = "Schema", Order = 54, Collapsed = true)]
    public string? BackupDirectory { get; set; }

    /// <summary>Length of string columns without an explicit size.</summary>
    [ConfigField(Label = "Default String Size", Min = 1, Max = 65535, Group = "Schema", Order = 55, Collapsed = true)]
    public int DefaultStringSize { get; set; } = 255;

    // ── Performance ─────────────────────────────────────────────────────────

    /// <summary>Overrides the cache master switch for this connection (null = follow <c>data.cache</c>).</summary>
    [ConfigField(Label = "Cache Enabled Override", Group = "Performance", Order = 60, Collapsed = true)]
    public bool? CacheEnabled { get; set; }

    /// <summary>Rows per multi-row INSERT; the provider's parameter limit may lower it further.</summary>
    [ConfigField(Label = "Max Batch Size", Min = 1, Max = 10000, Group = "Performance", Order = 61, Collapsed = true)]
    public int MaxBatchSize { get; set; } = 1000;

    /// <summary><c>InsertMany</c> switches to the native bulk path at or above this many rows (0 = never).</summary>
    [ConfigField(Label = "Bulk Threshold", Min = 0, Group = "Performance", Order = 62, Collapsed = true)]
    public int BulkThreshold { get; set; } = 5000;

    /// <summary>Prepare frequently repeated statements on the server.</summary>
    [ConfigField(Label = "Prepare Statements", Group = "Performance", Order = 64, Collapsed = true)]
    public bool PrepareStatements { get; set; } = true;

    // ── Diagnostics ─────────────────────────────────────────────────────────

    /// <summary>Commands slower than this many milliseconds are logged and published (0 = off).</summary>
    [ConfigField(Label = "Slow Query Threshold (ms)", Min = 0, Group = "Diagnostics", Order = 70)]
    public int SlowQueryThresholdMs { get; set; } = 1000;

    /// <summary>Capture the execution plan of slow queries.</summary>
    [ConfigField(Label = "Capture EXPLAIN On Slow Query", Group = "Diagnostics", Order = 71)]
    public bool CaptureExplainOnSlowQuery { get; set; }

    /// <summary>Report a query shape that repeats this many times within a second (0 = off).</summary>
    [ConfigField(Label = "N+1 Threshold", Min = 0, Group = "Diagnostics", Order = 72)]
    public int NPlusOneThreshold { get; set; }

    /// <summary>Publish a <c>QueryExecutedEvent</c> for every command (frequent; off by default).</summary>
    [ConfigField(Label = "Publish Query Events", Group = "Diagnostics", Order = 73, Collapsed = true)]
    public bool PublishQueryEvents { get; set; }

    // ── Resilience ──────────────────────────────────────────────────────────

    /// <summary>Retries for transient failures outside transactions (0 = no retry).</summary>
    [ConfigField(Label = "Retry Count", Min = 0, Max = 10, Group = "Resilience", Order = 80, Collapsed = true)]
    public int RetryCount { get; set; } = 3;

    /// <summary>Base delay before the first retry; later retries back off exponentially with jitter.</summary>
    [ConfigField(Label = "Retry Base Delay (ms)", Min = 0, Max = 10000, Group = "Resilience", Order = 81, Collapsed = true)]
    public int RetryBaseDelayMs { get; set; } = 50;

    /// <summary>Consecutive failures that open a node's circuit breaker (0 = no breaker).</summary>
    [ConfigField(Label = "Circuit Breaker Threshold", Min = 0, Group = "Resilience", Order = 82, Collapsed = true)]
    public int CircuitBreakerThreshold { get; set; } = 5;

    /// <summary>Seconds a node's circuit stays open before a trial call.</summary>
    [ConfigField(Label = "Circuit Break Duration (s)", Min = 1, Group = "Resilience", Order = 83, Collapsed = true)]
    public int CircuitBreakSeconds { get; set; } = 30;

    // ── Replicas ────────────────────────────────────────────────────────────

    /// <summary>Read replicas of this connection.</summary>
    [ConfigField(Label = "Replicas", Group = "Replicas", Order = 90)]
    public List<ReplicaConfig> Replicas { get; set; } = [];

    /// <summary>Where reads go when replicas are configured.</summary>
    [ConfigField(Label = "Read Preference", Group = "Replicas", Order = 91)]
    public ReadPreference ReadPreference { get; set; } = ReadPreference.PreferReplica;

    /// <summary>After a write, reads stay on the primary this many milliseconds (read-your-writes).</summary>
    [ConfigField(Label = "Sticky After Write (ms)", Min = 0, Group = "Replicas", Order = 92)]
    public int StickyAfterWriteMs { get; set; } = 2000;

    /// <summary>Replicas lagging more than this many milliseconds are skipped (0 = no lag check).</summary>
    [ConfigField(Label = "Max Replica Lag (ms)", Min = 0, Group = "Replicas", Order = 93)]
    public int MaxReplicaLagMs { get; set; } = 5000;

    /// <summary>Seconds between replica health and lag checks.</summary>
    [ConfigField(Label = "Replica Probe Interval (s)", Min = 1, Group = "Replicas", Order = 94, Collapsed = true)]
    public int ReplicaProbeIntervalSeconds { get; set; } = 10;

    // ── Provider options ────────────────────────────────────────────────────

    /// <summary>MySQL / MariaDB options.</summary>
    [ConfigField(Label = "MySQL", Group = "Provider", Order = 100, Collapsed = true)]
    public MySqlOptions? MySql { get; set; }

    /// <summary>PostgreSQL options.</summary>
    [ConfigField(Label = "PostgreSQL", Group = "Provider", Order = 101, Collapsed = true)]
    public PostgreSqlOptions? PostgreSql { get; set; }

    /// <summary>SQL Server options.</summary>
    [ConfigField(Label = "SQL Server", Group = "Provider", Order = 102, Collapsed = true)]
    public SqlServerOptions? SqlServer { get; set; }

    /// <summary>SQLite options.</summary>
    [ConfigField(Label = "SQLite", Group = "Provider", Order = 103, Collapsed = true)]
    public SqliteOptions? Sqlite { get; set; }

    /// <summary>Free-form options for third-party providers.</summary>
    [ConfigField(Label = "Options", Group = "Provider", Order = 104, Collapsed = true)]
    public Dictionary<string, string> Options
    {
        get;
        set => field = value is null ? [with(StringComparer.OrdinalIgnoreCase)] : new(value, StringComparer.OrdinalIgnoreCase);
    } = [with(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The provider name, trimmed and lower-cased.</summary>
    public string ProviderKey => Provider.Trim().ToLowerInvariant();

    internal void Validate(string path, List<ConfigFieldError> errors)
    {
        if (string.IsNullOrWhiteSpace(Provider))
            errors.Add(new($"{path}.provider", "A provider is required (mysql, postgresql, sqlserver, sqlite, ...)."));

        var isSqlite = ProviderKey == "sqlite";
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            if (string.IsNullOrWhiteSpace(Database))
                errors.Add(new($"{path}.database", isSqlite
                    ? "A database file path (or :memory:) or a connection string is required."
                    : "A database name or a connection string is required."));
            if (!isSqlite && string.IsNullOrWhiteSpace(Host))
                errors.Add(new($"{path}.host", "A host or a connection string is required."));
        }

        if (Port is < 1 or > 65535)
            errors.Add(new($"{path}.port", "Port must be between 1 and 65535."));
        if (MinPoolSize < 0)
            errors.Add(new($"{path}.minPoolSize", "Min pool size cannot be negative."));
        if (MaxPoolSize < 1 || MaxPoolSize < MinPoolSize)
            errors.Add(new($"{path}.maxPoolSize", "Max pool size must be at least 1 and not below the min pool size."));
        if (ConnectTimeoutSeconds < 1)
            errors.Add(new($"{path}.connectTimeoutSeconds", "Connect timeout must be at least 1 second."));
        if (CommandTimeoutSeconds < 1)
            errors.Add(new($"{path}.commandTimeoutSeconds", "Command timeout must be at least 1 second."));
        if (MaxBatchSize < 1)
            errors.Add(new($"{path}.maxBatchSize", "Max batch size must be at least 1."));
        if (DefaultStringSize < 1)
            errors.Add(new($"{path}.defaultStringSize", "Default string size must be at least 1."));
        if (RetryCount < 0 || RetryBaseDelayMs < 0 || CircuitBreakerThreshold < 0 || CircuitBreakSeconds < 1)
            errors.Add(new($"{path}.retryCount", "Retry and circuit breaker settings must not be negative."));
        // VerifyCA/VerifyFull without a CA path is allowed: the system trust store is used.

        if (isSqlite && Replicas.Count > 0)
            errors.Add(new($"{path}.replicas", "SQLite does not support replicas."));

        HashSet<string> names = [with(StringComparer.OrdinalIgnoreCase)];
        for (var i = 0; i < Replicas.Count; i++)
        {
            var replica = Replicas[i];
            var rp = $"{path}.replicas[{i}]";
            if (string.IsNullOrWhiteSpace(replica.Name))
                errors.Add(new($"{rp}.name", "Replica name is required."));
            else if (string.Equals(replica.Name, "primary", StringComparison.OrdinalIgnoreCase))
                errors.Add(new($"{rp}.name", "'primary' is reserved for the primary node."));
            else if (!names.Add(replica.Name))
                errors.Add(new($"{rp}.name", $"Replica name '{replica.Name}' is used twice."));
            if (string.IsNullOrWhiteSpace(replica.Host) && string.IsNullOrWhiteSpace(replica.ConnectionString))
                errors.Add(new($"{rp}.host", "A replica needs a host or a connection string."));
            if (replica.Port is < 1 or > 65535)
                errors.Add(new($"{rp}.port", "Port must be between 1 and 65535."));
            if (replica.Weight < 1)
                errors.Add(new($"{rp}.weight", "Weight must be at least 1."));
        }

        if (ReadPreference == ReadPreference.ReplicaOnly && Replicas.Count(r => r.Enabled) == 0)
            errors.Add(new($"{path}.readPreference", "ReplicaOnly needs at least one enabled replica."));
    }
}

/// <summary>A read replica of a connection. Unset credentials and database fall back to the primary's.</summary>
public sealed class ReplicaConfig
{
    /// <summary>Unique name, used in health reports, events and <c>ReadFrom</c>.</summary>
    [ConfigField(Label = "Name", Required = true, Order = 0)]
    public string Name { get; set; } = "";

    /// <summary>Turn this replica on or off.</summary>
    [ConfigField(Label = "Enabled", Order = 1)]
    public bool Enabled { get; set; } = true;

    /// <summary>Full connection string; when set it replaces the primary's.</summary>
    [ConfigField(Label = "Connection String", InputType = ConfigInputType.Password, Secret = true, Order = 2)]
    public string? ConnectionString { get; set; }

    /// <summary>Replica host.</summary>
    [ConfigField(Label = "Host", Order = 3)]
    public string? Host { get; set; }

    /// <summary>Replica port; null = the primary's port.</summary>
    [ConfigField(Label = "Port", Min = 1, Max = 65535, Order = 4)]
    public int? Port { get; set; }

    /// <summary>User name override.</summary>
    [ConfigField(Label = "Username", Order = 5)]
    public string? Username { get; set; }

    /// <summary>Password override.</summary>
    [ConfigField(Label = "Password", InputType = ConfigInputType.Password, Secret = true, Order = 6)]
    public string? Password { get; set; }

    /// <summary>Relative share of reads (weighted random selection among healthy replicas).</summary>
    [ConfigField(Label = "Weight", Min = 1, Order = 7)]
    public int Weight { get; set; } = 1;
}
