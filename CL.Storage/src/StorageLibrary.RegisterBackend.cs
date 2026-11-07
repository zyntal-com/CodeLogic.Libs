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

    /// <summary>
    /// Registers a prebuilt custom backend after an asynchronous health check. Ownership transfers only
    /// when registration succeeds; replacement disposal completes before this method returns.
    /// </summary>
    public async Task<Result> RegisterBackendAsync(
        string id,
        IStorageBackend backend,
        bool ownsBackend = true,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionId(id);
        ArgumentNullException.ThrowIfNull(backend);
        var runtime = CaptureRuntimeSnapshot();
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(id, backend.ConnectionId, StringComparison.OrdinalIgnoreCase))
            return Result.Failure(StorageErrors.Conflict(
                $"Backend connection ID '{backend.ConnectionId}' does not match registry ID '{id}'."));

        var health = await CheckRegistrationHealthAsync(
            id,
            backend,
            runtime.HealthCheckTimeoutSeconds,
            runtime.Logger,
            cancellationToken).ConfigureAwait(false);
        if (health.IsFailure)
            return health;

        BackendEntry? previous = null;
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOperational();
            lock (_registryGate)
            {
                if (_registry.TryGetValue(id, out previous) && ReferenceEquals(previous.Backend, backend))
                    return Result.Success();
                var replacement = new BackendEntry(backend, ownsBackend);
                _registry[id] = replacement;
                _connectionInfos[id] = new StorageConnectionInfo(
                    backend.ConnectionId,
                    backend.Provider,
                    backend.Root,
                    Enabled: true);
            }
        }
        finally
        {
            _mutationGate.Release();
        }

        if (previous is not null)
            await previous.RetireAndDisposeAsync().ConfigureAwait(false);
        return Result.Success();
    }

    private static async Task<Result> CheckRegistrationHealthAsync(
        string id,
        IStorageBackend backend,
        int timeoutSeconds,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var result = await backend.CheckHealthAsync(linked.Token)
                .WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken)
                .ConfigureAwait(false);
            return result.IsSuccess
                ? Result.Success()
                : Result.Failure(StorageErrors.Unavailable(
                    $"Storage backend '{id}' failed its registration health check.",
                    result.Error?.Code ?? StorageErrors.ProviderErrorCode));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return Result.Failure(StorageErrors.Timeout(
                $"Storage backend '{id}' timed out during its registration health check."));
        }
        catch (TimeoutException)
        {
            timeout.Cancel();
            return Result.Failure(StorageErrors.Timeout(
                $"Storage backend '{id}' timed out during its registration health check."));
        }
        catch (Exception error)
        {
            logger.Error($"Storage backend '{id}' threw during its registration health check.", error);
            return Result.Failure(StorageErrors.ProviderError(
                $"Storage backend '{id}' failed its registration health check."));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        CL.Internal.AsyncCleanup.Observe(DisposeAsync().AsTask(), TryCaptureLogger());
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        LifecycleState state;
        lock (_stateGate)
            state = _state;
        if (state == LifecycleState.Disposed)
            return;
        try
        {
            await OnStopAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_stateGate)
                _state = LifecycleState.Disposed;
            GC.SuppressFinalize(this);
        }
    }

    internal BackendEntry.BackendOperationLease AcquireOperation(string connectionId)
    {
        while (true)
        {
            EnsureOperational();
            BackendEntry entry;
            lock (_registryGate)
            {
                if (!_registry.TryGetValue(connectionId, out entry!))
                    throw new KeyNotFoundException($"Storage connection '{connectionId}' is not registered or enabled.");
            }
            if (entry.TryAcquire(out var lease))
                return lease!;
        }
    }

    private StorageEventPublisher? TryCaptureEventPublisher()
    {
        lock (_stateGate)
            return _context is { } context ? new StorageEventPublisher(context.Events, context.Logger) : null;
    }

    private ILogger? TryCaptureLogger()
    {
        lock (_stateGate)
            return _context?.Logger;
    }

    internal StorageConnectionObserver ConnectionObserver => _connectionObserver;

    /// <summary>Gets whether the library keeps its configuration in memory only.</summary>
    public bool RuntimeOnly => _runtimeOnly;

    /// <summary>Reads a configuration section, from memory in runtime-only mode.</summary>
    private T ReadConfig<T>(LibraryContext context) where T : ConfigModelBase, new() =>
        _runtimeOnly
            ? (T)_memoryConfig.GetOrAdd(typeof(T), _ => typeof(T) == typeof(StorageConfig) && _runtimeSettings is not null ? _runtimeSettings : new T())
            : context.Configuration.Get<T>();

    /// <summary>Saves a configuration section, to memory in runtime-only mode.</summary>
    /// <remarks>
    /// Marks the save as the library's own, so the resulting config-change notification is not
    /// applied a second time (see <see cref="OnConfigChangedAsync"/>): the callers hold the mutation
    /// gate and update the registry themselves.
    /// </remarks>
    private async Task SaveConfigAsync<T>(LibraryContext context, T value) where T : ConfigModelBase, new()
    {
        if (_runtimeOnly)
        {
            _memoryConfig[typeof(T)] = value;
            return;
        }

        _savingOwnConfig.Value = true;
        try
        {
            await context.Configuration.SaveAsync(value).ConfigureAwait(false);
        }
        finally
        {
            _savingOwnConfig.Value = false;
        }
    }

    internal StorageEventPublisher CaptureEventPublisher()
    {
        LibraryContext context;
        lock (_stateGate)
            context = _context ?? throw new InvalidOperationException("Storage library context is unavailable.");
        return new StorageEventPublisher(context.Events, context.Logger);
    }

    private static async Task ObserveProbeAndDisposeReplacementAsync(
        string id,
        BackendEntry replacement,
        Task<Result>? probeTask,
        ILogger logger)
    {
        if (probeTask is not null)
        {
            try { await probeTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                TryLog(logger, $"Unpublished storage replacement '{id}' health probe failed after the caller stopped waiting.", error);
            }
        }

        try { await replacement.RetireAndDisposeAsync().ConfigureAwait(false); }
        catch (Exception error)
        {
            TryLog(logger, $"Failed to dispose unpublished storage replacement '{id}'.", error);
        }
    }
}
