using CL.Data.Errors;
using CodeLogic.Core.Results;

namespace CL.Data.Execution;

/// <summary>
/// A named advisory lock held on its own connection (MySQL <c>GET_LOCK</c>, PostgreSQL
/// <c>pg_try_advisory_lock</c>, SQL Server <c>sp_getapplock</c>). Other processes asking for the same name wait
/// until it is released. Dispose to release.
/// </summary>
public sealed class DataLock : IAsyncDisposable
{
    private readonly Database _db;
    private DataSession? _session;

    private DataLock(Database db, string name, DataSession session)
    {
        _db = db;
        Name = name;
        _session = session;
    }

    /// <summary>The lock name.</summary>
    public string Name { get; }

    internal DataSession Session => _session ?? throw new ObjectDisposedException(nameof(DataLock));

    /// <summary>
    /// Acquires <paramref name="name"/>, retrying until <paramref name="timeout"/> passes
    /// (<c>data.lock_not_acquired</c> then). <c>data.unsupported</c> when the provider has no advisory locks.
    /// </summary>
    internal static async ValueTask<Result<DataLock>> AcquireAsync(Database db, string name, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!db.Provider.Capabilities.AdvisoryLocks)
            return DataErrors.Unsupported($"{db.Provider.Name} has no advisory locks.");

        var opened = await DataSession.OpenAsync(db, ct).ConfigureAwait(false);
        if (opened.IsFailure) return opened.Error!;
        var session = opened.Value!;

        var time = db.Options.Time;
        var deadline = time.GetTimestamp() + (long)(timeout.TotalSeconds * time.TimestampFrequency);
        var delay = TimeSpan.FromMilliseconds(50);
        while (true)
        {
            var attempt = await session.NumberAsync(db.Provider.TryLockSql(name), ct).ConfigureAwait(false);
            if (attempt.IsFailure)
            {
                // The server may have granted the lock before the failure (a cancelled or timed-out read): never pool it held.
                session.MarkDirty();
                await session.DisposeAsync().ConfigureAwait(false);
                return attempt.Error!;
            }
            if (attempt.Value > 0) return new DataLock(db, name, session);

            if (time.GetTimestamp() >= deadline)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return DataErrors.LockNotAcquired($"Lock '{name}' is held by another session.", $"Waited {timeout.TotalSeconds:0.###}s.");
            }
            try
            {
                await Task.Delay(delay, time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return DataErrors.Cancelled();
            }
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 1000));
        }
    }

    /// <summary>Releases the lock and closes its connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_session is not { } session) return;
        _session = null;
        // The explicit release keeps the pooled connection clean. When it fails, or does not report the lock as released
        // (MySQL RELEASE_LOCK and pg_advisory_unlock return 1/true), the session is marked dirty: providers that reset dirty
        // sessions reset it before it can be reused (the others reset every session anyway).
        var released = await session.NumberAsync(_db.Provider.UnlockSql(Name), CancellationToken.None).ConfigureAwait(false);
        if (released.IsFailure || released.Value != 1) session.MarkDirty();
        await session.DisposeAsync().ConfigureAwait(false);
    }
}
