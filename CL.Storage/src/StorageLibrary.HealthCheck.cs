using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CL.Storage.Providers;
using CL.Storage.Providers.Azure;
using CL.Storage.Providers.Ftp;
using CL.Storage.Providers.GoogleCloud;
using CL.Storage.Providers.Local;
using CL.Storage.Providers.S3;
using CL.Storage.Providers.Sftp;
using CL.Storage.Providers.Swift;
using CL.Storage.Providers.WebDav;
using CL.Storage.Registry;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using CodeLogic.Framework.Libraries;

namespace CL.Storage;

public sealed partial class StorageLibrary
{

    /// <inheritdoc />
    public async Task<HealthStatus> HealthCheckAsync()
    {
        if (!TryCaptureRuntimeSnapshot(out _, out var state))
            return HealthStatus.Unhealthy($"Storage library is {DescribeState(state)}");

        var targets = new List<HealthTarget>();
        int timeoutSeconds;
        ILogger logger;
        try
        {
            await _mutationGate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return HealthStatus.Unhealthy("Storage library is disposed");
        }

        try
        {
            if (!TryCaptureRuntimeSnapshot(out var runtime, out state))
                return HealthStatus.Unhealthy($"Storage library is {DescribeState(state)}");
            if (!runtime.Enabled)
                return HealthStatus.Healthy("Storage library is disabled");

            timeoutSeconds = runtime.HealthCheckTimeoutSeconds;
            logger = runtime.Logger;

            lock (_registryGate)
            {
                if (!_runtimeOnly && !_registry.ContainsKey(runtime.DefaultConnection))
                    return HealthStatus.Unhealthy($"Default storage connection '{runtime.DefaultConnection}' is unavailable");
                foreach (var (id, entry) in _registry)
                {
                    if (!entry.TryAcquire(out var lease))
                    {
                        foreach (var target in targets)
                            target.Lease.Dispose();
                        targets.Clear();
                        return HealthStatus.Unhealthy($"Storage connection '{id}' is retiring");
                    }
                    targets.Add(new HealthTarget(id, lease!));
                }
            }
        }
        finally
        {
            _mutationGate.Release();
        }

        var probes = await Task.WhenAll(
            targets.Select(target => ProbeHealthAsync(target, timeoutSeconds, logger))).ConfigureAwait(false);
        foreach (var probe in probes)
            RecordHealth(probe.Id, probe.Provider, probe.Healthy, probe.Healthy ? null : probe.Detail, probe.Latency);

        state = GetLifecycleState();
        if (state is not (LifecycleState.Initialized or LifecycleState.Started))
            return HealthStatus.Unhealthy($"Storage library is {DescribeState(state)}");

        var failed = probes.Where(probe => !probe.Healthy).ToArray();
        if (failed.Length == 0)
            return HealthStatus.Healthy($"All {probes.Length} storage connection(s) are healthy");

        var failedIds = string.Join(", ", failed.Select(probe => probe.Id));
        return new HealthStatus
        {
            Status = failed.Length == probes.Length ? HealthStatusLevel.Unhealthy : HealthStatusLevel.Degraded,
            Message = failed.Length == probes.Length
                ? $"All storage connections failed: {failedIds}"
                : $"Storage connections unavailable: {failedIds}",
            Data = new Dictionary<string, object>
            {
                ["failedConnections"] = failed.ToDictionary(
                    probe => probe.Id,
                    probe => (object)probe.Detail,
                    StringComparer.OrdinalIgnoreCase)
            }
        };
    }

    /// <summary>Returns a stable service proxy for a named effective connection.</summary>
    public IStorageService GetStorage(string connectionId = "Default")
    {
        ValidateConnectionId(connectionId);
        EnsureOperational();
        lock (_registryGate)
        {
            if (!_registry.TryGetValue(connectionId, out var entry))
                throw new KeyNotFoundException($"Storage connection '{connectionId}' is not registered or enabled.");
            var effectiveId = entry.Backend.ConnectionId;
            if (!_proxies.TryGetValue(effectiveId, out var proxy))
            {
                proxy = new StorageServiceProxy(this, effectiveId);
                _proxies.Add(effectiveId, proxy);
            }
            return proxy;
        }
    }

    /// <summary>Attempts to return a stable service proxy without throwing for an unknown or disabled connection.</summary>
    public bool TryGetStorage(
        string connectionId,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IStorageService? storage)
    {
        ValidateConnectionId(connectionId);
        EnsureOperational();
        lock (_registryGate)
        {
            if (!_registry.TryGetValue(connectionId, out var entry))
            {
                storage = null;
                return false;
            }
            var effectiveId = entry.Backend.ConnectionId;
            if (!_proxies.TryGetValue(effectiveId, out var proxy))
            {
                proxy = new StorageServiceProxy(this, effectiveId);
                _proxies.Add(effectiveId, proxy);
            }
            storage = proxy;
            return true;
        }
    }

