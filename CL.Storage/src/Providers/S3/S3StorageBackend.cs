using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers.S3;

/// <summary>Root-scoped storage over Amazon S3 or an S3-compatible service.</summary>
public sealed partial class S3StorageBackend :
    IStorageBackend,
    IStorageMetadataService,
    IStorageTagService,
    IStorageSignedUrlService,
    IStorageVersionService,
    IStorageChecksumService,
    IStorageConditionEnforcementSource
{
    private static readonly StorageCapabilities S3Capabilities = new(
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
        StorageFeature.Tags |
        StorageFeature.ConditionalCreate |
        StorageFeature.ConditionalUpdate |
        StorageFeature.ConditionalDelete |
        StorageFeature.AtomicReplace |
        StorageFeature.ServerPagination |
        StorageFeature.MultipartUpload |
        StorageFeature.SignedReadUrls |
        StorageFeature.SignedWriteUrls |
        StorageFeature.Versioning);
    private readonly IAmazonS3 _client;
    private readonly StorageCapabilities _capabilities;
    private readonly StorageCapabilities _unconditionalCapabilities;
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private S3ConditionProbe? _conditionProbe;
    private readonly string _bucket;
    private readonly string _keyPrefix;
    private readonly bool _ownsClient;
    private readonly bool _disablePayloadSigning;
    private readonly bool _disableChecksumValidation;
    private readonly int _multipartPartSizeBytes;
    private readonly long _multipartThresholdBytes;
    private readonly long _maxBufferedDownloadBytes;
    private int _disposed;

    /// <summary>Initializes a backend over an Amazon S3 or S3-compatible client.</summary>
    /// <param name="connectionId">Unique connection ID exposed by the storage registry.</param>
    /// <param name="client">S3 client used for all operations.</param>
    /// <param name="bucket">Bucket mounted by this connection.</param>
    /// <param name="prefix">Optional key prefix mounted as the connection root.</param>
    /// <param name="ownsClient">Whether disposal of this backend also disposes the client.</param>
    /// <param name="maxBufferedDownloadBytes">Maximum size accepted by buffered download helpers.</param>
    /// <param name="disablePayloadSigning">Whether compatible endpoints receive unsigned request payloads.</param>
    /// <param name="disableDefaultChecksumValidation">Whether SDK default response checksum validation is disabled.</param>
    /// <param name="multipartPartSizeBytes">Part size used for multipart uploads.</param>
    /// <param name="multipartThresholdBytes">Content size at which multipart upload begins.</param>
    public S3StorageBackend(
        string connectionId,
        IAmazonS3 client,
        string bucket,
        string? prefix = null,
        bool ownsClient = false,
        long maxBufferedDownloadBytes = 67_108_864,
        bool disablePayloadSigning = false,
        bool disableDefaultChecksumValidation = false,
        int multipartPartSizeBytes = 16 * 1024 * 1024,
        long multipartThresholdBytes = 64L * 1024 * 1024)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) throw new ArgumentException("Connection ID is required.", nameof(connectionId));
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentException("Bucket is required.", nameof(bucket));
        if (maxBufferedDownloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBufferedDownloadBytes));
        if (multipartPartSizeBytes is < 5 * 1024 * 1024 or > 512 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(multipartPartSizeBytes));
        if (multipartThresholdBytes < multipartPartSizeBytes)
            throw new ArgumentOutOfRangeException(nameof(multipartThresholdBytes));
        ArgumentNullException.ThrowIfNull(client);

        var normalized = StoragePath.Normalize(prefix ?? string.Empty);
        if (normalized.IsFailure) throw new ArgumentException(normalized.Error!.Message, nameof(prefix));
        ConnectionId = connectionId;
        _client = client;
        _bucket = bucket;
        Root = normalized.Value!;
        _keyPrefix = Root.Length == 0 ? string.Empty : Root + "/";
        _ownsClient = ownsClient;
        _maxBufferedDownloadBytes = maxBufferedDownloadBytes;
        _disablePayloadSigning = disablePayloadSigning;
        _disableChecksumValidation = disableDefaultChecksumValidation;
        _multipartPartSizeBytes = multipartPartSizeBytes;
        _multipartThresholdBytes = multipartThresholdBytes;
        _capabilities = new StorageCapabilities(S3Capabilities.Features, new StorageLimits
        {
            MaxPageSize = 1_000,
            MaxObjectBytes = checked((long)multipartPartSizeBytes * 10_000),
            MaxSingleUploadBytes = 5L * 1024 * 1024 * 1024,
            MaxMetadataBytes = 2 * 1024,
            MaxTags = 10,
            PreferredUploadPartBytes = multipartPartSizeBytes
        });
        _unconditionalCapabilities = new StorageCapabilities(
            _capabilities.Features & ~(StorageFeature.ConditionalCreate | StorageFeature.ConditionalUpdate | StorageFeature.ConditionalDelete),
            _capabilities.Limits);
    }

    /// <summary>
    /// Gets whether the server enforces request conditions. With <see cref="S3ConditionalRequestSupport.NotEnforced"/>
    /// no conditional headers are sent, the conditional capabilities are not declared, and conditions are checked
    /// immediately before the request that commits.
    /// </summary>
    public S3ConditionalRequestSupport ConditionalRequests { get; init; } = S3ConditionalRequestSupport.Auto;

    /// <summary>Objects larger than this are copied part by part: S3 copies at most 5 GiB in one request.</summary>
    internal long MultipartCopyThresholdBytes { get; init; } = 5L * 1024 * 1024 * 1024;

    /// <summary>How many parts of a large copy run at once.</summary>
    internal const int MultipartCopyConcurrency = 4;

    /// <summary>The smallest part a large server-side copy uses (each part is a request; the data never passes through the client).</summary>
    internal long MultipartCopyMinPartBytes { get; init; } = 128L * 1024 * 1024;

    /// <summary>How long after a probe that could not finish the next one may run; doubled for each further failure, up to 32 times.</summary>
    internal TimeSpan InconclusiveProbeBackoff { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The clock the probe back-off is measured with.</summary>
    internal Func<DateTimeOffset> Clock { get; init; } = static () => DateTimeOffset.UtcNow;

    private int _inconclusiveProbes;
}
