using System.Runtime.CompilerServices;

namespace CL.Storage.Providers;

/// <summary>Limits and liveness settings for a <see cref="ProviderClientPool{TClient}"/>.</summary>
/// <param name="MaxSessions">Maximum number of open sessions, rented or idle.</param>
/// <param name="MaxIdle">Maximum number of connected sessions kept idle.</param>
/// <param name="IdleLifetime">Idle time after which a pooled session is discarded.</param>
/// <param name="AcquireTimeout">How long a renter waits for a free session slot.</param>
/// <param name="ProbeAfterIdle">Idle time after which a pooled session is probed before reuse.</param>
internal sealed record ProviderPoolOptions(
    int MaxSessions = 8,
    int MaxIdle = 4,
    TimeSpan? IdleLifetime = null,
    TimeSpan? AcquireTimeout = null,
    TimeSpan? ProbeAfterIdle = null)
{
    public static ProviderPoolOptions Default { get; } = new();

    /// <summary>Maps user-facing session configuration onto pool options.</summary>
    public static ProviderPoolOptions From(Configuration.StorageSessionConfig? session) => session is null
        ? Default
        : new ProviderPoolOptions(
            session.MaxSessions,
            session.MaxIdleSessions,
            TimeSpan.FromSeconds(session.IdleLifetimeSeconds),
            TimeSpan.FromSeconds(session.AcquireTimeoutSeconds),
            TimeSpan.FromSeconds(session.ValidateAfterIdleSeconds));
}

/// <summary>Point-in-time pool counters.</summary>
/// <param name="Idle">Connected sessions waiting for reuse.</param>
/// <param name="InUse">Sessions currently rented.</param>
/// <param name="Opened">Sessions connected since the pool was created.</param>
/// <param name="Destroyed">Sessions closed since the pool was created.</param>
/// <param name="ProbeFailures">Idle sessions found dead by the liveness probe.</param>
internal readonly record struct ProviderPoolStats(int Idle, int InUse, int Opened, int Destroyed, int ProbeFailures);

/// <summary>Raised when every session slot stays busy for the whole acquire timeout.</summary>
internal sealed class ProviderPoolExhaustedException(int maxSessions, TimeSpan waited)
    : TimeoutException($"All {maxSessions} sessions stayed in use for {waited.TotalSeconds:0.#} seconds.")
{
    public int MaxSessions { get; } = maxSessions;
}

/// <summary>
/// Keeps connected provider clients alive between operations for the session-oriented backends
/// (FTP, SFTP), which would otherwise pay a full connect and authentication handshake on every call.
/// </summary>
/// <remarks>
/// <para>
/// Clients are rented exclusively: a rented client is owned by one caller until it is returned, so
/// this preserves the single-threaded usage that <c>SftpClient</c> and <c>AsyncFtpClient</c> both
/// require while still reusing the underlying session.
/// </para>
/// <para>
/// Each rented session holds one of <c>MaxSessions</c> slots. A renter takes an idle session when one
/// is available and only connects a new one when none is idle, so rented plus idle sessions never
/// exceed <c>MaxSessions</c> and the pool stays within a server's per-user connection limit.
/// </para>
/// <para>
/// The client's own connection flag reports local socket state, so a session the server has already
/// closed can pass it. Sessions idle longer than <c>ProbeAfterIdle</c> are therefore probed with a
/// cheap round trip before reuse, and any session an operation reports as faulted is destroyed on
/// return rather than handed to the next caller.
/// </para>
/// </remarks>
/// <typeparam name="TClient">Native client type owned by the pool.</typeparam>
internal sealed class ProviderClientPool<TClient> : IAsyncDisposable where TClient : class
{
    private readonly Func<TClient> _create;
    private readonly Func<TClient, CancellationToken, Task> _connectAsync;
    private readonly Func<TClient, bool> _isUsable;
    private readonly Func<TClient, ValueTask> _destroyAsync;
    private readonly Func<TClient, CancellationToken, Task<bool>>? _probeAsync;
    private readonly int _maxSessions;
    private readonly int _maxIdle;
    private readonly TimeSpan _idleLifetime;
    private readonly TimeSpan _acquireTimeout;
    private readonly TimeSpan _probeAfterIdle;
    private readonly Stack<Idle> _idle = new();
    private readonly ConditionalWeakTable<TClient, object> _faulted = [];
    private readonly SemaphoreSlim _slots;
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private bool _disposed;
    private int _connectionsOpened;
    private int _destroyed;
    private int _probeFailures;