    /// <summary>Checks one connection using the configured health timeout.</summary>
    public async Task<Result<HealthStatus>> CheckConnectionHealthAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionId(connectionId);
        cancellationToken.ThrowIfCancellationRequested();
        var runtime = CaptureRuntimeSnapshot();
        using var lease = AcquireOperation(connectionId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(runtime.HealthCheckTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var started = Stopwatch.GetTimestamp();
        var result = await CheckConnectionHealthCoreAsync(lease.Backend, linked.Token, timeout, cancellationToken).ConfigureAwait(false);
        RecordHealth(lease.Backend.ConnectionId, lease.Backend.Provider, result.IsSuccess, result.Error?.Code, Stopwatch.GetElapsedTime(started));
        return result;
    }

    private static async Task<Result<HealthStatus>> CheckConnectionHealthCoreAsync(
        IStorageBackend backend,
        CancellationToken probeToken,
        CancellationTokenSource timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var health = await backend.CheckHealthAsync(probeToken).ConfigureAwait(false);
            if (health.IsFailure)
                return Result<HealthStatus>.Failure(health.Error!);
            return Result<HealthStatus>.Success(HealthStatus.Healthy(
                $"Storage connection '{backend.ConnectionId}' is healthy"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return Result<HealthStatus>.Failure(StorageErrors.Timeout(
                $"Storage connection '{backend.ConnectionId}' timed out during health check."));
        }
        catch (TimeoutException)
        {
            return Result<HealthStatus>.Failure(StorageErrors.Timeout(
                $"Storage connection '{backend.ConnectionId}' timed out during health check."));
        }
        catch (Exception)
        {
            return Result<HealthStatus>.Failure(StorageErrors.ProviderError(
                $"Storage connection '{backend.ConnectionId}' failed its health check."));
        }
    }

    /// <summary>Stores a health result and publishes <see cref="StorageConnectionHealthChangedEvent"/> when the state flips.</summary>
    private void RecordHealth(string id, StorageProvider provider, bool healthy, string? errorCode, TimeSpan latency)
    {
        var current = new StorageConnectionHealth(healthy, healthy ? null : errorCode, latency, DateTimeOffset.UtcNow);
        StorageConnectionHealth? previous = null;
        _health.AddOrUpdate(id, current, (_, existing) =>
        {
            previous = existing;
            return current;
        });
        if (previous?.Healthy != healthy)
            _connectionObserver.HealthChanged(id, provider, previous, current);
    }

    /// <summary>
    /// Describes a registered connection and the server behind it: address, transport security, the
    /// certificate or host key presented, negotiated algorithms, server software and features, session
    /// pool counters, and the last health check. Opens a session when the provider needs one to answer.
    /// </summary>
    /// <param name="connectionId">Connection to describe.</param>
    /// <param name="cancellationToken">Token used to cancel the request.</param>
    /// <returns>The diagnostics, or the error from opening a session.</returns>
    public async Task<Result<StorageConnectionDiagnostics>> GetConnectionDiagnosticsAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionId(connectionId);
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = AcquireOperation(connectionId);
        StorageConnectionInfo? info;
        lock (_registryGate)
            _connectionInfos.TryGetValue(lease.Backend.ConnectionId, out info);
        _health.TryGetValue(lease.Backend.ConnectionId, out var health);
        return await DiagnoseAsync(lease.Backend, info?.Host, info?.Port, info?.Security ?? StorageTransportSecurity.Unknown, health, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<Result<StorageConnectionDiagnostics>> DiagnoseAsync(
        IStorageBackend backend,
        string? host,
        int? port,
        StorageTransportSecurity security,
        StorageConnectionHealth? health,
        CancellationToken cancellationToken)
    {
        var diagnostics = new StorageConnectionDiagnostics
        {
            ConnectionId = backend.ConnectionId,
            Provider = backend.Provider,
            Host = host,
            Port = port,
            Security = security,
            LastHealth = health
        };
        if (backend is not IStorageDiagnosticsSource source)
            return Result<StorageConnectionDiagnostics>.Success(diagnostics);
        var details = await source.GetServerDetailsAsync(cancellationToken).ConfigureAwait(false);
        if (details.IsFailure)
            return Result<StorageConnectionDiagnostics>.Failure(details.Error!);
        return Result<StorageConnectionDiagnostics>.Success(diagnostics with
        {
            ServerSystem = details.Value!.System,
            ServerSoftware = details.Value.Software,
            ServerFeatures = details.Value.Features,
            Negotiated = details.Value.Negotiated,
            ServerIdentity = source.PresentedIdentity,
            Pool = source.PoolStats
        });
    }
}
