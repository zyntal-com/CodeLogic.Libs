namespace CL.MySQL2.Models;

/// <summary>MySQL column data types.</summary>
public enum DataType
{
    /// <summary>
    /// No explicit type was declared on the column. The type is inferred from the CLR
    /// property type via <see cref="CL.MySQL2.Core.TypeConverter.InferColumn"/>. This is the
    /// default so that <c>[Column(Name = "x")]</c> without a <c>DataType</c> infers correctly
    /// instead of silently resolving to <see cref="TinyInt"/>.
    /// </summary>
    Unspecified = 0,

    // Numeric
    TinyInt,
    SmallInt,
    MediumInt,
    Int,
    BigInt,
    Decimal,
    Float,
    Double,
    Bit,

    // String
    Char,
    VarChar,
    TinyText,
    Text,
    MediumText,
    LongText,
    Enum,
    Set,

    // Binary
    Binary,
    VarBinary,
    TinyBlob,
    Blob,
    MediumBlob,
    LongBlob,

    // Date/Time
    Date,
    Time,
    DateTime,
    Timestamp,
    Year,

    // Spatial / JSON
    Json,
    Geometry
}

/// <summary>
/// Optional physical-storage override for a column. When set to anything other than
/// <see cref="StorageType.Default"/>, the storage type takes precedence over
/// <see cref="DataType"/> for DDL generation. For certain CLR types automatic value
/// conversion is applied (e.g. a <see cref="System.Guid"/> property with
/// <see cref="StorageType.Binary"/> is stored as <c>BINARY(16)</c> with big-endian byte conversion).
/// </summary>
public enum StorageType
{
    /// <summary>No override — use <see cref="DataType"/> as-is.</summary>
    Default = 0,

    /// <summary>Fixed-length binary. Uses <see cref="ColumnAttribute.Size"/> for explicit length; for <see cref="System.Guid"/> auto-sizes to 16.</summary>
    Binary,

    /// <summary>Variable-length binary. Uses <see cref="ColumnAttribute.Size"/> (default 255).</summary>
    VarBinary,

    /// <summary>Tiny binary large object (max 255 bytes).</summary>
    TinyBlob,

    /// <summary>Binary large object (max 65 535 bytes).</summary>
    Blob,

    /// <summary>Medium binary large object (max 16 MB).</summary>
    MediumBlob,

    /// <summary>Long binary large object (max 4 GB).</summary>
    LongBlob
}

/// <summary>MySQL storage engine options.</summary>
public enum TableEngine
{
    InnoDB,
    MyISAM,
    Memory,
    Archive,
    CSV,
    NDB
}

/// <summary>MySQL character set options.</summary>
public enum Charset
{
    Utf8mb4,
    Utf8,
    Latin1,
    Ascii,
    Binary
}

/// <summary>Sort order for query results.</summary>
public enum SortOrder
{
    Asc,
    Desc
}

/// <summary>
/// Controls how aggressively the table sync service reconciles the live database
/// schema with entity definitions. Higher levels allow more destructive operations.
/// </summary>
public enum SchemaSyncLevel
{
    /// <summary>Skip sync entirely. No ALTER/CREATE statements are issued.</summary>
    None = 0,

    /// <summary>
    /// Default. Adds missing columns, indexes and foreign keys, and modifies existing
    /// columns to match the model (grow VARCHAR, toggle NULL, update default, etc.).
    /// Never drops anything.
    /// </summary>
    Safe = 1,

    /// <summary>
    /// Safe + drops indexes and foreign keys that no longer exist in the model.
    /// No column data is lost.
    /// </summary>
    Additive = 2,

    /// <summary>
    /// Additive + drops columns that no longer exist in the model, and allows full
    /// DROP TABLE rebuild when used together with backups. Intended for development only.
    /// </summary>
    Full = 3
}

/// <summary>
/// Operator-facing schema sync mode. This is the primary knob exposed in configuration;
/// it maps onto an internal <see cref="SchemaSyncLevel"/> and governs how the CRC-gated
/// <c>__schema_state</c> sentinel reconciles the database with the entity models.
/// </summary>
public enum SyncMode
{
    /// <summary>
    /// Rolling-update development mode. Reconciles aggressively on every boot, dropping
    /// removed columns/indexes/FKs without asking (maps to <see cref="SchemaSyncLevel.Full"/>).
    /// Unchanged models are still skipped via their stored CRC.
    /// </summary>
    Developer = 0,

    /// <summary>
    /// Default. Safe/additive only — adds and modifies columns/indexes/FKs but never drops
    /// (maps to <see cref="SchemaSyncLevel.Safe"/>). When a model change would require a drop,
    /// the change is deferred and the table is flagged <c>DriftPending</c> for a later Migration pass.
    /// </summary>
    Production = 1,

    /// <summary>
    /// Deliberate one-shot destructive reconcile (maps to <see cref="SchemaSyncLevel.Full"/>,
    /// with a schema backup taken first). Idempotent: once every model matches its stored CRC and
    /// no drift is pending, the pass does nothing and logs a warning to switch back to
    /// <see cref="Production"/>.
    /// </summary>
    Migration = 2
}

/// <summary>
/// Result outcome of an <see cref="Services.Repository{T}.InsertIgnoreAsync"/> operation.
/// </summary>
public enum InsertOutcome
{
    /// <summary>Row was successfully inserted.</summary>
    Inserted,

    /// <summary>A row with matching unique/primary key already existed; insert was ignored.</summary>
    Duplicate
}
