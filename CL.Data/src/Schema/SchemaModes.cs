namespace CL.Data.Schema;

/// <summary>
/// How CL.Data treats differences between entity classes and database tables at startup.
/// </summary>
public enum SyncMode
{
    /// <summary>Apply every change, including dropping columns. For local development only.</summary>
    Developer,

    /// <summary>
    /// Create tables and add columns and indexes; never drop or narrow anything. Changes that would lose data
    /// are reported as drift (<c>SchemaDriftDetectedEvent</c>) instead of applied.
    /// </summary>
    Production,

    /// <summary>Never change the schema automatically; only migrations do. Drift is reported.</summary>
    Migration,
}

/// <summary>
/// Which schema changes table sync may apply. <see cref="SyncMode"/> picks a level; set
/// <c>DataConnectionConfig.SchemaSyncLevel</c> to override it.
/// </summary>
public enum SchemaSyncLevel
{
    /// <summary>No changes.</summary>
    None,

    /// <summary>Create tables, add columns, widen types, add indexes and foreign keys.</summary>
    Safe,

    /// <summary><see cref="Safe"/>, plus dropping indexes and foreign keys that are no longer declared.</summary>
    Additive,

    /// <summary><see cref="Additive"/>, plus dropping columns and narrowing types.</summary>
    Full,
}

/// <summary>Helpers for <see cref="SyncMode"/>.</summary>
public static class SyncModeExtensions
{
    /// <summary>The sync level a mode implies.</summary>
    public static SchemaSyncLevel DefaultLevel(this SyncMode mode) => mode switch
    {
        SyncMode.Developer => SchemaSyncLevel.Full,
        SyncMode.Production => SchemaSyncLevel.Safe,
        SyncMode.Migration => SchemaSyncLevel.None,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}