    /// <summary>Initializes a pool over one provider's native client lifecycle.</summary>
    /// <param name="create">Creates a new, unconnected client.</param>
    /// <param name="connectAsync">Connects and authenticates a freshly created client.</param>
    /// <param name="isUsable">Reports the client's own connection state; see the type remarks for what this does and does not detect.</param>
    /// <param name="destroyAsync">Disconnects and disposes a client the pool is giving up.</param>
    /// <param name="options">Session limits and liveness settings; defaults when omitted.</param>
    /// <param name="probeAsync">Optional server round trip that returns false or throws when a session is dead.</param>
    /// <param name="time">Clock used for idle expiry; defaults to the system clock.</param>
    public ProviderClientPool(
        Func<TClient> create,
        Func<TClient, CancellationToken, Task> connectAsync,
        Func<TClient, bool> isUsable,
        Func<TClient, ValueTask> destroyAsync,
        ProviderPoolOptions? options = null,
        Func<TClient, CancellationToken, Task<bool>>? probeAsync = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(connectAsync);
        ArgumentNullException.ThrowIfNull(isUsable);
        ArgumentNullException.ThrowIfNull(destroyAsync);
        options ??= ProviderPoolOptions.Default;
        if (options.MaxSessions <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxSessions must be positive.");
        if (options.MaxIdle <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxIdle must be positive.");
        _create = create;
        _connectAsync = connectAsync;
        _isUsable = isUsable;
        _destroyAsync = destroyAsync;
        _probeAsync = probeAsync;
        _maxSessions = options.MaxSessions;
        _maxIdle = Math.Min(options.MaxIdle, options.MaxSessions);
        _idleLifetime = options.IdleLifetime ?? TimeSpan.FromMinutes(2);
        _acquireTimeout = options.AcquireTimeout ?? TimeSpan.FromSeconds(30);
        _probeAfterIdle = options.ProbeAfterIdle ?? TimeSpan.FromSeconds(15);
        _slots = new SemaphoreSlim(_maxSessions, _maxSessions);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Gets the number of clients the pool has connected, reused or not.</summary>
    /// <remarks>Reused for assertions that connection count stays flat across paged listings.</remarks>
    public int ConnectionsOpened => Volatile.Read(ref _connectionsOpened);

    /// <summary>Gets current pool counters.</summary>
    public ProviderPoolStats Stats
    {
        get
        {
            int idle;
            lock (_gate) idle = _idle.Count;
            return new ProviderPoolStats(
                idle,
                _maxSessions - _slots.CurrentCount,
                Volatile.Read(ref _connectionsOpened),
                Volatile.Read(ref _destroyed),
                Volatile.Read(ref _probeFailures));
        }
    }

    /// <summary>Raised after a new session connects.</summary>
    public event Action? SessionOpened;

    /// <summary>Takes a connected client, reusing an idle session when one is live.</summary>
    /// <param name="cancellationToken">Token observed while waiting for a slot and connecting.</param>
    /// <returns>A connected client owned by the caller until it is returned.</returns>
    /// <exception cref="ProviderPoolExhaustedException">No session slot became free within the acquire timeout.</exception>
    public Task<TClient> RentAsync(CancellationToken cancellationToken) => RentAsync(cancellationToken, opened: null);

    /// <summary>Takes a connected client; <paramref name="opened"/> runs when this rent had to open a new session.</summary>
    /// <param name="cancellationToken">Token observed while waiting for a slot and connecting.</param>
    /// <param name="opened">Called after a new session connected for this caller (a shared pool serves several registrations).</param>
    /// <returns>A connected client owned by the caller until it is returned.</returns>
    public async Task<TClient> RentAsync(CancellationToken cancellationToken, Action? opened)
    {
        if (!await _slots.WaitAsync(_acquireTimeout, cancellationToken).ConfigureAwait(false))
            throw new ProviderPoolExhaustedException(_maxSessions, _acquireTimeout);

        TClient? client = null;
        try
        {
            var reused = await TryReuseIdleAsync(cancellationToken).ConfigureAwait(false);
            if (reused is not null) return reused;
            client = _create() ?? throw new InvalidOperationException($"The {typeof(TClient).Name} factory returned null.");
            await _connectAsync(client, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _connectionsOpened);
            SessionOpened?.Invoke();
            opened?.Invoke();
            return client;
        }
        catch
        {
            if (client is not null) await SafeDestroyAsync(client).ConfigureAwait(false);
            _slots.Release();
            throw;
        }
    }

    /// <summary>Flags a rented client so it is destroyed instead of reused when returned.</summary>
    /// <param name="client">Client whose session failed at the transport level.</param>
    public void MarkFaulted(TClient client)
    {
        if (client is not null) _faulted.AddOrUpdate(client, this);
    }

    /// <summary>Returns a client so the next operation can reuse its session.</summary>
    /// <param name="client">Client previously handed out by <see cref="RentAsync(CancellationToken)"/>.</param>
    /// <returns>A task representing the return or the disconnect that replaced it.</returns>
    public async ValueTask ReturnAsync(TClient client)
    {
        if (client is null) return;
        if (!_faulted.TryGetValue(client, out _) && _isUsable(client))
        {
            lock (_gate)
            {
                if (!_disposed && _idle.Count < _maxIdle)
                {
                    // Pooled before the slot is released, so a woken waiter finds it instead of
                    // connecting a session beyond the limit.
                    _idle.Push(new Idle(client, _time.GetUtcNow()));
                    _slots.Release();
                    return;
                }
            }
        }

        await DestroyRentedAsync(client).ConfigureAwait(false);
    }

    /// <summary>Disconnects and disposes a client instead of returning it to the pool.</summary>
    /// <param name="client">Client whose session must not be reused.</param>
    /// <returns>A task representing the disconnect.</returns>
    /// <remarks>
    /// Used for clients handed to callers as native leases: external code may have changed the
    /// working directory or transfer mode, and the pool cannot know whether the session is still in
    /// the state the backend expects.
    /// </remarks>
    public ValueTask DiscardAsync(TClient client) => DestroyRentedAsync(client);

    /// <summary>Disconnects every idle client held by the pool.</summary>
    /// <returns>A task representing asynchronous shutdown.</returns>
    public async ValueTask DisposeAsync()
    {
        Idle[] pooled;
        lock (_gate)
        {
            _disposed = true;
            pooled = [.. _idle];
            _idle.Clear();
        }

        foreach (var entry in pooled) await SafeDestroyAsync(entry.Client).ConfigureAwait(false);
    }

    private async Task<TClient?> TryReuseIdleAsync(CancellationToken cancellationToken)
    {
        while (TryTakeIdle(out var pooled, out var idleFor))
        {
            if (idleFor <= _idleLifetime && _isUsable(pooled) &&
                await IsAliveAsync(pooled, idleFor, cancellationToken).ConfigureAwait(false))
                return pooled;
            await SafeDestroyAsync(pooled).ConfigureAwait(false);
        }
        return null;
    }

    private async Task<bool> IsAliveAsync(TClient client, TimeSpan idleFor, CancellationToken cancellationToken)
    {
        if (_probeAsync is null || idleFor < _probeAfterIdle) return true;
        try
        {
            if (await _probeAsync(client, cancellationToken).ConfigureAwait(false)) return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await SafeDestroyAsync(client).ConfigureAwait(false);
            throw;
        }
        catch
        {
            // A failed probe means the session is dead; the caller gets a fresh one instead.
        }
        Interlocked.Increment(ref _probeFailures);
        return false;
    }

    private bool TryTakeIdle(out TClient client, out TimeSpan idleFor)
    {
        lock (_gate)
        {
            if (_idle.Count > 0)
            {
                var entry = _idle.Pop();
                client = entry.Client;
                // Destroying inside the lock would block other renters, so the caller decides
                // whether the session is still fit and tears it down outside the lock.
                idleFor = _time.GetUtcNow() - entry.PooledAt;
                return true;
            }
        }

        client = null!;
        idleFor = TimeSpan.Zero;
        return false;
    }

    private async ValueTask DestroyRentedAsync(TClient client)
    {
        await SafeDestroyAsync(client).ConfigureAwait(false);
        _slots.Release();
    }

    private async ValueTask SafeDestroyAsync(TClient client)
    {
        Interlocked.Increment(ref _destroyed);
        _faulted.Remove(client);
        try { await _destroyAsync(client).ConfigureAwait(false); }
        catch { /* A client being torn down must never fail the caller's operation. */ }
    }

    private readonly record struct Idle(TClient Client, DateTimeOffset PooledAt);
}
