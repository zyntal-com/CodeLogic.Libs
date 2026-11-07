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

    private static void ValidateGlobalIds(
        LocalStorageConfig local,
        S3StorageConfig s3,
        FtpStorageConfig ftp,
        SftpStorageConfig sftp,
        WebDavStorageConfig webDav,
        AzureStorageConfig azure,
        GoogleCloudStorageConfig gcs,
        SwiftStorageConfig swift)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Add(local.Connections.Keys, "storage.local");
        Add(s3.Connections.Keys, "storage.s3");
        Add(ftp.Connections.Keys, "storage.ftp");
        Add(sftp.Connections.Keys, "storage.sftp");
        Add(webDav.Connections.Keys, "storage.webdav");
        Add(azure.Connections.Keys, "storage.azure");
        Add(gcs.Connections.Keys, "storage.gcs");
        Add(swift.Connections.Keys, "storage.swift");
        return;

        void Add(IEnumerable<string> connectionIds, string section)
        {
            foreach (var id in connectionIds)
            {
                if (ids.TryGetValue(id, out var existing))
                    throw new InvalidOperationException(
                        $"Duplicate storage connection ID '{id}' appears in '{existing}' and '{section}'. IDs are case-insensitive.");
                ids.Add(id, section);
            }
        }
    }

    private void AddProviderConnections<TConnection>(
        IDictionary<string, BackendEntry> entries,
        IDictionary<string, StorageConnectionInfo> infos,
        ProviderStorageConfigBase<TConnection> config,
        StorageProvider provider,
        long maxBufferedDownloadBytes,
        TransferLimits libraryLimits)
        where TConnection : StorageConnectionConfigBase
    {
        _factories.TryGetValue(typeof(TConnection), out var factory);
        foreach (var (id, connection) in config.Connections)
        {
            infos.Add(id, DescribeConnection(id, provider, connection));
            if (!connection.Enabled)
                continue;
            if (factory is null)
                throw new InvalidOperationException($"The {provider} provider factory is not registered.");
            entries.Add(id, new BackendEntry(
                WithLimits(factory.Create(id, connection, maxBufferedDownloadBytes, _connectionObserver), connection, libraryLimits),
                ownsBackend: true));
        }
    }

    private static StorageConnectionInfo DescribeConnection(string id, StorageProvider provider, StorageConnectionConfigBase connection)
    {
        var (host, port, security) = StorageEndpoints.Describe(connection);
        return new StorageConnectionInfo(id, provider, connection.MountRoot, connection.Enabled)
        {
            Host = host,
            Port = port,
            Security = security
        };
    }

    /// <summary>Attaches a connection's speed limits, plus the library-wide totals, to its backend.</summary>
    private static IStorageBackend WithLimits(IStorageBackend backend, StorageConnectionConfigBase configuration, TransferLimits libraryLimits)
    {
        // Pooled connections also record their session limit, so a relay that needs two sessions on one
        // connection fails at once instead of waiting for a session that never frees.
        var session = configuration switch
        {
            FtpConnectionConfig ftp => ftp.Session,
            SftpConnectionConfig sftp => sftp.Session,
            _ => null
        };
        if (session is not null)
            StorageTransferPipeline.SetSessionLimit(backend, session.MaxSessions);
        return WithLimits(backend, configuration.TransferLimits, libraryLimits);
    }

    private static IStorageBackend WithLimits(IStorageBackend backend, LocalConnectionConfig configuration, TransferLimits libraryLimits) =>
        WithLimits(backend, configuration.TransferLimits, libraryLimits);

    private static IStorageBackend WithLimits(IStorageBackend backend, StorageTransferLimitsConfig? configured, TransferLimits libraryLimits)
    {
        var limits = configured ?? new StorageTransferLimitsConfig();
        StorageTransferPipeline.SetLimits(backend, limits.MaxUploadBytesPerSecond, limits.MaxDownloadBytesPerSecond, libraryLimits);
        return backend;
    }

    private static void ValidateConnectionId(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("A storage connection ID is required.", nameof(connectionId));
    }

    private string? FindConfiguredNonLocalSection(string id, LibraryContext context)
    {
        if (Contains(ReadConfig<S3StorageConfig>(context), id)) return "storage.s3";
        if (Contains(ReadConfig<FtpStorageConfig>(context), id)) return "storage.ftp";
        if (Contains(ReadConfig<SftpStorageConfig>(context), id)) return "storage.sftp";
        if (Contains(ReadConfig<WebDavStorageConfig>(context), id)) return "storage.webdav";
        if (Contains(ReadConfig<AzureStorageConfig>(context), id)) return "storage.azure";
        if (Contains(ReadConfig<GoogleCloudStorageConfig>(context), id)) return "storage.gcs";
        if (Contains(ReadConfig<SwiftStorageConfig>(context), id)) return "storage.swift";
        return null;

        static bool Contains(ProviderStorageConfigBase providerConfig, string connectionId) =>
            providerConfig.ContainsConnection(connectionId);
    }

    private static StorageConfig CloneStorageConfig(StorageConfig source) => new()
    {
        Enabled = source.Enabled,
        DefaultConnection = source.DefaultConnection,
        HealthCheckTimeoutSeconds = source.HealthCheckTimeoutSeconds,
        MaxBufferedDownloadBytes = source.MaxBufferedDownloadBytes,
        MaxTotalUploadBytesPerSecond = source.MaxTotalUploadBytesPerSecond,
        MaxTotalDownloadBytesPerSecond = source.MaxTotalDownloadBytesPerSecond
    };

    private static LocalStorageConfig CloneLocalConfig(LocalStorageConfig source)
    {
        var clone = new LocalStorageConfig();
        foreach (var (id, connection) in source.Connections)
            clone.Connections[id] = Clone(connection);
        return clone;
    }

    private static LocalConnectionConfig Clone(LocalConnectionConfig source) => new()
    {
        Enabled = source.Enabled,
        RootPath = source.RootPath,
        FollowLinks = source.FollowLinks,
        TransferLimits = new StorageTransferLimitsConfig
        {
            MaxUploadBytesPerSecond = source.TransferLimits?.MaxUploadBytesPerSecond,
            MaxDownloadBytesPerSecond = source.TransferLimits?.MaxDownloadBytesPerSecond
        }
    };

    private void ApplyRuntimeLocalOverrides(LocalStorageConfig target)
    {
        foreach (var (id, connection) in _runtimeLocalOverrides)
        {
            RemoveKey(target.Connections, id);
            if (connection is not null)
                target.Connections[id] = Clone(connection);
        }
    }

    private static void SetLocalConnection(LocalStorageConfig target, string id, LocalConnectionConfig connection)
    {
        RemoveKey(target.Connections, id);
        target.Connections[id] = Clone(connection);
    }

    private static void RemoveKey<T>(IDictionary<string, T> dictionary, string id)
    {
        if (TryFindKey(dictionary, id, out var key))
            dictionary.Remove(key!);
    }

    private static bool TryFindKey<T>(IDictionary<string, T> dictionary, string id, out string? key)
    {
        key = dictionary.Keys.FirstOrDefault(candidate => string.Equals(candidate, id, StringComparison.OrdinalIgnoreCase));
        return key is not null;
    }

    private sealed record HealthProbe(string Id, bool Healthy, string Detail)
    {
        public StorageProvider Provider { get; init; }
        public TimeSpan Latency { get; init; }
    }
    private sealed record HealthTarget(string Id, BackendEntry.BackendOperationLease Lease);
    private readonly record struct ProviderDescriptor(StorageProvider Provider, string Section);
    private sealed record ProviderConfigMatch(ProviderStorageConfigBase Config, ProviderDescriptor Descriptor);
    private readonly record struct RuntimeSnapshot(
        LibraryContext Context,
        StorageConfig StorageConfig,
        ILogger Logger,
        bool Enabled,
        string DefaultConnection,
        long MaxBufferedDownloadBytes,
        int HealthCheckTimeoutSeconds);

    private enum LifecycleState
    {
        Created,
        Configured,
        Initialized,
        Started,
        Stopping,
        Stopped,
        Disposed
    }
}
