using System.Data.Common;
using System.Runtime.CompilerServices;

namespace CL.Data.Execution;

/// <summary>How an open connection may have been left with session state.</summary>
internal enum SessionMark
{
    /// <summary>Not marked.</summary>
    None = 0,

    /// <summary>A statement may have changed the session (raw SQL, an interrupted command, a lock not released cleanly).</summary>
    Dirty = 1,

    /// <summary>
    /// User code had the raw connection (<c>WithConnectionAsync</c>): besides session state it may have prepared statements,
    /// whose handles the driver caches, so the reset must also clear the driver's cache.
    /// </summary>
    HandedToUser = 2,
}

/// <summary>
/// Remembers which open connections may carry session state (see <see cref="Providers.DataProvider.ResetsDirtySessions"/>).
/// Commands that run on a connection someone else owns (a transaction, a lock session, a stream) mark it here; the owner
/// takes the mark when it returns the connection to the pool through <see cref="DataNode.ReturnAsync"/>. A mark only ever
/// grows: <see cref="SessionMark.HandedToUser"/> is never downgraded to <see cref="SessionMark.Dirty"/>.
/// </summary>
internal static class SessionTracker
{
    private static readonly ConditionalWeakTable<DbConnection, object> Marks = new();
    private static readonly object DirtyMark = SessionMark.Dirty;
    private static readonly object HandedMark = SessionMark.HandedToUser;

    /// <summary>Marks <paramref name="connection"/> as possibly carrying session state.</summary>
    public static void MarkDirty(DbConnection connection) =>
        Marks.AddOrUpdate(connection, Marks.TryGetValue(connection, out var current) && ReferenceEquals(current, HandedMark) ? HandedMark : DirtyMark);

    /// <summary>Marks <paramref name="connection"/> as handed to user code (implies dirty).</summary>
    public static void MarkHandedToUser(DbConnection connection) => Marks.AddOrUpdate(connection, HandedMark);

    /// <summary>The mark of <paramref name="connection"/>, cleared.</summary>
    /// <remarks>The lock-free lookup first: almost every returned connection is clean, and removing takes the table's lock.</remarks>
    public static SessionMark Take(DbConnection connection)
    {
        if (!Marks.TryGetValue(connection, out var mark)) return SessionMark.None;
        Marks.Remove(connection);
        return (SessionMark)mark;
    }
}
