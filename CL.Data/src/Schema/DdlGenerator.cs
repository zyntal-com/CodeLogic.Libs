using System.Text;
using CL.Data.Sql;

namespace CL.Data.Schema;

/// <summary>
/// Writes DDL for schema changes. This base class writes standard SQL; each provider derives from it and overrides
/// what its database does differently (identity columns, renames, index options, table rebuilds).
/// </summary>
public class DdlGenerator(SqlDialect dialect)
{
    /// <summary>The dialect used for quoting.</summary>
    protected SqlDialect Dialect { get; } = dialect;

    /// <summary>The statements for <paramref name="change"/>, in order.</summary>
    public virtual IReadOnlyList<string> Generate(SchemaChange change) => change switch
    {
        CreateTableChange c => CreateTable(c.Definition),
        AddColumnChange a => [AddColumn(a)],
        AlterColumnChange a => [.. BackfillNulls(a), .. AlterColumn(a)],
        RenameColumnChange r => [RenameColumn(r)],
        DropColumnChange d => DropColumn(d),
        CreateIndexChange i => [CreateIndex(i.Table, i.Schema, i.Index)],
        DropIndexChange d => [DropIndex(d)],
        AddForeignKeyChange f => AddForeignKey(f),
        DropForeignKeyChange d => DropForeignKey(d),
    };

    /// <summary>
    /// The statements for one table's <paramref name="changes"/> (in plan order). Default: <see cref="Generate"/> for each
    /// change. Providers whose ALTER TABLE takes several clauses override it so a table is rebuilt once, not once per column.
    /// </summary>
    public virtual IReadOnlyList<string> GenerateAll(IReadOnlyList<SchemaChange> changes) => changes.SelectMany(Generate).ToArray();

    /// <summary>True when DDL can run in a transaction (all but MySQL); a failed table plan then rolls back completely.</summary>
    public virtual bool TransactionalDdl => true;

    /// <summary>Statements run on the session before a table's changes, outside the transaction.</summary>
    public virtual IReadOnlyList<string> SessionSetup => [];

    /// <summary>Statements run on the session after a table's changes (also after a failure).</summary>
    public virtual IReadOnlyList<string> SessionTeardown => [];

    /// <summary>A query run before commit that must return no rows (SQLite <c>PRAGMA foreign_key_check</c>), or null.</summary>
    public virtual string? IntegrityCheckSql => null;

    /// <summary>Statements run (in the restore transaction) before rows with explicit identity values are inserted.</summary>
    public virtual IReadOnlyList<string> IdentityInsertSetup(TableSchema table) => [];

    /// <summary>Statements run after rows with explicit identity values were inserted (reset sequences).</summary>
    public virtual IReadOnlyList<string> IdentityInsertTeardown(TableSchema table) => [];

    /// <summary>A quoted, possibly schema-qualified table name.</summary>
    protected string Table(string table, string? schema) => Dialect.QuoteTable(table, schema);

    /// <summary>A quoted identifier.</summary>
    protected string Q(string identifier) => Dialect.QuoteIdentifier(identifier);

    // ── Tables ──────────────────────────────────────────────────────────────

    /// <summary>CREATE TABLE, then its indexes and foreign keys.</summary>
    protected virtual IReadOnlyList<string> CreateTable(TableSchema table)
    {
        var sb = new StringBuilder(CreateTableKeyword(table)).Append(Table(table.Name, table.Schema)).Append(" (");
        var parts = table.Columns.Select(c => ColumnDefinition(c, table)).ToList();
        if (table.PrimaryKey.Count > 0 && !InlinePrimaryKey(table))
            parts.Add("PRIMARY KEY (" + string.Join(", ", table.PrimaryKey.Select(Q)) + ")");
        parts.AddRange(InlineForeignKeys(table));
        sb.Append(string.Join(", ", parts)).Append(')').Append(TableSuffix(table));

        List<string> statements = [sb.ToString()];
        statements.AddRange(table.Indexes.Select(i => CreateIndex(table.Name, table.Schema, i)));
        statements.AddRange(PostCreateTable(table));
        return statements;
    }

    /// <summary>True when the primary key is declared on the column itself (SQLite identities).</summary>
    protected virtual bool InlinePrimaryKey(TableSchema table) => false;

    /// <summary>Foreign keys declared inside CREATE TABLE (SQLite); others add them afterwards.</summary>
    protected virtual IEnumerable<string> InlineForeignKeys(TableSchema table) => [];

    /// <summary>Statements after CREATE TABLE and its indexes. Default: one ALTER TABLE ADD CONSTRAINT per foreign key.</summary>
    protected virtual IEnumerable<string> PostCreateTable(TableSchema table) =>
        table.ForeignKeys.Select(f => AddForeignKeySql(table.Name, table.Schema, f));

