using System.Diagnostics.CodeAnalysis;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CL.Storage.Providers.Local;
using CodeLogic.Core.Results;
using FluentFTP;
using FluentFTP.Exceptions;

namespace CL.Storage.Providers.Ftp;

/// <summary>Root-scoped storage over FTP, explicit FTPS, or implicit FTPS.</summary>
public sealed partial class FtpStorageBackend : IStorageBackend, IStorageAttributeService, IStorageChecksumService, IStorageAppendService, IStorageCommandService, IStorageSpaceService, IStorageDiagnosticsSource, IStorageRestoringReplace
{
    private static readonly StorageCapabilities FtpCapabilities = new(
        StorageFeature.PhysicalDirectories |
        StorageFeature.FileCopy |
        StorageFeature.FileMove |
        StorageFeature.DirectoryMove |
        StorageFeature.RelayedCopy |
        StorageFeature.ServerSideMove |
        // A rename on the server (RNFR/RNTO) moves a whole tree in one step,
        // so folder moves no longer fall back to copy-then-delete through the client.
        StorageFeature.AtomicMove |
        StorageFeature.RangeReads |
        StorageFeature.SpaceInfo |
        StorageFeature.Append |
        StorageFeature.Checksums |
        StorageFeature.Links |
        StorageFeature.Permissions |
        StorageFeature.SetTimestamps |
        StorageFeature.ReadLinks);

    private readonly ProviderClientPool<AsyncFtpClient> _clients;
    private readonly IPoolHandle<AsyncFtpClient> _pool;
    private readonly Action? _sessionOpened;
    private readonly RemotePathResolver _paths;
    private readonly long _maxBufferedDownloadBytes;
    private readonly ProviderRetryPolicy _retry;
    private readonly IStorageConnectionObserver? _observer;

    /// <summary>Initializes a backend that leases FTP clients from a factory.</summary>
    /// <param name="connectionId">Unique connection ID exposed by the storage registry.</param>
    /// <param name="clientFactory">Factory that creates configured FTP clients.</param>
    /// <param name="root">Optional remote directory mounted as the connection root.</param>
    /// <param name="maxBufferedDownloadBytes">Maximum size accepted by buffered download helpers.</param>
    /// <param name="session">Session pool limits; defaults when omitted.</param>
    /// <param name="retry">Transient-failure retry policy; defaults when omitted.</param>
    public FtpStorageBackend(
        string connectionId,
        Func<AsyncFtpClient> clientFactory,
        string? root = null,
        long maxBufferedDownloadBytes = 67_108_864,
        StorageSessionConfig? session = null,
        StorageRetryConfig? retry = null)
        : this(connectionId, clientFactory, root, maxBufferedDownloadBytes, session, retry, observer: null)
    {
    }

    internal FtpStorageBackend(
        string connectionId,
        Func<AsyncFtpClient> clientFactory,
        string? root,
        long maxBufferedDownloadBytes,
        StorageSessionConfig? session,
        StorageRetryConfig? retry,
        IStorageConnectionObserver? observer,
        Func<AsyncFtpClient, CancellationToken, Task>? afterConnect = null)
        : this(connectionId, new OwnedPool<AsyncFtpClient>(CreatePool(clientFactory, session, afterConnect)), root, maxBufferedDownloadBytes, retry, observer)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
    }

    internal FtpStorageBackend(
        string connectionId,
        IPoolHandle<AsyncFtpClient> pool,
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
            _sessionOpened = () => observer.SessionOpened(connectionId, StorageProvider.Ftp);
        }
        _retry = new ProviderRetryPolicy(retry, connectionId, StorageProvider.Ftp, observer);
        _retry.Enrich = (error, attempt) => TlsDiagnosis.Enrich(error, Identity, attempt);
        _paths = new RemotePathResolver(root);
        _maxBufferedDownloadBytes = maxBufferedDownloadBytes;
    }

    private static readonly System.Text.RegularExpressions.Regex ListedTimeOfDay = new(
        @"(?<![\d:])\d{1,2}:\d{2}(?![\d:])",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
}
