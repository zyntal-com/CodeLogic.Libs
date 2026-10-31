namespace CL.Data;

/// <summary>
/// Maps a class to a table. Declare the class <c>partial</c> so the CL.Data source generator can emit
/// its mapping at compile time (no reflection, Native AOT safe).
/// </summary>
/// <example>
/// <code>
/// [Table(Name = "users")]
/// public partial class User
/// {
///     [Column(Primary = true, AutoIncrement = true)] public long Id { get; set; }
///     [Column(Size = 320, Unique = true)] public string Email { get; set; } = "";
///     [CreatedAt] public DateTimeOffset CreatedAt { get; set; }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class TableAttribute : Attribute
{
    /// <summary>Table name; null derives it from the class name and the connection's naming policy.</summary>
    public string? Name { get; set; }

    /// <summary>Schema (PostgreSQL, SQL Server); null uses the connection's default schema.</summary>
    public string? Schema { get; set; }

    /// <summary>Table comment, where the provider supports comments.</summary>
    public string? Comment { get; set; }
}

/// <summary>
/// Marks a class or record that is read from queries but is not a table: raw SQL results, projections
/// and views. The generator emits a fast materializer for it.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class DataRowAttribute : Attribute;

/// <summary>Column settings for a property. Properties without it are still mapped, with inferred settings.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ColumnAttribute : Attribute
{
    /// <summary>Column name; null derives it from the property name and the naming policy.</summary>
    public string? Name { get; set; }

    /// <summary>Former column name. Table sync renames the column instead of adding a new one.</summary>
    public string? PreviousName { get; set; }

    /// <summary>Neutral column type; <see cref="DataType.Unspecified"/> infers it from the CLR type.</summary>
    public DataType DataType { get; set; }

    /// <summary>Length for text and binary types (0 = default; -1 = unbounded).</summary>
    public int Size { get; set; }

    /// <summary>Total digits for <see cref="DataType.Decimal"/> (0 = 18), or fractional-second digits for time types.</summary>
    public int Precision { get; set; }

    /// <summary>Digits after the decimal point for <see cref="DataType.Decimal"/> (-1 = default of 2).</summary>
    public int Scale { get; set; } = -1;

    /// <summary>Part of the primary key. For a composite key, set <see cref="KeyOrder"/> on every key column.</summary>
    public bool Primary { get; set; }

    /// <summary>Position in a composite primary key (1-based).</summary>
    public int KeyOrder { get; set; }

    /// <summary>Server-generated integer identity. The generated value is written back to the entity on insert.</summary>
    public bool AutoIncrement { get; set; }

    /// <summary>
    /// Force NOT NULL. Without it, nullability follows the property type (<c>int?</c>, <c>string?</c>) and its
    /// nullable reference type annotation.
    /// </summary>
    public bool NotNull { get; set; }

    /// <summary>Unique single-column index.</summary>
    public bool Unique { get; set; }

    /// <summary>Non-unique single-column index. Use <see cref="IndexAttribute"/> for names, ordering and included columns.</summary>
    public bool Index { get; set; }

    /// <summary>Raw SQL default expression (for example <c>0</c> or <c>CURRENT_TIMESTAMP</c>). Not validated across providers.</summary>
    public string? DefaultSql { get; set; }

    /// <summary>Column comment, where the provider supports comments.</summary>
    public string? Comment { get; set; }

    /// <summary>Exact native type for all providers (for example <c>MEDIUMTEXT</c>). Prefer a provider hint attribute when providers differ.</summary>
    public string? NativeType { get; set; }
}

/// <summary>The property is not mapped to a column.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class IgnoreAttribute : Attribute;

/// <summary>A foreign key from this column to <paramref name="referencedTable"/>.<paramref name="referencedColumn"/>.</summary>
/// <param name="referencedTable">Referenced table name.</param>
/// <param name="referencedColumn">Referenced column name.</param>
[AttributeUsage(AttributeTargets.Property)]
public class ForeignKeyAttribute(string referencedTable, string referencedColumn) : Attribute
{
    /// <summary>Referenced table name (null when <see cref="ReferencedEntity"/> is used).</summary>
    public string? ReferencedTable { get; } = referencedTable;

    /// <summary>Referenced column or property name.</summary>
    public string ReferencedColumn { get; } = referencedColumn;

    /// <summary>Referenced entity type, when declared with <see cref="ForeignKeyAttribute{TEntity}"/>.</summary>
    public virtual Type? ReferencedEntity => null;