    /// <summary>The words before the table name: <c>CREATE TABLE </c> (PostgreSQL: <c>CREATE UNLOGGED TABLE </c>), with the trailing space.</summary>
    protected virtual string CreateTableKeyword(TableSchema table) => "CREATE TABLE ";

    /// <summary>Text after the closing parenthesis of CREATE TABLE (MySQL engine and charset). Default: nothing.</summary>
    protected virtual string TableSuffix(TableSchema table) => "";

    /// <summary>A column definition: name, type, identity, nullability, default, collation.</summary>
    protected virtual string ColumnDefinition(ColumnSchema column, TableSchema? table)
    {
        var sb = new StringBuilder(Q(column.Name)).Append(' ').Append(ColumnType(column));
        if (column.Identity) sb.Append(IdentityClause(column));
        if (column.Collation is { } collation) sb.Append(" COLLATE ").Append(collation);
        sb.Append(column.Nullable ? " NULL" : " NOT NULL");
        if (column.DefaultSql is { } defaultSql) sb.Append(" DEFAULT ").Append(defaultSql);
        return sb.ToString();
    }

    /// <summary>The column's type in DDL. Default: the store type.</summary>
    protected virtual string ColumnType(ColumnSchema column) => column.StoreType;

    /// <summary>The identity clause after the type. Default: <c>GENERATED BY DEFAULT AS IDENTITY</c>.</summary>
    protected virtual string IdentityClause(ColumnSchema column) => " GENERATED BY DEFAULT AS IDENTITY";

    // ── Columns ─────────────────────────────────────────────────────────────

    /// <summary>ALTER TABLE ADD COLUMN. A NOT NULL column without a default gets one, so existing rows stay valid.</summary>
    protected virtual string AddColumn(AddColumnChange change) =>
        $"ALTER TABLE {Table(change.Table, change.Schema)} {AddColumnKeyword} {ColumnDefinition(ColumnToAdd(change.Column), null)}";

    /// <summary><paramref name="column"/> as added to a table that may have rows: NOT NULL without a default gets an implicit one.</summary>
    protected ColumnSchema ColumnToAdd(ColumnSchema column) =>
        !column.Nullable && column.DefaultSql is null && !column.Identity ? column with { DefaultSql = ImplicitDefault(column) } : column;

    /// <summary><c>ADD COLUMN</c> (<c>ADD</c> on SQL Server).</summary>
    protected virtual string AddColumnKeyword => "ADD COLUMN";

    /// <summary>A default literal for adding a NOT NULL column to a table that has rows.</summary>
    protected virtual string ImplicitDefault(ColumnSchema column)
    {
        var type = column.StoreType;
        if (type.StartsWith("bool", StringComparison.Ordinal)) return "FALSE";
        if (type is "bit" || type.StartsWith("tinyint(1)", StringComparison.Ordinal)) return "0";
        if (type.Contains("int", StringComparison.Ordinal) || type.StartsWith("decimal", StringComparison.Ordinal)
            || type.StartsWith("numeric", StringComparison.Ordinal) || type is "real" or "float" or "double" or "double precision")
            return "0";
        if (type.Contains("uuid", StringComparison.Ordinal) || type.Contains("uniqueidentifier", StringComparison.Ordinal))
            return "'00000000-0000-0000-0000-000000000000'";
        if (type.StartsWith("date", StringComparison.Ordinal) && !type.StartsWith("datetime", StringComparison.Ordinal)) return "'1970-01-01'";
        if (type.Contains("time", StringComparison.Ordinal)) return "'1970-01-01 00:00:00'";
        if (type.Contains("binary", StringComparison.Ordinal) || type.Contains("blob", StringComparison.Ordinal) || type == "bytea") return "''";
        return "''";
    }

    /// <summary>
    /// Before a nullable column becomes NOT NULL: an UPDATE that replaces existing NULLs with the column's default (or an
    /// implicit one), since every database refuses the change while NULLs remain (MySQL strict mode included).
    /// </summary>
    protected virtual IReadOnlyList<string> BackfillNulls(AlterColumnChange change)
    {
        if (!change.From.Nullable || change.To.Nullable || change.To.Identity) return [];
        var value = change.To.DefaultSql ?? ImplicitDefault(change.To);
        return [$"UPDATE {Table(change.Table, change.Schema)} SET {Q(change.To.Name)} = {value} WHERE {Q(change.To.Name)} IS NULL"];
    }

    /// <summary>Changes a column's type and nullability.</summary>
    protected virtual IReadOnlyList<string> AlterColumn(AlterColumnChange change)
    {
        var table = Table(change.Table, change.Schema);
        List<string> statements = [];
        if (!string.Equals(change.From.StoreType, change.To.StoreType, StringComparison.OrdinalIgnoreCase))
            statements.Add($"ALTER TABLE {table} ALTER COLUMN {Q(change.To.Name)} TYPE {ColumnType(change.To)}");
        if (change.From.Nullable != change.To.Nullable)
            statements.Add($"ALTER TABLE {table} ALTER COLUMN {Q(change.To.Name)} {(change.To.Nullable ? "DROP NOT NULL" : "SET NOT NULL")}");
        return statements;
    }

