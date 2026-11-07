using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers.Azure;

/// <summary>Root-scoped storage over an Azure Blob container.</summary>
public sealed partial class AzureBlobStorageBackend :
    IStorageBackend,
    IStorageMetadataService,
    IStorageTagService,
    IStorageSignedUrlService,
    IStorageVersionService,
    IStorageChecksumService
{
    private static readonly StorageCapabilities AzureCapabilities = new(
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
        StorageFeature.Versioning,
        new StorageLimits
        {
            MaxPageSize = 5_000,
            MaxObjectBytes = 209_715_200_000_000,
            MaxMetadataBytes = 8 * 1024,
            MaxTags = 10
        });
    private readonly BlobContainerClient _container;
    private readonly StorageCapabilities _capabilities;
    private readonly string _prefix;
    private readonly long _maxBufferedDownloadBytes;

    /// <summary>Initializes a backend over an Azure Blob container client.</summary>
    /// <param name="connectionId">Unique connection ID exposed by the storage registry.</param>
    /// <param name="container">Azure container client used for all operations.</param>
    /// <param name="prefix">Optional blob prefix mounted as the connection root.</param>
    /// <param name="maxBufferedDownloadBytes">Maximum size accepted by buffered download helpers.</param>
    public AzureBlobStorageBackend(
        string connectionId,
        BlobContainerClient container,
        string? prefix = null,
        long maxBufferedDownloadBytes = 67_108_864)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) throw new ArgumentException("Connection ID is required.", nameof(connectionId));
        ArgumentNullException.ThrowIfNull(container);
        if (maxBufferedDownloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBufferedDownloadBytes));
        var normalized = StoragePath.Normalize(prefix ?? string.Empty);
        if (normalized.IsFailure) throw new ArgumentException(normalized.Error!.Message, nameof(prefix));
        ConnectionId = connectionId;
        _container = container;
        var features = AzureCapabilities.Features;
        if (container.GetBlobClient(".clstorage-capability-probe").CanGenerateSasUri)
            features |= StorageFeature.SignedReadUrls | StorageFeature.SignedWriteUrls;
        _capabilities = new StorageCapabilities(features, AzureCapabilities.Limits);
        Root = normalized.Value!;
        _prefix = Root.Length == 0 ? string.Empty : Root + "/";
        _maxBufferedDownloadBytes = maxBufferedDownloadBytes;
    }
}
