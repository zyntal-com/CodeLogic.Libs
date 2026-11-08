using System.Data.Common;
using System.Runtime.CompilerServices;

namespace CL.Data.Execution;

/// <summary>
/// Remembers which open connections may carry session state (see <see cref="Providers.DataProvider.ResetsDirtySessions"/>).
/// Commands that run on a connection someone else owns (a transaction, a lock session, a stream) mark it here; the owner
/// takes the mark when it returns the connection to the pool through <see cref="DataNode.ReturnAsync"/>.
/// </summary>
internal static class SessionTracker
{
    private static readonly ConditionalWeakTable<DbConnection, object> Dirty = new();
    private static readonly object Mark = new();

    /// <summary>Marks <paramref name="connection"/> as possibly carrying session state.</summary>
    public static void MarkDirty(DbConnection connection) => Dirty.AddOrUpdate(connection, Mark);

    /// <summary>True when <paramref name="connection"/> was marked; clears the mark.</summary>
    /// <remarks>The lock-free lookup first: almost every returned connection is clean, and removing takes the table's lock.</remarks>
    public static bool TakeDirty(DbConnection connection) => Dirty.TryGetValue(connection, out _) && Dirty.Remove(connection);
}
