namespace CL.Data.Mapping;

/// <summary>Special meanings a column can have. A column can carry several (for example key and identity).</summary>
[Flags]
public enum ColumnRoles
{
    /// <summary>A plain column.</summary>
    None = 0,
    /// <summary>Part of the primary key.</summary>
    Key = 1 << 0,
    /// <summary>Server-generated identity.</summary>
    Identity = 1 << 1,
    /// <summary>Soft-delete timestamp.</summary>
    SoftDelete = 1 << 2,
    /// <summary>Optimistic concurrency token.</summary>
    ConcurrencyToken = 1 << 3,
    /// <summary>Set on insert to the current time.</summary>
    CreatedAt = 1 << 4,
    /// <summary>Set on insert and update to the current time.</summary>
    UpdatedAt = 1 << 5,
    /// <summary>Set on insert to the current user.</summary>
    CreatedBy = 1 << 6,
    /// <summary>Set on insert and update to the current user.</summary>
    UpdatedBy = 1 << 7,
    /// <summary>Tenant key.</summary>
    TenantKey = 1 << 8,
    /// <summary>Retention timestamp.</summary>
    Retention = 1 << 9,
}

/// <summary>
/// One mapped property. Built by the source generator (or the reflection fallback) and frozen when its
/// <see cref="EntityMap"/> is created. Names here are as declared; the database name for a naming policy
/// comes from <see cref="EntityMap.GetNames"/>.
/// </summary>
public sealed class ColumnMap
{
    /// <summary>Property name.</summary>
    public required string PropertyName { get; init; }

    /// <summary>Declared CLR type of the property (may be <see cref="Nullable{T}"/>).</summary>
    public required Type ClrType { get; init; }

    /// <summary>Explicit column name from <c>[Column(Name = ...)]</c>; null derives it from <see cref="PropertyName"/>.</summary>
    public string? ExplicitName { get; init; }

    /// <summary>Former column name, for renames.</summary>
    public string? PreviousName { get; init; }

    /// <summary>
    /// True when the column accepts NULL: the type is <see cref="Nullable{T}"/> or a reference type annotated as
    /// nullable, and <c>NotNull</c> was not forced.
    /// </summary>
    public bool IsNullable { get; init; }

    /// <summary>Neutral type; <see cref="DataType.Unspecified"/> means a value converter decides.</summary>
    public DataType DataType { get; init; }

    /// <summary>Length for text and binary (0 = default, -1 = unbounded).</summary>
    public int Size { get; init; }

    /// <summary>Decimal precision or fractional-second digits (0 = default).</summary>
    public int Precision { get; init; }

    /// <summary>Decimal scale (-1 = default).</summary>
    public int Scale { get; init; } = -1;

    /// <summary>Position in a composite primary key (1-based); 0 when not part of the key or the key is single.</summary>
    public int KeyOrder { get; init; }

    /// <summary>Unique single-column index requested.</summary>
    public bool Unique { get; init; }

    /// <summary>Non-unique single-column index requested.</summary>
    public bool Indexed { get; init; }

    /// <summary>Raw SQL default expression.</summary>
    public string? DefaultSql { get; init; }

    /// <summary>Column comment.</summary>
    public string? Comment { get; init; }

    /// <summary>Exact native type for all providers.</summary>
    public string? NativeType { get; init; }

    /// <summary>Roles of this column.</summary>
    public ColumnRoles Roles { get; init; }

    /// <summary>Days to keep rows (when <see cref="ColumnRoles.Retention"/>).</summary>
    public int RetainDays { get; init; }

    /// <summary>Rows deleted per retention statement.</summary>
    public int RetentionBatchSize { get; init; } = 5000;

    /// <summary>Foreign key declared on this column.</summary>
    public ForeignKeyAttribute? ForeignKey { get; init; }

    /// <summary>Index attributes declared on this column.</summary>
    public IReadOnlyList<IndexAttribute> IndexAttributes { get; init; } = [];

    /// <summary>Provider hint attributes on the property (<c>[MySqlColumn]</c>, <c>[PostgresColumn]</c>, ...).</summary>
    public IReadOnlyList<Attribute> Hints { get; init; } = [];

    /// <summary>Position in <see cref="EntityMap.Columns"/>. Set when the map is created.</summary>
    public int Ordinal { get; internal set; } = -1;

    /// <summary>The non-nullable type (<c>int</c> for <c>int?</c>).</summary>
    public Type ValueType => Nullable.GetUnderlyingType(ClrType) ?? ClrType;

    /// <summary>True when the column has <paramref name="role"/>.</summary>
    public bool Has(ColumnRoles role) => (Roles & role) == role;

    /// <summary>Returns the provider hint of type <typeparamref name="THint"/>, if any.</summary>
    public THint? Hint<THint>() where THint : Attribute
    {
        foreach (var hint in Hints)
            if (hint is THint typed) return typed;
        return null;
    }

    /// <inheritdoc />
    public override string ToString() => $"{PropertyName} ({ClrType.Name})";
}
