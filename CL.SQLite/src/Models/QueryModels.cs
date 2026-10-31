namespace CL.SQLite.Models;

public record SQLiteQuery
{
    public required string QueryString { get; init; }
    public Dictionary<string, object?> Parameters { get; init; } = [];
}

public record TableSyncResult
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
    public Exception? Exception { get; init; }

    public static TableSyncResult Succeeded(string msg) => new() { Success = true, Message = msg };
    public static TableSyncResult Failed(string msg, Exception? ex = null) => new() { Success = false, Message = msg, Exception = ex };
}

public enum TransactionIsolation
{
    Deferred,
    Immediate,
    Exclusive
}

/// <summary>
/// What a locking read (<c>ForUpdate</c> / <c>ForShare</c>) does when a row it wants is
/// already locked. Kept for API parity with the server libraries: SQLite has no row locks,
/// and CL.SQLite's <c>ForUpdate</c> / <c>ForShare</c> throw <see cref="NotSupportedException"/>.
/// </summary>
public enum LockWait
{
    /// <summary>Block until the other transaction releases the row.</summary>
    Wait,
    /// <summary>Fail immediately instead of waiting.</summary>
    NoWait,
    /// <summary>Leave locked rows out of the result.</summary>
    SkipLocked
}

public class WhereCondition
{
    public required string Column { get; init; }
    public required string Operator { get; init; }
    public object? Value { get; init; }
    public string LogicalOperator { get; init; } = "AND";
}

public class OrderByClause
{
    public required string Column { get; init; }
    public required SortOrder Order { get; init; }
}

public enum SortOrder
{
    Asc,
    Desc
}

public class AggregateFunction
{
    public required AggregateType Type { get; init; }
    public required string Column { get; init; }
    public required string Alias { get; init; }
}

public enum AggregateType
{
    Sum,
    Avg,
    Min,
    Max,
    Count
}

public class PagedResult<T>
{
    public required List<T> Items { get; init; }
    public int PageNumber { get; init; }
    public int PageSize { get; init; }
    public long TotalItems { get; init; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalItems / PageSize) : 0;
}
