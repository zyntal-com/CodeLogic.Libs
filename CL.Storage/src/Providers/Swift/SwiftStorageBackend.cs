using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers.Swift;

/// <summary>Root-scoped storage over the OpenStack Swift HTTP API.</summary>
/// <remarks>
/// Swift enforces <c>If-None-Match: *</c> on an upload, but ignores <c>If-Match</c> on uploads and deletes, and
/// applies conditional headers on a <c>COPY</c> to the source it reads. So create-only uploads are atomic, while
/// conditional replaces and deletes and create-only copies are checked immediately before the request that
/// commits; a copy is pinned to the source version it read.
/// </remarks>
public sealed partial class SwiftStorageBackend : IStorageBackend, IStorageMetadataService, IStorageChecksumService, IStorageConditionEnforcementSource
{
    private static readonly StorageCapabilities SwiftCapabilities = new(
        StorageFeature.VirtualDirectories |
        StorageFeature.FileCopy |
        StorageFeature.DirectoryCopy |
        StorageFeature.FileMove |
        StorageFeature.DirectoryMove |
        StorageFeature.ServerSideCopy |
        StorageFeature.ServerSideMove |
        StorageFeature.RangeReads |
        StorageFeature.Checksums |
        StorageFeature.MetadataRead |
        StorageFeature.MetadataWrite |
        // Only If-None-Match on an upload is enforced; If-Match on PUT and DELETE is ignored by the server, so
        // replaces and deletes with a condition are checked by the caller (the library stages and checks them).
        StorageFeature.ConditionalCreate |
        StorageFeature.AtomicReplace |
        StorageFeature.ServerPagination,
        new StorageLimits { MaxPageSize = 10_000 });
    private readonly HttpClient _client;
    private readonly SwiftConnectionConfig _configuration;
    private readonly SemaphoreSlim _authenticationGate = new(1, 1);
    private readonly string _prefix;
    private readonly bool _ownsClient;
    private readonly long _maxBufferedDownloadBytes;
    private string? _token;
    private string? _storageUrl;
    private DateTimeOffset _tokenExpiresAt;
    private int _disposed;

    /// <summary>Initializes a backend over an OpenStack Swift HTTP connection.</summary>
    /// <param name="connectionId">Unique connection ID exposed by the storage registry.</param>
    /// <param name="client">HTTP client used for Swift requests.</param>
    /// <param name="configuration">Container, authentication, and mount configuration.</param>
    /// <param name="ownsClient">Whether disposal of this backend also disposes the HTTP client.</param>
    /// <param name="maxBufferedDownloadBytes">Maximum size accepted by buffered download helpers.</param>
    public SwiftStorageBackend(
        string connectionId,
        HttpClient client,
        SwiftConnectionConfig configuration,
        bool ownsClient = false,
        long maxBufferedDownloadBytes = 67_108_864)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) throw new ArgumentException("Connection ID is required.", nameof(connectionId));
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(configuration);
        if (maxBufferedDownloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBufferedDownloadBytes));
        var normalized = StoragePath.Normalize(configuration.Prefix ?? string.Empty);
        if (normalized.IsFailure) throw new ArgumentException(normalized.Error!.Message, nameof(configuration));
        ConnectionId = connectionId;
        _client = client;
        _configuration = configuration;
        Root = normalized.Value!;
        _prefix = Root.Length == 0 ? string.Empty : Root + "/";
        _ownsClient = ownsClient;
        _maxBufferedDownloadBytes = maxBufferedDownloadBytes;
        if (configuration.AuthenticationMode == SwiftAuthenticationMode.StaticToken)
        {
            _token = configuration.Token;
            _storageUrl = configuration.StorageUrl?.TrimEnd('/');
            _tokenExpiresAt = DateTimeOffset.MaxValue;
        }
    }
}
