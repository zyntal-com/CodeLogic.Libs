namespace CL.MySQL2.Models;

/// <summary>
/// Marks a class as a database table entity.
/// Apply to a class to enable table sync and repository operations.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class TableAttribute : Attribute
{
    /// <summary>Custom table name. Defaults to the class name if null.</summary>
    public string? Name { get; set; }

    /// <summary>MySQL storage engine. Default: InnoDB.</summary>
    public TableEngine Engine { get; set; } = TableEngine.InnoDB;

    /// <summary>Table character set. Default: utf8mb4.</summary>
    public Charset Charset { get; set; } = Charset.Utf8mb4;

    /// <summary>Table collation (e.g., "utf8mb4_unicode_ci"). Optional.</summary>
    public string? Collation { get; set; }

    /// <summary>Table comment. Optional.</summary>
    public string? Comment { get; set; }
}

/// <summary>
/// Maps a property to a database column with optional schema metadata.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ColumnAttribute : Attribute
{
    /// <summary>Custom column name. Defaults to the property name if null.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Former column name, used by schema sync to <c>CHANGE COLUMN</c> (rename in place,
    /// preserving data) instead of drop-old + add-new. Set this to the previous column
    /// name when you rename a property/column; remove it once every environment has
    /// synced. Default: null (no rename).
    /// </summary>
    public string? PreviousName { get; set; } = null;

    /// <summary>MySQL data type for the column.</summary>
    public DataType DataType { get; set; }

    /// <summary>Column size / length (e.g., 255 for VARCHAR(255)). 0 = type default.</summary>
    public int Size { get; set; } = 0;

    /// <summary>Numeric precision (for DECIMAL columns). Default: 10.</summary>
    public int Precision { get; set; } = 10;

    /// <summary>Numeric scale (decimal places, for DECIMAL columns). Default: 2.</summary>
    public int Scale { get; set; } = 2;

    /// <summary>Whether this column is the primary key.</summary>
    public bool Primary { get; set; } = false;

    /// <summary>Whether this column uses AUTO_INCREMENT.</summary>
    public bool AutoIncrement { get; set; } = false;

    /// <summary>Whether this column has a NOT NULL constraint.</summary>
    public bool NotNull { get; set; } = false;

    /// <summary>Whether this column has a UNIQUE constraint.</summary>
    public bool Unique { get; set; } = false;

    /// <summary>Whether this column has a plain INDEX.</summary>
    public bool Index { get; set; } = false;

    /// <summary>
    /// Default value as raw SQL (e.g., "0", "'active'", "CURRENT_TIMESTAMP"). An empty string means
    /// an empty-string default (<c>DEFAULT ''</c>) on CHAR/VARCHAR columns and is ignored on other types.
    /// </summary>
    public string? DefaultValue { get; set; }

    /// <summary>Column-level character set override (for string types). Optional.</summary>
    public string? Charset { get; set; }

    /// <summary>Column comment. Optional.</summary>
    public string? Comment { get; set; }

    /// <summary>Whether this column is UNSIGNED (for numeric types).</summary>
    public bool Unsigned { get; set; } = false;

    /// <summary>Whether to add ON UPDATE CURRENT_TIMESTAMP (for TIMESTAMP/DATETIME).</summary>
    public bool OnUpdateCurrentTimestamp { get; set; } = false;

    /// <summary>
    /// Optional physical-storage override. When set, takes precedence over <see cref="DataType"/>
    /// for DDL generation and may activate automatic value conversion.
    /// </summary>
    public StorageType StorageType { get; set; } = StorageType.Default;
}

/// <summary>
/// Declares a foreign key constraint on a column property.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ForeignKeyAttribute : Attribute
{
    /// <summary>The referenced table name. Required.</summary>
    public string ReferenceTable { get; }

    /// <summary>The referenced column name. Required.</summary>
    public string ReferenceColumn { get; }

    /// <summary>Action when the referenced row is deleted. Default: Restrict.</summary>
    public ForeignKeyAction OnDelete { get; set; } = ForeignKeyAction.Restrict;

    /// <summary>Action when the referenced row is updated. Default: Restrict.</summary>
    public ForeignKeyAction OnUpdate { get; set; } = ForeignKeyAction.Restrict;

    /// <summary>Custom constraint name. Auto-generated if null.</summary>
    public string? ConstraintName { get; set; }

    /// <summary>
    /// Declares a foreign key to the specified table and column.
    /// </summary>
    /// <param name="referenceTable">The referenced table name.</param>
    /// <param name="referenceColumn">The referenced column name.</param>
    public ForeignKeyAttribute(string referenceTable, string referenceColumn)
    {
        ReferenceTable = referenceTable;
        ReferenceColumn = referenceColumn;
    }
}

/// <summary>Referential action for ON DELETE / ON UPDATE foreign key clauses.</summary>
public enum ForeignKeyAction
{
    Restrict,
    Cascade,
    SetNull,
    NoAction,
    SetDefault
}

/// <summary>
/// Excludes a property from all database operations (read, write, schema sync).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class IgnoreAttribute : Attribute { }

