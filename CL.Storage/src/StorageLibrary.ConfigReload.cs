using System.Collections.Concurrent;
using CL.Storage.Configuration;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Results;
using CodeLogic.Framework.Libraries;

namespace CL.Storage;

public sealed partial class StorageLibrary : IConfigReloadable
{
    // Set while the library persists its own change (AddOrUpdateConnectionAsync, RemoveConnectionAsync).
    // Those calls hold the mutation gate and update the registry themselves, so the config-change
    // notification their save triggers must be skipped — applying it would wait on that same gate.
    private readonly AsyncLocal<bool> _savingOwnConfig = new();

    // Connections whose last config change failed its health check: they still run on the previous
    // backend, so the next apply retries them even when their settings did not change again.
    private readonly ConcurrentDictionary<string, byte> _unappliedConnections = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Applies a config change made outside the library (admin UI, reload, file watcher) without a
    /// restart, touching only the connections that changed. A changed or added connection is built
    /// and health-checked first, then swapped in; the backend it replaces finishes its in-flight
    /// operations before it is disposed. Removed or disabled connections are retired the same way,
    /// and untouched connections keep running. A replacement that fails its health check leaves the
    /// current backend in service and is reported (the change stays saved). The default connection
    /// cannot be removed or disabled, and turning the library on or off requires a restart.
    /// </summary>
    public async Task OnConfigChangedAsync(LibraryContext context, ConfigChangedEventArgs change, CancellationToken cancellationToken)
    {
        if (_runtimeOnly || _savingOwnConfig.Value) return;

        switch (change.NewValue)
        {
            case StorageConfig storage:
                await ApplyStorageSettingsAsync(storage, cancellationToken).ConfigureAwait(false);
                break;

            case LocalStorageConfig local:
                await ApplyLocalConnectionsAsync(local, cancellationToken).ConfigureAwait(false);
                break;

            case ProviderStorageConfigBase provider:
                await ApplyProviderConnectionsAsync(change.OldValue as ProviderStorageConfigBase, provider, cancellationToken)
                    .ConfigureAwait(false);
                break;
        }
    }

