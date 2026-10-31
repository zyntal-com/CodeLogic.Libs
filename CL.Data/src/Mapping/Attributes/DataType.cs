namespace CL.Data;

/// <summary>
/// Provider-neutral column type. Each provider maps it to its native type (for example
/// <see cref="String"/> → <c>VARCHAR(n)</c> on MySQL, <c>varchar(n)</c> on PostgreSQL,
/// <c>nvarchar(n)</c> on SQL Server, <c>TEXT</c> on SQLite). Leave it <see cref="Unspecified"/> to infer
/// the type from the property's CLR type; use <c>[Column(NativeType = ...)]</c> or a provider hint
/// attribute for an exact native type.
/// </summary>
public enum DataType
{
    /// <summary>Infer from the CLR type.</summary>
    Unspecified = 0,

    // ── Numbers ─────────────────────────────────────────────────────────────

    /// <summary>Boolean (<c>bool</c>).</summary>
    Boolean,
    /// <summary>8-bit integer (<c>byte</c>/<c>sbyte</c>).</summary>
    TinyInt,
    /// <summary>16-bit integer (<c>short</c>).</summary>
    SmallInt,
    /// <summary>32-bit integer (<c>int</c>).</summary>
    Int,
    /// <summary>64-bit integer (<c>long</c>).</summary>
    BigInt,
    /// <summary>Exact decimal with <c>Precision</c> and <c>Scale</c> (<c>decimal</c>).</summary>
    Decimal,
    /// <summary>Single-precision float (<c>float</c>).</summary>
    Real,
    /// <summary>Double-precision float (<c>double</c>).</summary>
    Double,

    // ── Text ────────────────────────────────────────────────────────────────

    /// <summary>Variable-length text up to <c>Size</c> characters (<c>string</c>); size 0 uses the connection's default.</summary>
    String,
    /// <summary>Fixed-length text of <c>Size</c> characters.</summary>
    FixedString,
    /// <summary>Unbounded text.</summary>
    Text,

    // ── Binary ──────────────────────────────────────────────────────────────

    /// <summary>Variable-length bytes up to <c>Size</c> (<c>byte[]</c>).</summary>
    Binary,
    /// <summary>Fixed-length bytes of <c>Size</c>.</summary>
    FixedBinary,
    /// <summary>Unbounded bytes.</summary>
    Blob,

    // ── Identity and time ───────────────────────────────────────────────────

    /// <summary>UUID (<c>Guid</c>). Stored natively where available, otherwise as 16 bytes or text.</summary>
    Guid,
    /// <summary>Calendar date (<c>DateOnly</c>).</summary>
    Date,
    /// <summary>Time of day (<c>TimeOnly</c>).</summary>
    Time,
    /// <summary>Date and time without offset (<c>DateTime</c>, stored as UTC).</summary>
    DateTime,
    /// <summary>Instant with offset (<c>DateTimeOffset</c>). The default for time columns.</summary>
    DateTimeOffset,
    /// <summary>Duration (<c>TimeSpan</c>).</summary>
    Interval,

    // ── Structured and special ──────────────────────────────────────────────

    /// <summary>JSON document. Native <c>json</c>/<c>jsonb</c> where available, otherwise text.</summary>
    Json,
    /// <summary>XML document (SQL Server, PostgreSQL); text elsewhere.</summary>
    Xml,
    /// <summary>Server-maintained row version used as a concurrency token (SQL Server <c>rowversion</c>).</summary>
    RowVersion,
    /// <summary>Planar spatial value.</summary>
    Geometry,
    /// <summary>Geodetic spatial value.</summary>
    Geography,
}

/// <summary>Referential action of a foreign key.</summary>
public enum ForeignKeyAction
{
    /// <summary>Reject the change while referencing rows exist.</summary>
    Restrict,
    /// <summary>Apply the change to referencing rows.</summary>
    Cascade,
    /// <summary>Set the referencing column to NULL.</summary>
    SetNull,
    /// <summary>Like <see cref="Restrict"/>, checked at the end of the statement.</summary>
    NoAction,
    /// <summary>Set the referencing column to its default.</summary>
    SetDefault,
}

/// <summary>Sort direction of an index column.</summary>
public enum SortOrder
{
    /// <summary>Ascending.</summary>
    Ascending,
    /// <summary>Descending.</summary>
    Descending,
}
