namespace CL.PostgreSQL.Models;

/// <summary>Native PostgreSQL column data types.</summary>
public enum DataType
{
    /// <summary>
    /// No explicit type was declared on the column. The type is inferred from the CLR
    /// property type via <see cref="CL.PostgreSQL.Core.TypeConverter.InferColumn"/>.
    /// </summary>
    Unspecified = 0,

    // Numeric
    SmallInt,
    Int,
    BigInt,
    SmallSerial,
    Serial,
    BigSerial,
    Real,
    DoublePrecision,
    Numeric,
    Money,

    // Character
    Char,
    VarChar,
    Text,

    // Binary
    Bytea,

    // Date/Time
    Date,
    Time,
    TimeTz,
    Timestamp,
    TimestampTz,
    Interval,

    // Boolean / identity
    Bool,
    Uuid,

    // Structured
    Json,
    Jsonb,
    Xml,

    // Network / bit
    Inet,
    Cidr,
    MacAddr,
    Bit,
    VarBit,

    // Ranges
    Int4Range,
    Int8Range,
    NumRange,
    TsRange,
    TsTzRange,
    DateRange,

    // Arrays
    SmallIntArray,
    IntArray,
    BigIntArray,
    TextArray,
    VarCharArray,
    NumericArray,
    UuidArray,
    BoolArray,
    JsonbArray
}

/// <summary>
/// Optional physical-storage override for a column. When set to anything other than
/// <see cref="StorageType.Default"/>, the storage type takes precedence over
/// <see cref="DataType"/> for DDL generation. For certain CLR types automatic value
/// conversion is applied (e.g. a <see cref="System.Guid"/> property with
/// <see cref="StorageType.Binary"/> is stored as <c>bytea</c> with big-endian byte conversion).
/// </summary>
public enum StorageType
{
    /// <summary>No override — use <see cref="DataType"/> as-is.</summary>
    Default = 0,

    /// <summary>
    /// Raw binary (<c>bytea</c>). PostgreSQL has a single variable-length binary type, so the
    /// MySQL family of fixed/blob sizes collapses onto it; <see cref="ColumnAttribute.Size"/>
    /// is advisory only.
    /// </summary>
    Binary,

    /// <summary>Alias for <see cref="Binary"/>; PostgreSQL does not distinguish the two.</summary>
    VarBinary
}

/// <summary>
/// How a table's rows are physically laid out. PostgreSQL has no pluggable storage engines
/// in the MySQL sense; this selects the built-in table access method.
/// </summary>
public enum TableAccessMethod
{
    /// <summary>The default heap access method.</summary>
    Heap,

    /// <summary>An extension-provided access method, named by <see cref="TableAttribute.AccessMethodName"/>.</summary>
    Custom
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
