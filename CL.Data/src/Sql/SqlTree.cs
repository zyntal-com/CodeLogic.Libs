namespace CL.Data.Sql;

// The SQL tree. LINQ translation produces these nodes; SqlRenderer turns them into text through a SqlDialect.
// Both hierarchies are closed, so every switch over them is checked for exhaustiveness (CS8509 is an error):
// adding a node breaks the build until the renderer and every dialect hook handle it.

/// <summary>Binary operators.</summary>
public enum SqlBinaryOperator
{
    /// <summary><c>+</c></summary>
    Add,
    /// <summary><c>-</c></summary>
    Subtract,
    /// <summary><c>*</c></summary>
    Multiply,
    /// <summary><c>/</c></summary>
    Divide,
    /// <summary><c>%</c></summary>
    Modulo,
    /// <summary>String concatenation (dialect renders <c>||</c>, <c>+</c> or <c>CONCAT</c>).</summary>
    Concat,
    /// <summary><c>=</c></summary>
    Equal,
    /// <summary><c>&lt;&gt;</c></summary>
    NotEqual,
    /// <summary><c>&lt;</c></summary>
    LessThan,
    /// <summary><c>&lt;=</c></summary>
    LessThanOrEqual,
    /// <summary><c>&gt;</c></summary>
    GreaterThan,
    /// <summary><c>&gt;=</c></summary>
    GreaterThanOrEqual,
    /// <summary><c>AND</c></summary>
    And,
    /// <summary><c>OR</c></summary>
    Or,
}

/// <summary>Unary operators.</summary>
public enum SqlUnaryOperator
{
    /// <summary><c>NOT</c></summary>
    Not,
    /// <summary>Arithmetic negation.</summary>
    Negate,
}

/// <summary>Aggregate functions.</summary>
public enum SqlAggregate
{
    /// <summary><c>COUNT</c> (<c>COUNT(*)</c> without an operand).</summary>
    Count,
    /// <summary><c>SUM</c></summary>
    Sum,
    /// <summary><c>AVG</c></summary>
    Average,
    /// <summary><c>MIN</c></summary>
    Min,
    /// <summary><c>MAX</c></summary>
    Max,
}

/// <summary>Provider-neutral scalar functions; each dialect renders its own SQL.</summary>
public enum SqlFunction
{
    /// <summary>Lower case.</summary>
    Lower,
    /// <summary>Upper case.</summary>
    Upper,
    /// <summary>Trim both ends.</summary>
    Trim,
    /// <summary>Character length.</summary>
    Length,
    /// <summary>Substring (string, 1-based start, length).</summary>
    Substring,
    /// <summary>Replace (string, find, replacement).</summary>
    Replace,
    /// <summary>First non-null argument.</summary>
    Coalesce,
    /// <summary>Absolute value.</summary>
    Abs,
    /// <summary>Round (value, digits).</summary>
    Round,
    /// <summary>Round down.</summary>
    Floor,
    /// <summary>Round up.</summary>
    Ceiling,
    /// <summary>Smallest argument.</summary>
    Least,
    /// <summary>Largest argument.</summary>
    Greatest,
    /// <summary>Year of a date.</summary>
    Year,
    /// <summary>Month (1-12).</summary>
    Month,
    /// <summary>Day of month.</summary>
    Day,
    /// <summary>Hour (0-23).</summary>
    Hour,
    /// <summary>Minute.</summary>
    Minute,
    /// <summary>Second.</summary>
    Second,
    /// <summary>Day of week, 0 = Sunday (matches <see cref="System.DayOfWeek"/>).</summary>
    DayOfWeek,
    /// <summary>Date part of a date/time.</summary>
    Date,
    /// <summary>Start of the UTC time bucket of (value, bucket seconds).</summary>
    BucketUtc,
    /// <summary>Current UTC time on the server.</summary>
    UtcNow,
    /// <summary>JSON value at a path (document, path).</summary>
    JsonValue,
    /// <summary>True when a JSON path exists (document, path).</summary>
    JsonExists,
    /// <summary>True when a JSON document contains a value (document, value).</summary>
    JsonContains,
    /// <summary>Full-text match (column, search terms).</summary>
    FullTextMatch,
}

/// <summary>Join kinds.</summary>
public enum SqlJoinKind
{
    /// <summary><c>INNER JOIN</c></summary>
    Inner,
    /// <summary><c>LEFT JOIN</c></summary>
    Left,
    /// <summary><c>RIGHT JOIN</c></summary>
    Right,
    /// <summary><c>CROSS JOIN</c> (no condition).</summary>
    Cross,
}