/// <summary>
/// Declares an index on the annotated property. Supersedes <c>Column(Index=true)</c> —
/// provides control over the name, uniqueness, and covering <c>Include</c> columns.
/// Can appear multiple times per property for rare multi-index cases.
/// <para>
/// <b>Include</b> lets you build a covering index: leaf pages carry the named extra
/// columns so queries that filter on the indexed column and project only the included
/// columns can answer from the index alone (no PK lookup per row). Property names map
/// to their underlying column names via <see cref="ColumnAttribute.Name"/>.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public sealed class IndexAttribute : Attribute
{
    /// <summary>
    /// Optional index name. If null, the sync generates <c>idx_{table}_{column}</c>
    /// (or <c>uq_{table}_{column}</c> for unique).
    /// </summary>
    public string? Name { get; set; }

    /// <summary>Whether this is a UNIQUE index.</summary>
    public bool Unique { get; set; } = false;

    /// <summary>
    /// Additional property names stored at the index leaf (covering index). Use this
    /// when you frequently <c>WHERE</c> on the indexed column and <c>SELECT</c> only
    /// these extra columns — the query becomes an index-only scan.
    /// </summary>
    public string[]? Include { get; set; }
}

/// <summary>
/// Declares a composite index spanning multiple columns.
/// Apply to the class (not a single property) by using the index column names.
/// Can be applied multiple times for multiple composite indexes.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class CompositeIndexAttribute : Attribute
{
    /// <summary>The index name (must be unique within the table).</summary>
    public string IndexName { get; }

    /// <summary>The column names included in this index.</summary>
    public string[] ColumnNames { get; }

    /// <summary>Whether this is a unique index. Default: false.</summary>
    public bool Unique { get; set; } = false;

    /// <summary>
    /// Declares a composite index with the given name covering the specified columns.
    /// </summary>
    /// <param name="indexName">Index name (unique within the table).</param>
    /// <param name="columnNames">One or more column names to include.</param>
    public CompositeIndexAttribute(string indexName, params string[] columnNames)
    {
        IndexName = indexName;
        ColumnNames = columnNames;
    }
}

/// <summary>
/// Declares a retention policy for the annotated entity. The library's background
/// purge worker periodically deletes rows whose <see cref="TimestampColumn"/> is older
/// than <see cref="Days"/>. Replaces hand-rolled cleanup jobs.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RetainDaysAttribute : Attribute
{
    /// <summary>Rows older than this many days are deleted.</summary>
    public int Days { get; }

    /// <summary>
    /// The property name on the entity that holds the timestamp to compare
    /// (e.g. <c>nameof(FooRecord.CreatedUtc)</c>). Must map to a DATETIME column.
    /// </summary>
    public string TimestampColumn { get; }

    /// <summary>
    /// How many rows to delete per batch. Keeps the delete transaction small.
    /// Default: 5000.
    /// </summary>
    public int BatchSize { get; set; } = 5000;

    /// <param name="days">Retain rows for this many days.</param>
    /// <param name="timestampColumn">Property name holding the timestamp to compare.</param>
    public RetainDaysAttribute(int days, string timestampColumn)
    {
        Days = days;
        TimestampColumn = timestampColumn;
    }
}

/// <summary>
/// Marks an entity for <b>soft deletes</b>. <see cref="CL.MySQL2.Services.Repository{T}.DeleteAsync(object, System.Threading.CancellationToken)"/>
/// sets the named timestamp column to the current UTC time instead of issuing a physical DELETE, and
/// reads through <c>mysql.Query&lt;T&gt;()</c> and the repository getters
/// (<c>GetByIdAsync</c>, <c>GetByColumnAsync</c>, <c>GetAllAsync</c>, <c>GetPagedAsync</c>,
/// <c>FindAsync</c>) automatically exclude rows where that column is set
/// (<c>WHERE col IS NULL</c>). <c>Repository{T}.CountAsync</c> is the exception — it counts
/// every row, soft-deleted included. Opt back in to deleted rows with
/// <c>.IncludeDeleted()</c> on a query, or purge for real with
/// <see cref="CL.MySQL2.Services.Repository{T}.HardDeleteAsync(object, System.Threading.CancellationToken)"/>.
/// <para>
/// The referenced property must be a nullable <see cref="System.DateTime"/> mapped to a
/// NULL-able <c>DATETIME</c> column. Auto soft-delete filtering applies to single-table
/// queries and repository reads — not to joins, subqueries, or bulk
/// <c>UpdateAsync</c>/<c>DeleteAsync</c> on the query builder.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SoftDeleteAttribute : Attribute
{
    /// <summary>
    /// Property name holding the soft-delete timestamp (a nullable <see cref="System.DateTime"/>),
    /// e.g. <c>nameof(DeletedUtc)</c>.
    /// </summary>
    public string TimestampColumn { get; }

    /// <param name="timestampColumn">Property name of the nullable DateTime soft-delete column.</param>
    public SoftDeleteAttribute(string timestampColumn) => TimestampColumn = timestampColumn;
}

