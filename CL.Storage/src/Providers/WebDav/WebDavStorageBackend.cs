using System.Diagnostics.CodeAnalysis;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CL.Storage.Providers.Local;
using CodeLogic.Core.Results;
using WebDAVClient;
using WebDAVClient.Helpers;
using WebDAVClient.Model;

namespace CL.Storage.Providers.WebDav;

/// <summary>Root-scoped storage over a WebDAV endpoint.</summary>
public sealed partial class WebDavStorageBackend : IStorageBackend, IStorageMetadataService, IStorageDiagnosticsSource
{
    private static readonly StorageCapabilities WebDavCapabilities = new(
        StorageFeature.PhysicalDirectories |
        StorageFeature.FileCopy |
        StorageFeature.DirectoryCopy |
        StorageFeature.FileMove |
        StorageFeature.DirectoryMove |
        StorageFeature.ServerSideCopy |
        StorageFeature.ServerSideMove |
        // Not AtomicMove: RFC 4918 lets a server move a collection member by member and answer 207 Multi-Status
        // when some members failed, leaving the tree split between source and destination.
        StorageFeature.ConditionalCreate |
        StorageFeature.AtomicReplace |
        StorageFeature.MetadataRead);

    private readonly IClient _client;
    private readonly RemotePathResolver _paths;
    private readonly string _basePath;
    private readonly bool _ownsClient;
    private readonly long _maxBufferedDownloadBytes;
    private readonly ProviderRetryPolicy _retry;
    private readonly HttpClient? _http;
    private readonly Uri? _endpoint;
    private int _disposed;

    /// <summary>Initializes a backend over a WebDAV client.</summary>
    /// <param name="connectionId">Unique connection ID exposed by the storage registry.</param>
    /// <param name="client">Configured WebDAV client used for operations.</param>
    /// <param name="root">Optional provider-neutral root label.</param>
    /// <param name="basePath">Optional remote base path prepended to requests.</param>
    /// <param name="ownsClient">Whether disposal of this backend also disposes the client.</param>
    /// <param name="maxBufferedDownloadBytes">Maximum size accepted by buffered download helpers.</param>
    /// <param name="retry">Transient-failure retry policy; defaults when omitted.</param>
    public WebDavStorageBackend(
        string connectionId,
        IClient client,
        string? root = null,
        string? basePath = null,
        bool ownsClient = false,
        long maxBufferedDownloadBytes = 67_108_864,
        StorageRetryConfig? retry = null)
        : this(connectionId, client, root, basePath, ownsClient, maxBufferedDownloadBytes, retry, observer: null)
    {
    }

    internal WebDavStorageBackend(
        string connectionId,
        IClient client,
        string? root,
        string? basePath,
        bool ownsClient,
        long maxBufferedDownloadBytes,
        StorageRetryConfig? retry,
        IStorageConnectionObserver? observer,
        HttpClient? http = null,
        Uri? endpoint = null)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) throw new ArgumentException("Connection ID is required.", nameof(connectionId));
        ArgumentNullException.ThrowIfNull(client);
        if (maxBufferedDownloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBufferedDownloadBytes));
        ConnectionId = connectionId;
        _retry = new ProviderRetryPolicy(retry, connectionId, StorageProvider.WebDav, observer);
        _retry.Enrich = (error, attempt) => TlsDiagnosis.Enrich(error, Identity, attempt);
        _http = http;
        _endpoint = endpoint;
        _client = client;
        _paths = new RemotePathResolver(root);
        _basePath = NormalizeBasePath(basePath);
        _ownsClient = ownsClient;
        _maxBufferedDownloadBytes = maxBufferedDownloadBytes;
    }

    private const string LockBody =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><D:lockinfo xmlns:D=\"DAV:\"><D:lockscope><D:exclusive/></D:lockscope>" +
        "<D:locktype><D:write/></D:locktype><D:owner>CL.Storage</D:owner></D:lockinfo>";

    private static readonly System.Text.RegularExpressions.Regex StatusInMessage =
        new(@"Status Code:\s*([A-Za-z]+|\d{3})", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
