using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;
using Google;
using Google.Cloud.Storage.V1;
using GcsObject = Google.Apis.Storage.v1.Data.Object;

namespace CL.Storage.Providers.GoogleCloud;

/// <summary>Root-scoped storage over Google Cloud Storage.</summary>
public sealed partial class GoogleCloudStorageBackend :
    IStorageBackend,
    IStorageMetadataService,
    IStorageSignedUrlService,
    IStorageVersionService,
    IStorageChecksumService
{
    private static readonly StorageCapabilities GcsCapabilities = new(
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
        StorageFeature.ConditionalCreate |
        StorageFeature.ConditionalUpdate |
        StorageFeature.ConditionalDelete |
        StorageFeature.AtomicReplace |
        StorageFeature.ServerPagination |
        StorageFeature.Versioning,
        new StorageLimits { MaxPageSize = 1_000, MaxObjectBytes = 5_497_558_138_880, MaxMetadataBytes = 8 * 1024 });
    private readonly StorageClient _client;
    private readonly UrlSigner? _urlSigner;
    private readonly StorageCapabilities _capabilities;
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly int _uploadChunkSize;
    private readonly bool _ownsClient;
    private readonly long _maxBufferedDownloadBytes;
    private int _disposed;

    /// <summary>Initializes a backend over a Google Cloud Storage client and bucket.</summary>
    /// <param name="connectionId">Unique connection ID exposed by the storage registry.</param>
    /// <param name="client">Google Cloud Storage client used for operations.</param>
    /// <param name="bucket">Bucket mounted by this connection.</param>
    /// <param name="prefix">Optional object prefix mounted as the connection root.</param>
    /// <param name="uploadChunkSizeBytes">Chunk size used for resumable uploads.</param>
    /// <param name="maxBufferedDownloadBytes">Maximum size accepted by buffered download helpers.</param>
    /// <param name="ownsClient">Whether disposal of this backend also disposes the client.</param>
    public GoogleCloudStorageBackend(
        string connectionId,
        StorageClient client,
        string bucket,
        string? prefix = null,
        int uploadChunkSizeBytes = 10 * 1024 * 1024,
        long maxBufferedDownloadBytes = 67_108_864,
        bool ownsClient = false)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) throw new ArgumentException("Connection ID is required.", nameof(connectionId));
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentException("Bucket is required.", nameof(bucket));
        ArgumentNullException.ThrowIfNull(client);
        if (uploadChunkSizeBytes <= 0 || uploadChunkSizeBytes % (256 * 1024) != 0) throw new ArgumentOutOfRangeException(nameof(uploadChunkSizeBytes));
        if (maxBufferedDownloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBufferedDownloadBytes));
        var normalized = StoragePath.Normalize(prefix ?? string.Empty);
        if (normalized.IsFailure) throw new ArgumentException(normalized.Error!.Message, nameof(prefix));
        ConnectionId = connectionId;
        _client = client;
        // Credentials that cannot sign (user credentials, anonymous emulator clients) disable signed URLs.
        try { _urlSigner = client.CreateUrlSigner(); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException) { }
        var features = GcsCapabilities.Features;
        if (_urlSigner is not null)
            features |= StorageFeature.SignedReadUrls | StorageFeature.SignedWriteUrls;
        _capabilities = new StorageCapabilities(features, GcsCapabilities.Limits);
        _bucket = bucket;
        Root = normalized.Value!;
        _prefix = Root.Length == 0 ? string.Empty : Root + "/";
        _uploadChunkSize = uploadChunkSizeBytes;
        _maxBufferedDownloadBytes = maxBufferedDownloadBytes;
        _ownsClient = ownsClient;
    }
}
