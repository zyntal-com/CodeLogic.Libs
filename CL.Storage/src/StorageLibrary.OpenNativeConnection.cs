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

    /// <summary>Opens a provider-native session whose lease retains the backend until disposal.</summary>
    public async Task<Result<NativeConnectionLease<TClient>>> OpenNativeConnectionAsync<TClient>(
        string connectionId = "Default",
        CancellationToken cancellationToken = default)
        where TClient : class
    {
        ValidateConnectionId(connectionId);
        cancellationToken.ThrowIfCancellationRequested();
        var entryLease = AcquireOperation(connectionId);
        try
        {
            var result = await entryLease.Backend.OpenNativeConnectionAsync<TClient>(cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                entryLease.Dispose();
                return Result<NativeConnectionLease<TClient>>.Failure(result.Error!);
            }

            var backendLease = result.Value!;
            return Result<NativeConnectionLease<TClient>>.Success(new NativeConnectionLease<TClient>(
                backendLease.Client,
                async _ =>
                {
                    try { await backendLease.DisposeAsync().ConfigureAwait(false); }
                    finally { entryLease.Dispose(); }
                }));
        }
        catch
        {
            entryLease.Dispose();
            throw;
        }
    }

    /// <summary>Adds or replaces a built-in typed connection and optionally persists its JSON section.</summary>
    public async Task<Result> AddOrUpdateConnectionAsync<TConfig>(
        string id,
        TConfig config,
        bool persist = true,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionId(id);
        ArgumentNullException.ThrowIfNull(config);
        var runtime = CaptureRuntimeSnapshot();
        cancellationToken.ThrowIfCancellationRequested();
        if (config is not LocalConnectionConfig localConnection)
        {
            if (config is StorageConnectionConfigBase providerConnection)
                return await AddOrUpdateProviderConnectionAsync(
                    id,
                    providerConnection,
                    persist,
                    runtime,
                    cancellationToken).ConfigureAwait(false);
            return Result.Failure(StorageErrors.Unsupported(
                $"Connection configuration type '{typeof(TConfig).Name}' does not have a provider factory in this version."));
        }

        var validation = localConnection.Validate();
        if (!validation.IsValid)
            return Result.Failure(StorageErrors.InvalidContent(
                $"Local storage connection '{id}' is invalid: {string.Join("; ", validation.Errors)}"));

        BackendEntry? replacement = null;
        if (localConnection.Enabled)
        {
            var built = await BuildHealthyLocalReplacementAsync(
                id,
                localConnection,
                runtime,
                cancellationToken).ConfigureAwait(false);
            if (built.IsFailure)
                return Result.Failure(built.Error!);
            replacement = built.Value!;
        }

        BackendEntry? previous = null;
        var gateEntered = false;
        try
        {
            await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
            EnsureOperational();

            var conflictingSection = FindConfiguredNonLocalSection(id, runtime.Context);
            if (conflictingSection is not null)
                return Result.Failure(StorageErrors.Conflict(
                    $"Storage connection ID '{id}' is already configured in '{conflictingSection}'. IDs are case-insensitive."));

            if (persist)
            {
                var candidate = CloneLocalConfig(_persistedLocalConfig!);
                RemoveKey(candidate.Connections, id);
                candidate.Connections[id] = Clone(localConnection);
                try
                {
                    await SaveConfigAsync(runtime.Context, candidate).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    runtime.Logger.Error($"Failed to persist local storage connection '{id}'.", error);
                    return Result.Failure(StorageErrors.ProviderError(
                        $"Could not persist local storage connection '{id}'."));
                }
                _persistedLocalConfig = CloneLocalConfig(candidate);
                _runtimeLocalOverrides.Remove(id);
                ApplyRuntimeLocalOverrides(candidate);
                _localConfig = candidate;
            }
            else
            {
                _runtimeLocalOverrides[id] = Clone(localConnection);
                SetLocalConnection(_localConfig!, id, localConnection);
            }

            lock (_registryGate)
            {
                if (_registry.TryGetValue(id, out previous))
                    _registry.Remove(id);
                if (replacement is not null)
                    _registry[id] = replacement;
                _connectionInfos[id] = new StorageConnectionInfo(id, StorageProvider.Local, localConnection.RootPath, localConnection.Enabled);
                _health.TryRemove(id, out _);
            }
            replacement = null;
        }
        catch (OperationCanceledException) { throw; }
        finally
        {
            if (gateEntered)
                _mutationGate.Release();
            if (replacement is not null)
                await replacement.RetireAndDisposeAsync().ConfigureAwait(false);
        }

        if (previous is not null)
            await previous.RetireAndDisposeAsync().ConfigureAwait(false);
        return Result.Success();
    }

    private async Task<Result> AddOrUpdateProviderConnectionAsync(
        string id,
        StorageConnectionConfigBase connection,
        bool persist,
        RuntimeSnapshot runtime,
        CancellationToken cancellationToken)
    {
        var descriptor = DescribeProviderConnection(connection.GetType());
        if (descriptor is null || !_factories.ContainsKey(connection.GetType()))
            return Result.Failure(StorageErrors.Unsupported(
                $"Connection configuration type '{connection.GetType().Name}' does not have a provider factory."));

        var errors = connection.GetValidationErrors().ToArray();
        if (errors.Length > 0)
            return Result.Failure(StorageErrors.InvalidContent(
                $"{descriptor.Value.Provider} storage connection '{id}' is invalid: {string.Join("; ", errors)}"));

        var effectiveConnection = CloneProviderConnection(connection);
        BackendEntry? replacement = null;
        if (effectiveConnection.Enabled)
        {
            var built = await BuildHealthyProviderReplacementAsync(
                id,
                effectiveConnection,
                descriptor.Value,
                runtime,
                cancellationToken).ConfigureAwait(false);
            if (built.IsFailure) return Result.Failure(built.Error!);
            replacement = built.Value!;
        }

        BackendEntry? previous = null;
        var gateEntered = false;
        try
        {
            await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
            EnsureOperational();

            var conflictingSection = FindConfiguredSection(
                id,
                runtime.Context,
                exceptConnectionType: effectiveConnection.GetType());
            if (conflictingSection is not null)
                return Result.Failure(StorageErrors.Conflict(
                    $"Storage connection ID '{id}' is already configured in '{conflictingSection}'. IDs are case-insensitive."));
            lock (_registryGate)
            {
                if (_connectionInfos.TryGetValue(id, out var existing) && existing.Provider != descriptor.Value.Provider)
                    return Result.Failure(StorageErrors.Conflict(
                        $"Storage connection ID '{id}' is already registered for provider '{existing.Provider}'."));
            }

            if (persist)
            {
                var current = GetProviderConfig(runtime.Context, effectiveConnection.GetType());
                var candidate = current.DeepClone();
                candidate.SetConnection(id, effectiveConnection);
                try
                {
                    await SaveProviderConfigAsync(runtime.Context, candidate).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    runtime.Logger.Error($"Failed to persist {descriptor.Value.Provider} storage connection '{id}'.", error);
                    return Result.Failure(StorageErrors.ProviderError(
                        $"Could not persist {descriptor.Value.Provider} storage connection '{id}'."));
                }
            }

            lock (_registryGate)
            {
                if (_registry.TryGetValue(id, out previous)) _registry.Remove(id);
                if (replacement is not null) _registry[id] = replacement;
                _connectionInfos[id] = DescribeConnection(id, descriptor.Value.Provider, effectiveConnection);
                _health.TryRemove(id, out _);
            }
            replacement = null;
        }
        catch (OperationCanceledException) { throw; }
        finally
        {
            if (gateEntered) _mutationGate.Release();
            if (replacement is not null)
                await replacement.RetireAndDisposeAsync().ConfigureAwait(false);
        }

        if (previous is not null)
            await previous.RetireAndDisposeAsync().ConfigureAwait(false);
        return Result.Success();
    }
}