/// <summary>Row lock strength.</summary>
public enum LockMode
{
    /// <summary>Exclusive lock (<c>FOR UPDATE</c>).</summary>
    Update,
    /// <summary>Shared lock (<c>FOR SHARE</c>).</summary>
    Share,
}

/// <summary>What a locking read does when rows are already locked.</summary>
public enum LockWait
{
    /// <summary>Wait for the lock.</summary>
    Wait,
    /// <summary>Fail at once with <c>data.lock_not_acquired</c>.</summary>
    NoWait,
    /// <summary>Skip locked rows (job queues).</summary>
    SkipLocked,
}

/// <summary>A SQL expression.</summary>
public closed record SqlExpr;

/// <summary>A column reference, optionally qualified by a table alias.</summary>
public sealed record ColumnExpr(string Name, string? Table = null) : SqlExpr;

/// <summary>A bound parameter.</summary>
public sealed record ParameterExpr(SqlParameterValue Value) : SqlExpr;

/// <summary>A literal written into the SQL: only numbers and booleans from code constants (never captured values or text).</summary>
public sealed record LiteralExpr(object Value) : SqlExpr;

/// <summary><c>NULL</c>.</summary>
public sealed record NullExpr : SqlExpr
{
    /// <summary>The single instance.</summary>
    public static NullExpr Instance { get; } = new();
}

/// <summary>A binary operation.</summary>
public sealed record BinaryExpr(SqlBinaryOperator Operator, SqlExpr Left, SqlExpr Right) : SqlExpr;

/// <summary>A unary operation.</summary>
public sealed record UnaryExpr(SqlUnaryOperator Operator, SqlExpr Operand) : SqlExpr;

/// <summary><c>IS NULL</c> / <c>IS NOT NULL</c>.</summary>
public sealed record IsNullExpr(SqlExpr Operand, bool Negated = false) : SqlExpr;

/// <summary><c>IN (...)</c>. An empty list renders as always false (or always true when negated).</summary>
public sealed record InListExpr(SqlExpr Operand, IReadOnlyList<SqlExpr> Values, bool Negated = false) : SqlExpr;

/// <summary><c>IN (SELECT ...)</c>.</summary>
public sealed record InSubqueryExpr(SqlExpr Operand, SelectStatement Subquery, bool Negated = false) : SqlExpr;

/// <summary><c>EXISTS (SELECT ...)</c>.</summary>
public sealed record ExistsExpr(SelectStatement Subquery, bool Negated = false) : SqlExpr;

/// <summary><c>LIKE</c> with the dialect's escape character; <paramref name="Pattern"/> is already escaped.</summary>
public sealed record LikeExpr(SqlExpr Operand, SqlExpr Pattern, bool Negated = false) : SqlExpr;

/// <summary>A neutral scalar function.</summary>
public sealed record FunctionExpr(SqlFunction Function, IReadOnlyList<SqlExpr> Arguments) : SqlExpr;

/// <summary>An aggregate; <c>COUNT(*)</c> when <paramref name="Operand"/> is null.</summary>
public sealed record AggregateExpr(SqlAggregate Aggregate, SqlExpr? Operand, bool Distinct = false) : SqlExpr;

/// <summary>One <c>WHEN ... THEN ...</c> arm.</summary>
public sealed record CaseWhen(SqlExpr When, SqlExpr Then);

/// <summary><c>CASE WHEN ... END</c>.</summary>
public sealed record CaseExpr(IReadOnlyList<CaseWhen> Whens, SqlExpr? Else) : SqlExpr;

/// <summary><c>*</c> or <c>alias.*</c>.</summary>
public sealed record StarExpr(string? Table = null) : SqlExpr;

/// <summary>The incoming value of a column in an upsert's update arm (<c>EXCLUDED.col</c>, <c>VALUES(col)</c>, ...).</summary>
public sealed record ExcludedExpr(string Column) : SqlExpr;

/// <summary>A scalar subquery.</summary>
public sealed record SubqueryExpr(SelectStatement Query) : SqlExpr;

/// <summary>A table in a FROM, JOIN or DML target.</summary>
public sealed record TableRef(string Name, string? Schema = null, string? Alias = null);

