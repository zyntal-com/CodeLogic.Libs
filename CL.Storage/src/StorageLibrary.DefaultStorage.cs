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

    /// <summary>Returns the service for the configured default connection.</summary>
    public IStorageService DefaultStorage => GetStorage(GetDefaultConnectionId());

    /// <inheritdoc />
    public Task OnConfigureAsync(LibraryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_stateGate)
        {
            if (_state != LifecycleState.Created)
                throw new InvalidOperationException("Storage library configuration can only run once.");
            _context = context;
            _state = LifecycleState.Configured;
        }

        // Runtime-only libraries never touch configuration files; sections live in memory instead.
        if (_runtimeOnly)
            return Task.CompletedTask;
        context.Configuration.Register<StorageConfig>("storage");
        context.Configuration.Register<LocalStorageConfig>("storage.local");
        context.Configuration.Register<S3StorageConfig>("storage.s3");
        context.Configuration.Register<FtpStorageConfig>("storage.ftp");
        context.Configuration.Register<SftpStorageConfig>("storage.sftp");
        context.Configuration.Register<WebDavStorageConfig>("storage.webdav");
        context.Configuration.Register<AzureStorageConfig>("storage.azure");
        context.Configuration.Register<GoogleCloudStorageConfig>("storage.gcs");
        context.Configuration.Register<SwiftStorageConfig>("storage.swift");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task OnInitializeAsync(LibraryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_stateGate)
        {
            if (_state != LifecycleState.Configured)
                throw new InvalidOperationException("Storage library must be configured before it is initialized.");
            if (!ReferenceEquals(_context, context))
                throw new InvalidOperationException("Storage library lifecycle phases must use the same LibraryContext.");
        }

        var storage = ReadConfig<StorageConfig>(context);
        var local = ReadConfig<LocalStorageConfig>(context);
        var s3 = ReadConfig<S3StorageConfig>(context);
        var ftp = ReadConfig<FtpStorageConfig>(context);
        var sftp = ReadConfig<SftpStorageConfig>(context);
        var webDav = ReadConfig<WebDavStorageConfig>(context);
        var azure = ReadConfig<AzureStorageConfig>(context);
        var gcs = ReadConfig<GoogleCloudStorageConfig>(context);
        var swift = ReadConfig<SwiftStorageConfig>(context);

        EnsureValid("storage", storage.Validate());
        EnsureValid("storage.local", local.Validate());
        EnsureValid("storage.s3", s3.Validate());
        EnsureValid("storage.ftp", ftp.Validate());
        EnsureValid("storage.sftp", sftp.Validate());
        EnsureValid("storage.webdav", webDav.Validate());
        EnsureValid("storage.azure", azure.Validate());
        EnsureValid("storage.gcs", gcs.Validate());
        EnsureValid("storage.swift", swift.Validate());
        var libraryLimits = StorageTransferPipeline.LibraryLimits(storage.MaxTotalUploadBytesPerSecond, storage.MaxTotalDownloadBytesPerSecond);

        ValidateGlobalIds(local, s3, ftp, sftp, webDav, azure, gcs, swift);

        var builtEntries = new Dictionary<string, BackendEntry>(StringComparer.OrdinalIgnoreCase);
        var infos = new Dictionary<string, StorageConnectionInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (storage.Enabled)
            {
                foreach (var (id, configuration) in local.Connections)
                {
                    infos.Add(id, new StorageConnectionInfo(id, StorageProvider.Local, configuration.RootPath, configuration.Enabled));
                    if (!configuration.Enabled)
                        continue;
                    var backend = WithLimits(_factories[typeof(LocalConnectionConfig)].Create(
                        id,
                        configuration,
                        storage.MaxBufferedDownloadBytes), configuration, libraryLimits);
                    builtEntries.Add(id, new BackendEntry(backend, ownsBackend: true));
                }

                AddProviderConnections(builtEntries, infos, s3, StorageProvider.S3, storage.MaxBufferedDownloadBytes, libraryLimits);
                AddProviderConnections(builtEntries, infos, ftp, StorageProvider.Ftp, storage.MaxBufferedDownloadBytes, libraryLimits);
                AddProviderConnections(builtEntries, infos, sftp, StorageProvider.Sftp, storage.MaxBufferedDownloadBytes, libraryLimits);
                AddProviderConnections(builtEntries, infos, webDav, StorageProvider.WebDav, storage.MaxBufferedDownloadBytes, libraryLimits);
                AddProviderConnections(builtEntries, infos, azure, StorageProvider.AzureBlob, storage.MaxBufferedDownloadBytes, libraryLimits);
                AddProviderConnections(builtEntries, infos, gcs, StorageProvider.GoogleCloudStorage, storage.MaxBufferedDownloadBytes, libraryLimits);
                AddProviderConnections(builtEntries, infos, swift, StorageProvider.OpenStackSwift, storage.MaxBufferedDownloadBytes, libraryLimits);

                if (!_runtimeOnly && !builtEntries.ContainsKey(storage.DefaultConnection))
                    throw new InvalidOperationException(
                        $"The configured default storage connection '{storage.DefaultConnection}' is not available from an enabled provider factory.");
            }

            lock (_stateGate)
            {
                if (_state != LifecycleState.Configured || !ReferenceEquals(_context, context))
                    throw new InvalidOperationException("Storage library stopped before initialization completed.");

                lock (_registryGate)
                {
                    foreach (var pair in builtEntries)
                        _registry.Add(pair.Key, pair.Value);
                    foreach (var pair in infos)
                        _connectionInfos.Add(pair.Key, pair.Value);
                }

                _storageConfig = storage;
                _libraryLimits = libraryLimits;
                _localConfig = local;
                _persistedLocalConfig = CloneLocalConfig(local);
                _runtimeLocalOverrides.Clear();
                _enabled = storage.Enabled;
                _state = LifecycleState.Initialized;
            }
        }
        catch
        {
            await Task.WhenAll(builtEntries.Values.Select(entry => entry.RetireAndDisposeAsync())).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public Task OnStartAsync(LibraryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_stateGate)
        {
            if (_state != LifecycleState.Initialized)
                throw new InvalidOperationException("Storage library must be initialized before it is started.");
            if (!ReferenceEquals(_context, context))
                throw new InvalidOperationException("Storage library lifecycle phases must use the same LibraryContext.");
            _state = LifecycleState.Started;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnStopAsync()
    {
        lock (_stateGate)
        {
            if (_state is LifecycleState.Stopped or LifecycleState.Disposed)
                return Task.CompletedTask;
            if (_stopTask is not null)
                return _stopTask;
            _state = LifecycleState.Stopping;
            _stopTask = StopCoreAsync();
            return _stopTask;
        }
    }

    private async Task StopCoreAsync()
    {
        BackendEntry[] entries = [];
        var gateEntered = false;
        try
        {
            // Queues first, while their connections still work: running jobs record where they stopped
            // (queued again or interrupted) instead of failing on retired connections.
            Queue.StorageTransferQueue[] queues;
            lock (_queues) queues = [.. _queues];
            await Task.WhenAll(queues.Select(queue => queue.DisposeAsync().AsTask())).ConfigureAwait(false);

            await _mutationGate.WaitAsync().ConfigureAwait(false);
            gateEntered = true;
            try
            {
                lock (_registryGate)
                {
                    entries = _registry.Values.ToArray();
                    _registry.Clear();
                    _connectionInfos.Clear();
                }
            }
            finally
            {
                _mutationGate.Release();
                gateEntered = false;
            }

            var disposals = entries
                .Select(entry => (entry.Backend.ConnectionId, Task: entry.RetireAndDisposeAsync()))
                .ToArray();
            try
            {
                await Task.WhenAll(disposals.Select(disposal => disposal.Task)).ConfigureAwait(false);
            }
            catch
            {
                var failedIds = new List<string>();
                foreach (var disposal in disposals.Where(disposal => disposal.Task.IsFaulted))
                {
                    failedIds.Add(disposal.ConnectionId);
                    _context?.Logger.Error(
                        $"Storage backend '{disposal.ConnectionId}' failed during disposal.",
                        disposal.Task.Exception?.GetBaseException());
                }
                throw new InvalidOperationException(
                    $"Failed to dispose storage backend(s): {string.Join(", ", failedIds)}.");
            }
            // Sessions kept warm for re-registration (LingerSeconds) do not outlive the library; another
            // library's are left alone. Its connections were created with this observer, which identifies it.
            await Providers.SharedResources.FlushIdleAsync(_connectionObserver).ConfigureAwait(false);
        }
        finally
        {
            if (gateEntered)
                _mutationGate.Release();
            lock (_stateGate)
            {
                _context = null;
                _storageConfig = null;
                _localConfig = null;
                _persistedLocalConfig = null;
                _runtimeLocalOverrides.Clear();
                _enabled = false;
                _state = LifecycleState.Stopped;
            }
        }
    }
}
