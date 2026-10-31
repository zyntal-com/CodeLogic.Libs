using CL.Data.Mapping;
using CL.Data.Sql;

namespace CL.Data.Writes;

/// <summary>What kind of write a command performs (telemetry and cache invalidation).</summary>
public enum WriteKind
{
    /// <summary>INSERT.</summary>
    Insert,
    /// <summary>UPDATE.</summary>
    Update,
    /// <summary>DELETE (or a soft delete).</summary>
    Delete,
    /// <summary>Insert or update.</summary>
    Upsert,
}

/// <summary>How the executor checks the affected-row count.</summary>
public enum RowCheck
{
    /// <summary>Any count is fine.</summary>
    None,
    /// <summary>
    /// Exactly one row must be affected; zero means the row is gone or its concurrency token changed
    /// (<c>data.concurrency</c> when the entity has a token, <c>data.not_found</c> otherwise).
    /// </summary>
    ExactlyOne,
}

/// <summary>One planned write statement.</summary>
/// <param name="Statement">The statement to render and run.</param>
/// <param name="Kind">Write kind.</param>
/// <param name="Table">Main table (database name).</param>
/// <param name="Rows">Entities covered by this statement, in order (identity values are written back to them).</param>
/// <param name="Tags">Cache tags the write invalidates.</param>
public sealed record WriteCommand(SqlStatement Statement, WriteKind Kind, string Table, int Rows, IReadOnlyList<string> Tags)
{
    /// <summary>Affected-row check.</summary>
    public RowCheck Check { get; init; } = RowCheck.None;

    /// <summary>True when the statement returns generated values (RETURNING/OUTPUT) for <see cref="ReturnedColumns"/>.</summary>
    public bool Returns => ReturnedColumns.Count > 0;

    /// <summary>Columns whose generated values are read back, in RETURNING order.</summary>
    public IReadOnlyList<ColumnMap> ReturnedColumns { get; init; } = [];

    /// <summary>
    /// Identity column to fill from the provider's last-insert-id mechanism when RETURNING is unavailable
    /// (single-row inserts only).
    /// </summary>
    public ColumnMap? IdentityFromLastInsertId { get; init; }

    /// <summary>True when the statement must run in a transaction to be atomic (multi-statement upserts).</summary>
    public bool RequiresTransaction { get; init; }

    /// <summary>Start index of this batch's entities in the caller's list.</summary>
    public int FirstRow { get; init; }

    /// <summary>Applied to the entity after the statement succeeded (new concurrency token, soft-delete timestamp).</summary>
    public Action? OnSuccess { get; init; }

    /// <summary>The timestamp a soft delete writes.</summary>
    public DateTimeOffset? SoftDeletedAt { get; init; }
}
