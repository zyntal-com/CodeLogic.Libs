namespace CL.Data.Schema;

/// <summary>A column as the database sees it.</summary>
/// <param name="Name">Column name.</param>
/// <param name="StoreType">Native type, lower case, normalized by the provider (<c>varchar(64)</c>, <c>bigint</c>).</param>
/// <param name="Nullable">Accepts NULL.</param>
/// <param name="Identity">Server-generated identity.</param>
/// <param name="DefaultSql">Default expression, when declared.</param>
public sealed record ColumnSchema(string Name, string StoreType, bool Nullable, bool Identity = false, string? DefaultSql = null)
{
    /// <summary>Column comment.</summary>
    public string? Comment { get; init; }

    /// <summary>Collation, when declared.</summary>
    public string? Collation { get; init; }

    /// <summary>Former name (desired schemas only): the column is renamed instead of added.</summary>
    public string? PreviousName { get; init; }

    /// <summary>True for a JSON column stored as text; providers may add a validity check (SQL Server: <c>ISJSON</c>). Not compared by the diff.</summary>
    public bool Json { get; init; }
}

/// <summary>An index.</summary>
/// <param name="Name">Index name.</param>
/// <param name="Unique">Unique index.</param>
/// <param name="Columns">Key columns in order.</param>
public sealed record IndexSchema(string Name, bool Unique, IReadOnlyList<string> Columns)
{
    /// <summary>Descending flags per key column (empty = all ascending).</summary>
    public IReadOnlyList<bool> Descending { get; init; } = [];

    /// <summary>Covering columns.</summary>
    public IReadOnlyList<string> Include { get; init; } = [];

    /// <summary>
    /// Key columns that allow NULL. A unique index ignores rows with a NULL key column on MySQL, PostgreSQL and SQLite;
    /// providers that would treat NULLs as equal (SQL Server) filter those rows out so the model means the same
    /// everywhere. Not compared by the diff.
    /// </summary>
    public IReadOnlyList<string> NullableColumns { get; init; } = [];

    /// <summary>Structural equality on name-insensitive content (columns, uniqueness, directions, include).</summary>
    public bool SameShape(IndexSchema other) =>
        Unique == other.Unique &&
        Columns.SequenceEqual(other.Columns, StringComparer.OrdinalIgnoreCase) &&
        Include.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(other.Include.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase) &&
        (Descending.Count == 0 ? new bool[Columns.Count] : Descending).SequenceEqual(other.Descending.Count == 0 ? new bool[other.Columns.Count] : other.Descending);
}

/// <summary>A foreign key.</summary>
/// <param name="Name">Constraint name.</param>
/// <param name="Columns">Referencing columns.</param>
/// <param name="ReferencedTable">Referenced table.</param>
/// <param name="ReferencedColumns">Referenced columns.</param>
public sealed record ForeignKeySchema(string Name, IReadOnlyList<string> Columns, string ReferencedTable, IReadOnlyList<string> ReferencedColumns)
{
    /// <summary>Referenced table's schema.</summary>
    public string? ReferencedSchema { get; init; }

    /// <summary>Action on delete.</summary>
    public ForeignKeyAction OnDelete { get; init; } = ForeignKeyAction.Restrict;

    /// <summary>Action on update.</summary>
    public ForeignKeyAction OnUpdate { get; init; } = ForeignKeyAction.Restrict;

    /// <summary>Structural equality ignoring the name.</summary>
    public bool SameShape(ForeignKeySchema other) =>
        Columns.SequenceEqual(other.Columns, StringComparer.OrdinalIgnoreCase) &&
        string.Equals(ReferencedTable, other.ReferencedTable, StringComparison.OrdinalIgnoreCase) &&
        ReferencedColumns.SequenceEqual(other.ReferencedColumns, StringComparer.OrdinalIgnoreCase) &&
        Normalize(OnDelete) == Normalize(other.OnDelete) && Normalize(OnUpdate) == Normalize(other.OnUpdate);

    // RESTRICT and NO ACTION behave the same for CL.Data's purposes; databases report either.
    private static ForeignKeyAction Normalize(ForeignKeyAction action) => action == ForeignKeyAction.NoAction ? ForeignKeyAction.Restrict : action;
}

