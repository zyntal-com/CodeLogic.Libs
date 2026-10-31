using CodeLogic.Core.Configuration;

namespace CL.Data.Configuration;

/// <summary>MySQL / MariaDB options of a connection (<c>"mySql": { ... }</c>).</summary>
public sealed class MySqlOptions
{
    /// <summary>Connection character set.</summary>
    [ConfigField(Label = "Character Set", RequiresRestart = true, Order = 0)]
    public string CharacterSet { get; set; } = "utf8mb4";

    /// <summary>Default collation for created tables.</summary>
    [ConfigField(Label = "Collation", RequiresRestart = true, Order = 1)]
    public string Collation { get; set; } = "utf8mb4_unicode_ci";

    /// <summary>Default storage engine for created tables.</summary>
    [ConfigField(Label = "Default Engine", Order = 2)]
    public string DefaultEngine { get; set; } = "InnoDB";

    /// <summary>Allow fetching the server's RSA public key (some MySQL 8 setups without TLS).</summary>
    [ConfigField(Label = "Allow Public Key Retrieval", RequiresRestart = true, Order = 3)]
    public bool AllowPublicKeyRetrieval { get; set; }

    /// <summary>
    /// Allow <c>LOAD DATA LOCAL INFILE</c>, which native bulk copy needs (the server must also have
    /// <c>local_infile</c> on). When off, bulk copy falls back to batched multi-row inserts.
    /// </summary>
    [ConfigField(Label = "Allow Load Local Infile", RequiresRestart = true, Order = 4)]
    public bool AllowLoadLocalInfile { get; set; }
}

/// <summary>PostgreSQL options of a connection (<c>"postgreSql": { ... }</c>).</summary>
public sealed class PostgreSqlOptions
{
    /// <summary>Schema for tables without an explicit schema.</summary>
    [ConfigField(Label = "Default Schema", Order = 0)]
    public string DefaultSchema { get; set; } = "public";

    /// <summary>Statements Npgsql prepares automatically per connection (0 = off).</summary>
    [ConfigField(Label = "Max Auto Prepare", Min = 0, RequiresRestart = true, Order = 1)]
    public int MaxAutoPrepare { get; set; } = 64;

    /// <summary>Statement uses before Npgsql auto-prepares it.</summary>
    [ConfigField(Label = "Auto Prepare Min Usages", Min = 1, RequiresRestart = true, Order = 2)]
    public int AutoPrepareMinUsages { get; set; } = 3;

    /// <summary>Npgsql multiplexing: many commands share fewer physical connections.</summary>
    [ConfigField(Label = "Multiplexing", RequiresRestart = true, Order = 3)]
    public bool Multiplexing { get; set; }
}

/// <summary>How SQL Server connections authenticate.</summary>
public enum SqlServerAuthentication
{
    /// <summary>SQL login (user name and password).</summary>
    SqlPassword,
    /// <summary>Windows integrated security.</summary>
    Integrated,
    /// <summary>Microsoft Entra ID, default credential chain.</summary>
    EntraDefault,
    /// <summary>Microsoft Entra ID, managed identity (user name = client id for user-assigned).</summary>
    EntraManagedIdentity,
    /// <summary>Microsoft Entra ID, service principal (user name = client id, password = secret).</summary>
    EntraServicePrincipal,
    /// <summary>Microsoft Entra ID, user name and password.</summary>
    EntraPassword,
}

/// <summary>SQL Server options of a connection (<c>"sqlServer": { ... }</c>).</summary>
public sealed class SqlServerOptions
{
    /// <summary>Schema for tables without an explicit schema.</summary>
    [ConfigField(Label = "Default Schema", Order = 0)]
    public string DefaultSchema { get; set; } = "dbo";

    /// <summary>Authentication mode.</summary>
    [ConfigField(Label = "Authentication", RequiresRestart = true, Order = 1)]
    public SqlServerAuthentication Authentication { get; set; } = SqlServerAuthentication.SqlPassword;

    /// <summary>Encrypt the connection.</summary>
    [ConfigField(Label = "Encrypt", RequiresRestart = true, Order = 2)]
    public bool Encrypt { get; set; } = true;

    /// <summary>Accept the server certificate without validation (development only).</summary>
    [ConfigField(Label = "Trust Server Certificate", RequiresRestart = true, Order = 3)]
    public bool TrustServerCertificate { get; set; }

    /// <summary>Faster failover for Always On availability group listeners.</summary>
    [ConfigField(Label = "Multi-Subnet Failover", RequiresRestart = true, Order = 4)]
    public bool MultiSubnetFailover { get; set; }

    /// <summary>Database mirroring failover partner.</summary>
    [ConfigField(Label = "Failover Partner", RequiresRestart = true, Order = 5)]
    public string? FailoverPartner { get; set; }

    /// <summary>Application name reported to the server.</summary>
    [ConfigField(Label = "Application Name", RequiresRestart = true, Order = 6)]
    public string? ApplicationName { get; set; }
}

/// <summary>SQLite journal modes.</summary>
public enum SqliteJournalMode
{
    /// <summary>Write-ahead log: readers don't block the writer. Recommended.</summary>
    Wal,
    /// <summary>Rollback journal, deleted after each transaction.</summary>
    Delete,
    /// <summary>Rollback journal, truncated after each transaction.</summary>
    Truncate,
    /// <summary>In-memory journal.</summary>
    Memory,
}

/// <summary>SQLite <c>synchronous</c> levels.</summary>
public enum SqliteSynchronous
{
    /// <summary>No fsync. Fast, risks corruption on power loss.</summary>
    Off,
    /// <summary>Safe with WAL; recommended.</summary>
    Normal,
    /// <summary>fsync at every commit.</summary>
    Full,
}

/// <summary>SQLite options of a connection (<c>"sqlite": { ... }</c>). The file path goes in <c>database</c>.</summary>
public sealed class SqliteOptions
{
    /// <summary>Journal mode.</summary>
    [ConfigField(Label = "Journal Mode", RequiresRestart = true, Order = 0)]
    public SqliteJournalMode JournalMode { get; set; } = SqliteJournalMode.Wal;

    /// <summary>Synchronous level.</summary>
    [ConfigField(Label = "Synchronous", RequiresRestart = true, Order = 1)]
    public SqliteSynchronous Synchronous { get; set; } = SqliteSynchronous.Normal;

    /// <summary>Milliseconds to wait for a lock held by another connection.</summary>
    [ConfigField(Label = "Busy Timeout (ms)", Min = 0, RequiresRestart = true, Order = 2)]
    public int BusyTimeoutMs { get; set; } = 5000;

    /// <summary>Enforce foreign keys.</summary>
    [ConfigField(Label = "Foreign Keys", RequiresRestart = true, Order = 3)]
    public bool ForeignKeys { get; set; } = true;

    /// <summary>Open the database read-only.</summary>
    [ConfigField(Label = "Read Only", RequiresRestart = true, Order = 4)]
    public bool ReadOnly { get; set; }

    /// <summary>Create the database file when it does not exist.</summary>
    [ConfigField(Label = "Create File", RequiresRestart = true, Order = 5)]
    public bool CreateFile { get; set; } = true;
}