    private async Task ApplyStorageSettingsAsync(StorageConfig current, CancellationToken ct)
    {
        await _mutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var runtime = CaptureRuntimeSnapshot();
            var previous = runtime.StorageConfig;

            if (previous.Enabled != current.Enabled)
                throw new InvalidOperationException("Turning the storage library on or off requires a restart.");

            BackendEntry[] entries;
            lock (_registryGate)
            {
                if (current.Enabled && !_registry.ContainsKey(current.DefaultConnection))
                    throw new InvalidOperationException(
                        $"The default storage connection '{current.DefaultConnection}' is not an enabled connection.");
                entries = [.. _registry.Values];
            }

            var limits = StorageTransferPipeline.LibraryLimits(
                current.MaxTotalUploadBytesPerSecond,
                current.MaxTotalDownloadBytesPerSecond);
            lock (_stateGate)
            {
                _storageConfig = current;
                _libraryLimits = limits;
            }

            foreach (var entry in entries)
                StorageTransferPipeline.SetLibraryLimits(entry.Backend, limits);

            if (previous.MaxBufferedDownloadBytes != current.MaxBufferedDownloadBytes)
                runtime.Logger.Warning(
                    "Storage MaxBufferedDownloadBytes applies to connections created or changed from now on; " +
                    "restart to apply it to the existing connections.");
            runtime.Logger.Info("Storage settings applied");
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private Task ApplyLocalConnectionsAsync(LocalStorageConfig persisted, CancellationToken ct)
    {
        var runtime = CaptureRuntimeSnapshot();
        ValidateGlobalIds(runtime.Context);

        // Connections changed at runtime with persist: false stay in effect on top of the file.
        var effective = CloneLocalConfig(persisted);
        ApplyRuntimeLocalOverrides(effective);

        var diff = ConfigDiff.Keyed(_localConfig!.Connections, effective.Connections, StringComparer.OrdinalIgnoreCase);
        return ApplyConnectionChangesAsync(
            "storage.local",
            diff,
            connection => connection.Enabled,
            BuildHealthyLocalReplacementAsync,
            (id, connection) => new StorageConnectionInfo(id, StorageProvider.Local, connection.RootPath, connection.Enabled),
            commit: () =>
            {
                // Recomputed under the gate: a runtime override registered meanwhile is kept.
                _persistedLocalConfig = CloneLocalConfig(persisted);
                var latest = CloneLocalConfig(persisted);
                ApplyRuntimeLocalOverrides(latest);
                _localConfig = latest;
            },
            runtime,
            ct);
    }

    private Task ApplyProviderConnectionsAsync(
        ProviderStorageConfigBase? previous,
        ProviderStorageConfigBase current,
        CancellationToken ct)
    {
        var runtime = CaptureRuntimeSnapshot();
        ValidateGlobalIds(runtime.Context);

        var before = ToConnectionMap(previous);
        var after = ToConnectionMap(current);
        var diff = ConfigDiff.Keyed(before, after, StringComparer.OrdinalIgnoreCase);
        if (!HasWork(diff))
            return Task.CompletedTask;

        var connectionType = after.Values.Concat(before.Values).First().GetType();
        var descriptor = DescribeProviderConnection(connectionType)
            ?? throw new NotSupportedException($"Provider connection type '{connectionType.FullName}' is not supported.");

        return ApplyConnectionChangesAsync(
            descriptor.Section,
            diff,
            connection => connection.Enabled,
            (id, connection, snapshot, token) => BuildHealthyProviderReplacementAsync(
                id, CloneProviderConnection(connection), descriptor, snapshot, token),
            (id, connection) => DescribeConnection(id, descriptor.Provider, connection),
            commit: null,
            runtime,
            ct);
    }

    /// <summary>
    /// Builds and health-checks every changed or added connection outside the mutation gate, then
    /// swaps the successful ones (and removals) into the registry in one step and retires what they
    /// replaced. Failed replacements leave the current backend untouched and are reported together.
    /// </summary>
    private async Task ApplyConnectionChangesAsync<TConnection>(
        string section,
        KeyedDiff<TConnection> diff,
        Func<TConnection, bool> isEnabled,
        Func<string, TConnection, RuntimeSnapshot, CancellationToken, Task<Result<BackendEntry>>> build,
        Func<string, TConnection, StorageConnectionInfo> describe,
        Action? commit,
        RuntimeSnapshot runtime,
        CancellationToken ct)
    {
        if (!HasWork(diff)) return;

        // A disabled library has no backends; the change is picked up when it is enabled (restart).
        if (!runtime.Enabled)
        {
            await _mutationGate.WaitAsync(ct).ConfigureAwait(false);
            try { commit?.Invoke(); }
            finally { _mutationGate.Release(); }
            return;
        }

        var defaultId = runtime.DefaultConnection;
        // A connection whose last change failed is still on its old backend although the settings
        // already match: retry it, so a reload after the cause went away applies it.
        var retry = diff.Unchanged.Where(pair => _unappliedConnections.ContainsKey(pair.Key)).ToList();
        var targets = diff.Changed.Select(pair => (Id: pair.Key, Config: pair.Value.New))
            .Concat(diff.Added.Select(pair => (Id: pair.Key, Config: pair.Value)))
            .Concat(retry.Select(pair => (Id: pair.Key, Config: pair.Value)))
            .ToList();
        if (diff.Removed.Keys.Any(id => IsDefault(id)) || targets.Any(t => IsDefault(t.Id) && !isEnabled(t.Config)))
            throw new InvalidOperationException(
                $"'{defaultId}' is the default storage connection; point storage.defaultConnection at another " +
                "connection before removing or disabling it.");

        var prepared = new List<(string Id, TConnection Config, BackendEntry? Entry)>();
        var failures = new List<string>();
        try
        {
            foreach (var (id, connection) in targets)
            {
                if (!isEnabled(connection))
                {
                    prepared.Add((id, connection, null));
                    continue;
                }

                var built = await build(id, connection, runtime, ct).ConfigureAwait(false);
                if (built.IsSuccess)
                    prepared.Add((id, connection, built.Value!));
                else
                {
                    failures.Add($"'{id}' ({built.Error?.Message})");
                    _unappliedConnections[id] = 0;
                }
            }
        }
        catch
        {
            await DisposeUnpublishedAsync(prepared).ConfigureAwait(false);
            throw;
        }

        var retired = new List<BackendEntry>();
        await _mutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOperational();
            commit?.Invoke();
            lock (_registryGate)
            {
                foreach (var id in diff.Removed.Keys)
                {
                    _unappliedConnections.TryRemove(id, out _);
                    if (_registry.Remove(id, out var removed))
                        retired.Add(removed);
                    _connectionInfos.Remove(id);
                    _health.TryRemove(id, out _);
                }

                foreach (var (id, connection, entry) in prepared)
                {
                    _unappliedConnections.TryRemove(id, out _);
                    if (_registry.Remove(id, out var previous))
                        retired.Add(previous);
                    if (entry is not null)
                        _registry[id] = entry;
                    _connectionInfos[id] = describe(id, connection);
                    _health.TryRemove(id, out _);
                }
            }
            prepared.Clear();
        }
        finally
        {
            _mutationGate.Release();
            await DisposeUnpublishedAsync(prepared).ConfigureAwait(false);
        }

        foreach (var entry in retired)
            await entry.RetireAndDisposeAsync().ConfigureAwait(false);

        runtime.Logger.Info(
            $"Storage section '{section}' applied: {diff.Added.Count} added, {diff.Changed.Count} changed, " +
            $"{diff.Removed.Count} removed, {retry.Count} retried, {diff.Unchanged.Count - retry.Count} untouched");

        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"Storage connection(s) {string.Join(", ", failures)} kept their current backend: the new settings failed the health check.");

        bool IsDefault(string id) => string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase);
    }

    private bool HasWork<TConnection>(KeyedDiff<TConnection> diff) =>
        diff.HasChanges || diff.Unchanged.Keys.Any(_unappliedConnections.ContainsKey);

    private static async Task DisposeUnpublishedAsync<TConnection>(List<(string Id, TConnection Config, BackendEntry? Entry)> prepared)
    {
        foreach (var (_, _, entry) in prepared)
        {
            if (entry is not null)
                await entry.RetireAndDisposeAsync().ConfigureAwait(false);
        }
    }

    private void ValidateGlobalIds(LibraryContext context) =>
        ValidateGlobalIds(
            ReadConfig<LocalStorageConfig>(context),
            ReadConfig<S3StorageConfig>(context),
            ReadConfig<FtpStorageConfig>(context),
            ReadConfig<SftpStorageConfig>(context),
            ReadConfig<WebDavStorageConfig>(context),
            ReadConfig<AzureStorageConfig>(context),
            ReadConfig<GoogleCloudStorageConfig>(context),
            ReadConfig<SwiftStorageConfig>(context));

    private static Dictionary<string, StorageConnectionConfigBase> ToConnectionMap(ProviderStorageConfigBase? config) =>
        config?.EnumerateConnections().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
        ?? new Dictionary<string, StorageConnectionConfigBase>(StringComparer.OrdinalIgnoreCase);
}
