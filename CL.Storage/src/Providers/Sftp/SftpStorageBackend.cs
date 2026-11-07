using System.Diagnostics.CodeAnalysis;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CL.Storage.Providers.Local;
using CodeLogic.Core.Results;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;
using Renci.SshNet.Sftp;

namespace CL.Storage.Providers.Sftp;

/// <summary>Root-scoped storage over SSH File Transfer Protocol.</summary>
public sealed partial class SftpStorageBackend : IStorageBackend, IStorageAttributeService, IStorageAppendService, IStorageCommandService, IStorageSpaceService, IStorageDiagnosticsSource, IStorageRestoringReplace
{
    private static readonly StorageCapabilities SftpCapabilities = new(
        StorageFeature.PhysicalDirectories |
        StorageFeature.FileCopy |
        StorageFeature.FileMove |
        StorageFeature.DirectoryMove |
        StorageFeature.RelayedCopy |
        StorageFeature.ServerSideMove |
        // A rename on the server (SFTP rename) moves a whole tree in one step,
        // so folder moves no longer fall back to copy-then-delete through the client.
        StorageFeature.AtomicMove |
        StorageFeature.RangeReads |
        StorageFeature.SpaceInfo |
        StorageFeature.Append |
        StorageFeature.Links |
        StorageFeature.Permissions |
        StorageFeature.Ownership |
        StorageFeature.SetTimestamps |
        StorageFeature.CreateLinks);

    private readonly ProviderClientPool<SftpClient> _clients;
    private readonly IPoolHandle<SftpClient> _pool;
    private readonly Action? _sessionOpened;
    private readonly RemotePathResolver _paths;
    private readonly long _maxBufferedDownloadBytes;
    private readonly ProviderRetryPolicy _retry;
    private readonly IStorageConnectionObserver? _observer;

    /// <summary>Initializes a backend that leases SFTP clients from a factory.</summary>
    /// <param name="connectionId">Unique connection ID exposed by the storage registry.</param>
    /// <param name="clientFactory">Factory that creates configured SFTP clients.</param>
    /// <param name="root">Optional remote directory mounted as the connection root.</param>
    /// <param name="maxBufferedDownloadBytes">Maximum size accepted by buffered download helpers.</param>
    /// <param name="session">Session pool limits; defaults when omitted.</param>
    /// <param name="retry">Transient-failure retry policy; defaults when omitted.</param>
    public SftpStorageBackend(
        string connectionId,
        Func<SftpClient> clientFactory,
        string? root = null,
        long maxBufferedDownloadBytes = 67_108_864,
        StorageSessionConfig? session = null,
        StorageRetryConfig? retry = null)
        : this(connectionId, clientFactory, root, maxBufferedDownloadBytes, session, retry, observer: null)
    {
    }

    internal SftpStorageBackend(
        string connectionId,
        Func<SftpClient> clientFactory,
        string? root,
        long maxBufferedDownloadBytes,
        StorageSessionConfig? session,
        StorageRetryConfig? retry,
        IStorageConnectionObserver? observer)
        : this(connectionId, new OwnedPool<SftpClient>(CreatePool(clientFactory, session)), root, maxBufferedDownloadBytes, retry, observer)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
    }

    internal SftpStorageBackend(
        string connectionId,
        IPoolHandle<SftpClient> pool,
        string? root,
        long maxBufferedDownloadBytes,
        StorageRetryConfig? retry,
        IStorageConnectionObserver? observer)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) throw new ArgumentException("Connection ID is required.", nameof(connectionId));
        if (maxBufferedDownloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBufferedDownloadBytes));
        ConnectionId = connectionId;
        _observer = observer;
        _pool = pool;
        _clients = pool.Pool;
        if (observer is not null)
        {
            // Reported per rent, not through the pool's event: a shared pool serves other registrations too.
            _sessionOpened = () => observer.SessionOpened(connectionId, StorageProvider.Sftp);
        }
        _retry = new ProviderRetryPolicy(retry, connectionId, StorageProvider.Sftp, observer);
        _paths = new RemotePathResolver(root);
        _maxBufferedDownloadBytes = maxBufferedDownloadBytes;
    }
}