/// <summary>A join.</summary>
public sealed record SqlJoin(SqlJoinKind Kind, TableRef Table, SqlExpr? On);

/// <summary>An ORDER BY item.</summary>
public sealed record OrderItem(SqlExpr Expression, bool Descending = false);

/// <summary>A projected expression with an optional alias.</summary>
public sealed record ProjectionItem(SqlExpr Expression, string? Alias = null);

/// <summary>A locking read clause.</summary>
public sealed record LockClause(LockMode Mode, LockWait Wait = LockWait.Wait);

/// <summary>A column assignment in UPDATE or an upsert's update arm.</summary>
public sealed record SetItem(string Column, SqlExpr Value);

/// <summary>A SQL statement.</summary>
public closed record SqlStatement;

/// <summary><c>SELECT</c>.</summary>
public sealed record SelectStatement(TableRef From) : SqlStatement
{
    /// <summary>Projection; empty means <c>*</c>.</summary>
    public IReadOnlyList<ProjectionItem> Projection { get; init; } = [];
    /// <summary><c>SELECT DISTINCT</c>.</summary>
    public bool Distinct { get; init; }
    /// <summary>Joins.</summary>
    public IReadOnlyList<SqlJoin> Joins { get; init; } = [];
    /// <summary><c>WHERE</c>.</summary>
    public SqlExpr? Where { get; init; }
    /// <summary><c>GROUP BY</c>.</summary>
    public IReadOnlyList<SqlExpr> GroupBy { get; init; } = [];
    /// <summary><c>HAVING</c>.</summary>
    public SqlExpr? Having { get; init; }
    /// <summary><c>ORDER BY</c>.</summary>
    public IReadOnlyList<OrderItem> OrderBy { get; init; } = [];
    /// <summary>Row limit (a parameter, so the SQL text does not change with the value).</summary>
    public SqlExpr? Limit { get; init; }
    /// <summary>Rows to skip.</summary>
    public SqlExpr? Offset { get; init; }
    /// <summary>Locking read.</summary>
    public LockClause? Lock { get; init; }
}

/// <summary><c>INSERT</c> of one or more rows.</summary>
public sealed record InsertStatement(TableRef Table, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<SqlExpr>> Rows) : SqlStatement
{
    /// <summary>Columns to return from the inserted rows (identity, server defaults).</summary>
    public IReadOnlyList<string> Returning { get; init; } = [];

    /// <summary>True when the rows bring their own values for the table's identity column (SQL Server needs IDENTITY_INSERT).</summary>
    public bool ExplicitIdentity { get; init; }
}

/// <summary><c>UPDATE</c>.</summary>
public sealed record UpdateStatement(TableRef Table, IReadOnlyList<SetItem> Set) : SqlStatement
{
    /// <summary><c>WHERE</c>.</summary>
    public SqlExpr? Where { get; init; }
    /// <summary>Columns to return from the updated rows.</summary>
    public IReadOnlyList<string> Returning { get; init; } = [];
}

/// <summary><c>DELETE</c>.</summary>
public sealed record DeleteStatement(TableRef Table) : SqlStatement
{
    /// <summary><c>WHERE</c>.</summary>
    public SqlExpr? Where { get; init; }
    /// <summary>Most rows to delete (batched deletes); rendered by the dialect.</summary>
    public SqlExpr? Limit { get; init; }
    /// <summary>Which rows a limited delete takes first (with <see cref="Limit"/> only).</summary>
    public IReadOnlyList<OrderItem> OrderBy { get; init; } = [];
    /// <summary>Key columns used when the dialect emulates a limited delete through a key subquery.</summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = [];
}

/// <summary>
/// Insert, or update/ignore on a key conflict. The dialect decides how to express it (native upsert or a
/// transactional emulation).
/// </summary>
public sealed record UpsertStatement(InsertStatement Insert, IReadOnlyList<string> ConflictColumns) : SqlStatement
{
    /// <summary>Assignments on conflict; empty with <see cref="IgnoreOnConflict"/> means "do nothing".</summary>
    public IReadOnlyList<SetItem> UpdateSet { get; init; } = [];
    /// <summary>Insert only when no row with the key exists (insert-ignore).</summary>
    public bool IgnoreOnConflict { get; init; }

    /// <summary>
    /// When set, the conflict update only applies to an existing row whose value in this column equals the new row's
    /// (the tenant column): an upsert can never change another tenant's row.
    /// </summary>
    public string? GuardColumn { get; init; }
}
