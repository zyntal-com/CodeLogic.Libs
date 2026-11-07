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

    private async Task<Result<BackendEntry>> BuildHealthyProviderReplacementAsync(
        string id,
        StorageConnectionConfigBase configuration,
        ProviderDescriptor descriptor,
        RuntimeSnapshot runtime,
        CancellationToken cancellationToken)
    {
        BackendEntry? replacement = null;
        Task<Result>? probeTask = null;
        var deferCleanup = false;
        try
        {
            replacement = new BackendEntry(
                WithLimits(_factories[configuration.GetType()].Create(
                    id,
                    configuration,
                    runtime.MaxBufferedDownloadBytes,
                    _connectionObserver), configuration, _libraryLimits),
                ownsBackend: true);
            var timeoutDuration = TimeSpan.FromSeconds(runtime.HealthCheckTimeoutSeconds);
            using var timeout = new CancellationTokenSource(timeoutDuration);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            Result health;
            try
            {
                probeTask = replacement.Backend.CheckHealthAsync(linked.Token);
                health = await probeTask.WaitAsync(timeoutDuration, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                deferCleanup = true;
                throw;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                deferCleanup = true;
                return Result<BackendEntry>.Failure(StorageErrors.Timeout(
                    $"Storage connection '{id}' timed out during replacement health check."));
            }
            catch (TimeoutException)
            {
                timeout.Cancel();
                deferCleanup = true;
                return Result<BackendEntry>.Failure(StorageErrors.Timeout(
                    $"Storage connection '{id}' timed out during replacement health check."));
            }
            if (health.IsFailure) return Result<BackendEntry>.Failure(health.Error!);
            var result = replacement;
            replacement = null;
            return Result<BackendEntry>.Success(result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return Result<BackendEntry>.Failure(StorageErrors.ProviderError(
                $"Could not build {descriptor.Provider} storage connection '{id}'."));
        }
        finally
        {
            if (replacement is not null)
            {
                var cleanup = ObserveProbeAndDisposeReplacementAsync(id, replacement, probeTask, runtime.Logger);
                if (deferCleanup || probeTask is { IsCompleted: false }) _ = cleanup;
                else await cleanup.ConfigureAwait(false);
            }
        }
    }

    private async Task<Result<BackendEntry>> BuildHealthyLocalReplacementAsync(
        string id,
        LocalConnectionConfig configuration,
        RuntimeSnapshot runtime,
        CancellationToken cancellationToken)
    {
        BackendEntry? replacement = null;
        Task<Result>? probeTask = null;
        var deferCleanup = false;
        var logger = runtime.Logger;
        try
        {
            replacement = new BackendEntry(
                WithLimits(_factories[typeof(LocalConnectionConfig)].Create(
                    id,
                    configuration,
                    runtime.MaxBufferedDownloadBytes), configuration, _libraryLimits),
                ownsBackend: true);

            var timeoutDuration = TimeSpan.FromSeconds(runtime.HealthCheckTimeoutSeconds);
            using var timeout = new CancellationTokenSource(timeoutDuration);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            Result health;
            try
            {
                probeTask = replacement.Backend.CheckHealthAsync(linked.Token);
                health = await probeTask
                    .WaitAsync(timeoutDuration, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                deferCleanup = true;
                throw;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                deferCleanup = true;
                return Result<BackendEntry>.Failure(StorageErrors.Timeout(
                    $"Storage connection '{id}' timed out during replacement health check."));
            }
            catch (TimeoutException)
            {
                timeout.Cancel();
                deferCleanup = true;
                return Result<BackendEntry>.Failure(StorageErrors.Timeout(
                    $"Storage connection '{id}' timed out during replacement health check."));
            }

            if (health.IsFailure)
                return Result<BackendEntry>.Failure(health.Error!);

            var result = replacement;
            replacement = null;
            return Result<BackendEntry>.Success(result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return Result<BackendEntry>.Failure(StorageErrors.ProviderError(
                $"Could not build local storage connection '{id}'."));
        }
        finally
        {
            if (replacement is not null)
            {
                var cleanup = ObserveProbeAndDisposeReplacementAsync(id, replacement, probeTask, logger);
                if (deferCleanup || probeTask is { IsCompleted: false })
                    _ = cleanup;
                else
                    await cleanup.ConfigureAwait(false);
            }
        }
    }

    /// <summary>Removes an effective connection and, for local connections, optionally persists the removal.</summary>
    public async Task<Result> RemoveConnectionAsync(
        string id,
        bool persist = true,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionId(id);
        var runtime = CaptureRuntimeSnapshot();
        cancellationToken.ThrowIfCancellationRequested();
        BackendEntry? removed = null;
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOperational();
            bool exists;
            StorageConnectionInfo? info;
            lock (_registryGate)
                exists = _registry.ContainsKey(id) || _connectionInfos.TryGetValue(id, out info);
            var providerMatch = FindProviderConfigContaining(id, runtime.Context);
            exists = exists ||
                TryFindKey(_localConfig!.Connections, id, out _) ||
                TryFindKey(_persistedLocalConfig!.Connections, id, out _) ||
                providerMatch is not null;
            if (!exists)
                return Result.Failure(StorageErrors.NotFound($"Storage connection '{id}' is not registered."));

            var effectiveLocal = TryFindKey(_localConfig!.Connections, id, out _);
            var persistedLocal = TryFindKey(_persistedLocalConfig!.Connections, id, out var persistedKey);
            if (providerMatch is not null)
            {
                if (persist)
                {
                    var candidate = providerMatch.Config.DeepClone();
                    candidate.RemoveConnection(id);
                    try
                    {
                        await SaveProviderConfigAsync(runtime.Context, candidate).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error)
                    {
                        runtime.Logger.Error(
                            $"Failed to persist removal of {providerMatch.Descriptor.Provider} storage connection '{id}'.",
                            error);
                        return Result.Failure(StorageErrors.ProviderError(
                            $"Could not persist removal of {providerMatch.Descriptor.Provider} storage connection '{id}'."));
                    }
                }
            }
            else if (persist && (effectiveLocal || persistedLocal))
            {
                var candidate = CloneLocalConfig(_persistedLocalConfig);
                if (persistedKey is not null)
                    candidate.Connections.Remove(persistedKey);
                try
                {
                    await SaveConfigAsync(runtime.Context, candidate).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    runtime.Logger.Error($"Failed to persist removal of local storage connection '{id}'.", error);
                    return Result.Failure(StorageErrors.ProviderError(
                        $"Could not persist removal of local storage connection '{id}'."));
                }
                _persistedLocalConfig = CloneLocalConfig(candidate);
                _runtimeLocalOverrides.Remove(id);
                ApplyRuntimeLocalOverrides(candidate);
                _localConfig = candidate;
            }
            else if (!persist && effectiveLocal)
            {
                _runtimeLocalOverrides[id] = null;
                RemoveKey(_localConfig.Connections, id);
            }

            lock (_registryGate)
            {
                if (_registry.TryGetValue(id, out removed))
                    _registry.Remove(id);
                _connectionInfos.Remove(id);
                _health.TryRemove(id, out _);
            }
        }
        finally
        {
            _mutationGate.Release();
        }

        if (removed is not null)
            await removed.RetireAndDisposeAsync().ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>Registers a prebuilt custom backend for this runtime only.</summary>
    public Result RegisterBackend(string id, IStorageBackend backend, bool ownsBackend = true)
        => RegisterBackendAsync(id, backend, ownsBackend).ConfigureAwait(false).GetAwaiter().GetResult();
}