    /// <summary>Action on delete of the referenced row.</summary>
    public ForeignKeyAction OnDelete { get; set; } = ForeignKeyAction.Restrict;

    /// <summary>Action on update of the referenced key.</summary>
    public ForeignKeyAction OnUpdate { get; set; } = ForeignKeyAction.Restrict;

    /// <summary>Constraint name; null generates <c>fk_{table}_{column}</c>.</summary>
    public string? ConstraintName { get; set; }
}

/// <summary>
/// A foreign key to the mapped entity <typeparamref name="TEntity"/>; the table name and column come from
/// its mapping. <c>[ForeignKey&lt;User&gt;(nameof(User.Id))]</c>.
/// </summary>
/// <typeparam name="TEntity">Referenced entity.</typeparam>
/// <param name="referencedProperty">Referenced property (usually the key).</param>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ForeignKeyAttribute<TEntity>(string referencedProperty) : ForeignKeyAttribute(null!, referencedProperty)
{
    /// <inheritdoc />
    public override Type? ReferencedEntity => typeof(TEntity);
}

/// <summary>An index on this column. Repeat it with the same <see cref="Name"/> on several properties for a multi-column index ordered by <see cref="Order"/>.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public sealed class IndexAttribute : Attribute
{
    /// <summary>Index name; null generates <c>ix_{table}_{columns}</c>.</summary>
    public string? Name { get; set; }

    /// <summary>Unique index.</summary>
    public bool Unique { get; set; }

    /// <summary>Position in a multi-column index (1-based).</summary>
    public int Order { get; set; }

    /// <summary>Sort direction of this column in the index.</summary>
    public SortOrder Direction { get; set; } = SortOrder.Ascending;

    /// <summary>Covering columns (property names). Native INCLUDE on SQL Server and PostgreSQL; appended key columns elsewhere.</summary>
    public string[]? Include { get; set; }
}

/// <summary>A multi-column index declared on the class.</summary>
/// <param name="name">Index name.</param>
/// <param name="properties">Indexed properties, in order.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
public sealed class CompositeIndexAttribute(string name, params string[] properties) : Attribute
{
    /// <summary>Index name.</summary>
    public string Name { get; } = name;

    /// <summary>Indexed properties, in order.</summary>
    public string[] Properties { get; } = properties;

    /// <summary>Unique index.</summary>
    public bool Unique { get; set; }

    /// <summary>Covering columns (property names).</summary>
    public string[]? Include { get; set; }
}

/// <summary>
/// Soft delete: <c>Delete</c> sets this nullable timestamp instead of removing the row, and queries skip rows
/// where it is set (unless <c>IncludeDeleted()</c>). The property must be <c>DateTimeOffset?</c> or <c>DateTime?</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SoftDeleteAttribute : Attribute;

/// <summary>Rows older than <paramref name="days"/> by this timestamp property are deleted by the retention worker.</summary>
/// <param name="days">Days to keep rows.</param>
[AttributeUsage(AttributeTargets.Property)]
public sealed class RetainDaysAttribute(int days) : Attribute
{
    /// <summary>Days to keep rows.</summary>
    public int Days { get; } = days;

    /// <summary>Rows deleted per statement.</summary>
    public int BatchSize { get; set; } = 5000;
}

/// <summary>
/// Optimistic concurrency token. Updates and deletes include it in the WHERE clause and fail with
/// <c>data.concurrency</c> when the row changed. An <c>int</c>/<c>long</c> is incremented by CL.Data; a
/// <c>byte[]</c> with <see cref="DataType.RowVersion"/> is maintained by SQL Server.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ConcurrencyTokenAttribute : Attribute;

/// <summary>Set to the current UTC time on insert.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class CreatedAtAttribute : Attribute;

/// <summary>Set to the current UTC time on insert and on every update.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class UpdatedAtAttribute : Attribute;

/// <summary>Set to the current user (from <c>IDataUserAccessor</c>) on insert.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class CreatedByAttribute : Attribute;

/// <summary>Set to the current user (from <c>IDataUserAccessor</c>) on insert and on every update.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class UpdatedByAttribute : Attribute;

/// <summary>
/// Tenant key: reads are filtered to the current tenant and writes are stamped with it. Using the entity
/// without a current tenant fails with <c>data.tenant_missing</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class TenantKeyAttribute : Attribute;