/// <summary>A table: columns, primary key, indexes, foreign keys and provider options.</summary>
/// <param name="Name">Table name.</param>
/// <param name="Schema">Schema, where the provider has schemas.</param>
/// <param name="Columns">Columns in order.</param>
/// <param name="PrimaryKey">Primary-key columns in order.</param>
public sealed record TableSchema(string Name, string? Schema, IReadOnlyList<ColumnSchema> Columns, IReadOnlyList<string> PrimaryKey)
{
    /// <summary>Indexes (not including the primary key).</summary>
    public IReadOnlyList<IndexSchema> Indexes { get; init; } = [];

    /// <summary>Foreign keys.</summary>
    public IReadOnlyList<ForeignKeySchema> ForeignKeys { get; init; } = [];

    /// <summary>Provider table options (MySQL engine/charset/collation, ...).</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();

    /// <summary>Table comment.</summary>
    public string? Comment { get; init; }

    /// <summary>The column named <paramref name="name"/> (case-insensitive), or null.</summary>
    public ColumnSchema? Column(string name) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A schema change. The hierarchy is closed: DDL generators handle every case.</summary>
public closed record SchemaChange(string Table, string? Schema)
{
    /// <summary>Short human-readable description (drift reports, logs).</summary>
    public string Describe() => this switch
    {
        CreateTableChange c => $"create table {c.Table}",
        AddColumnChange a => $"add column {a.Table}.{a.Column.Name} {a.Column.StoreType}",
        AlterColumnChange a => $"alter column {a.Table}.{a.To.Name}: {a.From.StoreType}{(a.From.Nullable ? " null" : " not null")} -> {a.To.StoreType}{(a.To.Nullable ? " null" : " not null")}",
        RenameColumnChange r => $"rename column {r.Table}.{r.From} -> {r.To}",
        DropColumnChange d => $"drop column {d.Table}.{d.Column}",
        CreateIndexChange i => $"create {(i.Index.Unique ? "unique " : "")}index {i.Index.Name} on {i.Table}",
        DropIndexChange d => $"drop index {d.Name} on {d.Table}",
        AddForeignKeyChange f => $"add foreign key {f.ForeignKey.Name} on {f.Table}",
        DropForeignKeyChange d => $"drop foreign key {d.Name} on {d.Table}",
    };
}

/// <summary>Create a table.</summary>
public sealed record CreateTableChange(TableSchema Definition) : SchemaChange(Definition.Name, Definition.Schema);

/// <summary>Add a column.</summary>
public sealed record AddColumnChange(string Table, string? Schema, ColumnSchema Column) : SchemaChange(Table, Schema);

/// <summary>Change a column's type or nullability. <see cref="Desired"/> is the full target table (SQLite rebuilds it).</summary>
public sealed record AlterColumnChange(string Table, string? Schema, ColumnSchema From, ColumnSchema To, TableSchema Desired) : SchemaChange(Table, Schema);

/// <summary>Rename a column.</summary>
public sealed record RenameColumnChange(string Table, string? Schema, string From, string To) : SchemaChange(Table, Schema);

/// <summary>Drop a column. <see cref="Desired"/> is the full target table (for providers that rebuild).</summary>
public sealed record DropColumnChange(string Table, string? Schema, string Column, TableSchema Desired) : SchemaChange(Table, Schema);

/// <summary>Create an index.</summary>
public sealed record CreateIndexChange(string Table, string? Schema, IndexSchema Index) : SchemaChange(Table, Schema);

/// <summary>Drop an index.</summary>
public sealed record DropIndexChange(string Table, string? Schema, string Name) : SchemaChange(Table, Schema);

/// <summary>Add a foreign key. <see cref="Desired"/> is the full target table (SQLite rebuilds it).</summary>
public sealed record AddForeignKeyChange(string Table, string? Schema, ForeignKeySchema ForeignKey, TableSchema Desired) : SchemaChange(Table, Schema);

/// <summary>Drop a foreign key. <see cref="Desired"/> is the full target table (SQLite rebuilds it).</summary>
public sealed record DropForeignKeyChange(string Table, string? Schema, string Name, TableSchema Desired) : SchemaChange(Table, Schema);
