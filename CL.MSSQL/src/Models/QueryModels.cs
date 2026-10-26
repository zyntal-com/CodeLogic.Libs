namespace CL.MSSQL.Models;

/// <summary>
/// Represents a page of results from a paged query.
/// </summary>
/// <typeparam name="T">The entity type.</typeparam>
public sealed class PagedResult<T>
{
    /// <summary>The items on the current page.</summary>
    public List<T> Items { get; init; } = [];

    /// <summary>The 1-based page number.</summary>
    public int PageNumber { get; init; }

    /// <summary>The maximum number of items per page.</summary>
    public int PageSize { get; init; }

    /// <summary>Total number of items across all pages.</summary>
    public long TotalItems { get; init; }

    /// <summary>Total number of pages.</summary>
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalItems / PageSize) : 0;

    /// <summary>True when there is a page before this one.</summary>
    public bool HasPreviousPage => PageNumber > 1;

    /// <summary>True when there is a page after this one.</summary>
    public bool HasNextPage => PageNumber < TotalPages;
}

/// <summary>
/// Represents one forward-only page from a cursor-paged query.
/// </summary>
/// <typeparam name="T">The entity type.</typeparam>
public sealed class CursorPagedResult<T>
{
    /// <summary>The items returned for this page.</summary>
    public List<T> Items { get; init; } = [];

    /// <summary>The requested maximum number of items for the page.</summary>
    public int PageSize { get; init; }

    /// <summary>
    /// The opaque cursor to pass to <c>After</c> for the next page, or <see langword="null"/>
    /// when this is the final page.
    /// </summary>
    public string? NextCursor { get; init; }

    /// <summary>True when another page is available.</summary>
    public bool HasNextPage => NextCursor is not null;
}

/// <summary>SQL JOIN types.</summary>
public enum JoinType
{
    Inner,
    Left,
    Right,
    Cross
}

/// <summary>
/// What a locking read (<c>ForUpdate</c> / <c>ForShare</c>) does when a row it wants is
/// already locked by another transaction.
/// </summary>
public enum LockWait
{
    /// <summary>Block until the other transaction releases the row (or the lock timeout expires).</summary>
    Wait,
    /// <summary>Fail immediately with a lock error (<c>NOWAIT</c>, error 1222) instead of waiting.</summary>
    NoWait,
    /// <summary>Leave locked rows out of the result (<c>READPAST</c>). The usual choice for job queues.</summary>
    SkipLocked
}

/// <summary>
/// The result of a table synchronization operation.
/// </summary>
public sealed class SyncResult
{
    /// <summary>Whether the sync completed without errors.</summary>
    public bool Success { get; init; }

    /// <summary>The name of the table that was synced.</summary>
    public string TableName { get; init; } = string.Empty;

    /// <summary>DDL operations performed (e.g., "CREATE TABLE", "ADD COLUMN x").</summary>
    public List<string> Operations { get; init; } = [];

    /// <summary>Any non-fatal errors encountered during the sync.</summary>
    public List<string> Errors { get; init; } = [];

    /// <summary>How long the sync took.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// True when the table was skipped via the <c>__schema_state</c> CRC fast-path
    /// (the model is unchanged), so no <c>sys catalog</c> diffing or DDL ran.
    /// </summary>
    public bool Skipped { get; init; }

    /// <summary>The model's computed schema CRC, when available.</summary>
    public string? SchemaCrc { get; init; }

    /// <summary>
    /// True when an additive (Production) sync left a destructive change deferred
    /// (the table was flagged <c>DriftPending</c> for a later Migration pass).
    /// </summary>
    public bool DriftPending { get; init; }
}