    /// <summary>ALTER TABLE RENAME COLUMN.</summary>
    protected virtual string RenameColumn(RenameColumnChange change) =>
        $"ALTER TABLE {Table(change.Table, change.Schema)} RENAME COLUMN {Q(change.From)} TO {Q(change.To)}";

    /// <summary>ALTER TABLE DROP COLUMN.</summary>
    protected virtual IReadOnlyList<string> DropColumn(DropColumnChange change) =>
        [$"ALTER TABLE {Table(change.Table, change.Schema)} DROP COLUMN {Q(change.Column)}"];

    // ── Indexes ─────────────────────────────────────────────────────────────

    /// <summary>CREATE [UNIQUE] INDEX, with covering columns where supported.</summary>
    protected virtual string CreateIndex(string table, string? schema, IndexSchema index)
    {
        var sb = new StringBuilder("CREATE ").Append(index.Unique ? "UNIQUE " : "").Append("INDEX ").Append(IfNotExists).Append(Q(index.Name))
            .Append(" ON ").Append(Table(table, schema)).Append(" (").Append(IndexKey(index)).Append(')');
        if (SupportsInclude && index.Include.Count > 0)
            sb.Append(" INCLUDE (").Append(string.Join(", ", index.Include.Select(Q))).Append(')');
        return sb.ToString();
    }

    /// <summary>
    /// The key column list of an index (with DESC flags). Without INCLUDE support the covering columns are appended to the
    /// key (still covering, slightly larger).
    /// </summary>
    protected string IndexKey(IndexSchema index)
    {
        var columns = index.Columns.Select((c, i) => Q(c) + (index.Descending.Count > i && index.Descending[i] ? " DESC" : "")).ToList();
        if (!SupportsInclude)
            columns.AddRange(index.Include.Where(c => !index.Columns.Contains(c, StringComparer.OrdinalIgnoreCase)).Select(Q));
        return string.Join(", ", columns);
    }

    /// <summary><c>IF NOT EXISTS </c> where CREATE INDEX supports it and a table rebuild may already have created the index.</summary>
    protected virtual string IfNotExists => "";

    /// <summary>True when indexes support <c>INCLUDE</c>.</summary>
    protected virtual bool SupportsInclude => true;

    /// <summary>DROP INDEX.</summary>
    protected virtual string DropIndex(DropIndexChange change) =>
        change.Schema is null ? $"DROP INDEX {Q(change.Name)}" : $"DROP INDEX {Q(change.Schema)}.{Q(change.Name)}";

    // ── Foreign keys ────────────────────────────────────────────────────────

    /// <summary>ALTER TABLE ADD CONSTRAINT ... FOREIGN KEY.</summary>
    protected virtual IReadOnlyList<string> AddForeignKey(AddForeignKeyChange change) =>
        [AddForeignKeySql(change.Table, change.Schema, change.ForeignKey)];

    /// <summary>The ADD CONSTRAINT statement for <paramref name="fk"/>.</summary>
    protected string AddForeignKeySql(string table, string? schema, ForeignKeySchema fk) =>
        $"ALTER TABLE {Table(table, schema)} ADD {ForeignKeyClause(fk)}";

    /// <summary><c>CONSTRAINT name FOREIGN KEY (...) REFERENCES t (...) ON DELETE ... ON UPDATE ...</c>.</summary>
    protected virtual string ForeignKeyClause(ForeignKeySchema fk) =>
        $"CONSTRAINT {Q(fk.Name)} FOREIGN KEY ({string.Join(", ", fk.Columns.Select(Q))}) " +
        $"REFERENCES {Table(fk.ReferencedTable, fk.ReferencedSchema)} ({string.Join(", ", fk.ReferencedColumns.Select(Q))}) " +
        $"ON DELETE {Action(fk.OnDelete)} ON UPDATE {Action(fk.OnUpdate)}";

    /// <summary>A referential action in SQL.</summary>
    protected virtual string Action(ForeignKeyAction action) => action switch
    {
        ForeignKeyAction.Cascade => "CASCADE",
        ForeignKeyAction.SetNull => "SET NULL",
        ForeignKeyAction.SetDefault => "SET DEFAULT",
        ForeignKeyAction.NoAction => "NO ACTION",
        _ => "RESTRICT",
    };

    /// <summary>ALTER TABLE DROP CONSTRAINT.</summary>
    protected virtual IReadOnlyList<string> DropForeignKey(DropForeignKeyChange change) =>
        [$"ALTER TABLE {Table(change.Table, change.Schema)} DROP CONSTRAINT {Q(change.Name)}"];
}
