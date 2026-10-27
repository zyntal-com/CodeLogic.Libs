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
public sealed class S3StorageBackend :
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

    /// <inheritdoc />
    public string ConnectionId { get; }
    /// <inheritdoc />
    public StorageProvider Provider => StorageProvider.S3;

    /// <summary>Objects larger than this are copied part by part: S3 copies at most 5 GiB in one request.</summary>
    internal long MultipartCopyThresholdBytes { get; init; } = 5L * 1024 * 1024 * 1024;

    /// <summary>Runs between the existence check and a create-only copy, so a test can show the server enforces it.</summary>
    internal Func<Task>? BeforeConditionalCopy { get; init; }
    /// <inheritdoc />
    public string Root { get; }
    /// <inheritdoc />
    /// <remarks>
    /// Under <see cref="S3ConditionalRequestSupport.Auto"/> the conditional flags are provisional until the connection's
    /// probe has run (see <see cref="S3ConditionalRequestSupport.Auto"/>); from then on they name only what the server
    /// enforces: <see cref="StorageFeature.ConditionalCreate"/> when <c>If-None-Match</c> is enforced on both uploads
    /// and copies, <see cref="StorageFeature.ConditionalUpdate"/> likewise for <c>If-Match</c>, and
    /// <see cref="StorageFeature.ConditionalDelete"/> when <c>If-Match</c> is enforced on deletes. To know before
    /// relying on one, ask <see cref="StorageConditionEnforcementExtensions.GetConditionEnforcementAsync"/>, which runs
    /// the probe when needed.
    /// </remarks>
    public StorageCapabilities Capabilities => ConditionalRequests switch
    {
        S3ConditionalRequestSupport.NotEnforced => _unconditionalCapabilities,
        S3ConditionalRequestSupport.Enforced => _capabilities,
        _ => Volatile.Read(ref _conditionProbe) is { Conclusive: true } probe ? probe.Capabilities(_capabilities) : _capabilities
    };

    /// <inheritdoc />
    public async Task<Result<StorageItem>> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Normalize(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        if (normalized.Value!.Length == 0)
            return Result<StorageItem>.Success(DirectoryItem(string.Empty));

        try
        {
            var response = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _bucket,
                Key = ToKey(normalized.Value)
            }, cancellationToken).ConfigureAwait(false);
            return Result<StorageItem>.Success(FromMetadata(normalized.Value, response));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AmazonS3Exception error) when (IsNotFound(error))
        {
            return await GetDirectoryInfoAsync(normalized.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Get S3 item info")); }
    }

    /// <inheritdoc />
    public async Task<Result<bool>> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = await GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
        if (info.IsSuccess) return Result<bool>.Success(true);
        return info.Error?.Code == StorageErrors.NotFoundCode
            ? Result<bool>.Success(false)
            : Result<bool>.Failure(info.Error!);
    }

    /// <inheritdoc />
    public async Task<Result<StoragePage>> ListAsync(string path, StorageListOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageListOptions();
        var valid = options.Validate();
        if (valid.IsFailure) return Result<StoragePage>.Failure(valid.Error!);
        if (ProviderPaging.RecursiveTokenOnFlatListing(options) is { } mixed) return Result<StoragePage>.Failure(mixed);
        var normalized = Normalize(path);
        if (normalized.IsFailure) return Result<StoragePage>.Failure(normalized.Error!);
        if (normalized.Value!.Length > 0)
        {
            var directory = await GetInfoAsync(normalized.Value, cancellationToken).ConfigureAwait(false);
            if (directory.IsFailure) return Result<StoragePage>.Failure(directory.Error!);
            if (directory.Value!.ItemType != StorageItemType.Directory)
                return Result<StoragePage>.Failure(StorageErrors.Conflict("An S3 file cannot be listed as a directory."));
        }

        try
        {
            var (nativeToken, previous) = options.Recursive
                ? ImplicitDirectories.Unwrap(options.ContinuationToken)
                : (options.ContinuationToken, null);
            var request = new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = ToDirectoryPrefix(normalized.Value!),
                Delimiter = options.Recursive ? null : "/",
                MaxKeys = options.PageSize,
                ContinuationToken = nativeToken
            };
            var response = await _client.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
            var items = new List<StorageItem>();
            foreach (var prefix in response.CommonPrefixes ?? [])
            {
                var relative = FromKey(prefix).TrimEnd('/');
                if (relative.Length > 0) items.Add(DirectoryItem(relative));
            }
            foreach (var item in response.S3Objects ?? [])
            {
                var relative = FromKey(item.Key);
                if (relative.Length == 0) continue;
                if (options.Recursive)
                {
                    ImplicitDirectories.AddParents(items, relative, normalized.Value!, previous, DirectoryItem);
                    // The key itself, so a folder marker ("a/b/") that ends a page still covers "a/b" on the next.
                    previous = relative;
                }
                if (relative.EndsWith('/'))
                    items.Add(DirectoryItem(relative.TrimEnd('/')));
                else
                    items.Add(FromListedObject(relative, item));
            }
            var unique = items.GroupBy(item => item.Path, StringComparer.Ordinal).Select(group => group.First())
                .OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
            var next = options.Recursive ? ImplicitDirectories.Wrap(response.NextContinuationToken, previous) : response.NextContinuationToken;
            return Result<StoragePage>.Success(new StoragePage(StorageListFilter.Apply(unique, options, normalized.Value!), next));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StoragePage>.Failure(Map(error, "List S3 objects")); }
    }

    /// <inheritdoc />
    public async Task<Result> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Normalize(path);
        if (normalized.IsFailure) return Result.Failure(normalized.Error!);
        if (normalized.Value!.Length == 0) return Result.Success();
        await using var empty = new MemoryStream([]);
        try
        {
            await _client.PutObjectAsync(NewPutRequest(ToKey(normalized.Value!) + "/", empty, "application/x-directory", null), cancellationToken)
                .ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Create S3 directory")); }
    }

    /// <inheritdoc />
    public async Task<Result<StorageItem>> UploadAsync(string path, Stream source, StorageUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (StorageTransferPipeline.Applies(this, options))
            return await StorageTransferPipeline.UploadAsync(this, path, source, options, cancellationToken).ConfigureAwait(false);
        options ??= new StorageUploadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StorageItem>.Failure(validation.Error!);
        if (StorageOptionValidation.MetadataSizeBytes(options.Metadata) > 2 * 1024)
            return Result<StorageItem>.Failure(StorageErrors.TooLarge("S3 user metadata exceeds the 2 KiB limit."));
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);

        try
        {
            long? size = source.CanSeek ? Math.Max(0, source.Length - source.Position) : null;
            var key = ToKey(normalized.Value!);
            var ifMatch = await ResolveUploadIfMatchAsync(
                key,
                options.Condition,
                cancellationToken).ConfigureAwait(false);
            if (ifMatch.IsFailure) return Result<StorageItem>.Failure(ifMatch.Error!);
            var sendConditions = await UploadConditionsEnforcedAsync(options, cancellationToken).ConfigureAwait(false);
            if (!sendConditions)
            {
                // The server would ignore or reject the headers: the condition is checked here, just before the write.
                var check = await CheckBeforeWriteAsync(key, options.Overwrite ? options.Condition : null, !options.Overwrite, cancellationToken).ConfigureAwait(false);
                if (check.IsFailure) return Result<StorageItem>.Failure(check.Error!);
            }
            S3UploadCompletion completion;
            if (!size.HasValue || size.Value >= _multipartThresholdBytes || size.Value > 5L * 1024 * 1024 * 1024)
            {
                completion = await UploadMultipartAsync(
                    key,
                    source,
                    options,
                    ifMatch.Value,
                    sendConditions,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var request = NewPutRequest(key, source, options.ContentType, options.Metadata);
                if (!sendConditions) { /* checked above */ }
                else if (!options.Overwrite)
                    request.IfNoneMatch = "*";
                else if (ifMatch.Value is not null)
                    request.IfMatch = ifMatch.Value;
                var response = await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
                completion = new S3UploadCompletion(response.ETag?.Trim('"'), response.VersionId, size);
            }
            return Result<StorageItem>.Success(new StorageItem
            {
                Path = normalized.Value!,
                Name = NameOf(normalized.Value!),
                ItemType = StorageItemType.File,
                Size = completion.Bytes,
                LastModified = DateTimeOffset.UtcNow,
                ContentType = options.ContentType,
                ETag = completion.ETag,
                VersionId = completion.VersionId,
                Metadata = options.Metadata
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Upload S3 object")); }
    }

    /// <inheritdoc />
    public async Task<Result<StorageItem>> UploadBytesAsync(string path, byte[] content, StorageUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        await using var stream = new MemoryStream(content, writable: false);
        return await UploadAsync(path, stream, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<Stream>> DownloadAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default) =>
        await StorageTransferPipeline.MeterAsync(this, path, await DownloadUnmeteredAsync(path, options, cancellationToken).ConfigureAwait(false), options, cancellationToken).ConfigureAwait(false);

    private async Task<Result<Stream>> DownloadUnmeteredAsync(string path, StorageDownloadOptions? options, CancellationToken cancellationToken)
    {
        options ??= new StorageDownloadOptions();
        var valid = options.Validate();
        if (valid.IsFailure) return Result<Stream>.Failure(valid.Error!);
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<Stream>.Failure(normalized.Error!);
        try
        {
            var request = new GetObjectRequest { BucketName = _bucket, Key = ToKey(normalized.Value!) };
            if (options.VersionId is not null)
                request.VersionId = options.VersionId;
            if (options.Offset > 0 || options.Length.HasValue)
            {
                var end = options.Length.HasValue ? options.Offset + options.Length.Value - 1 : long.MaxValue;
                request.ByteRange = new ByteRange(options.Offset, end);
            }
            var response = await _client.GetObjectAsync(request, cancellationToken).ConfigureAwait(false);
            return Result<Stream>.Success(new OwnedResourceStream(response.ResponseStream, response));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<Stream>.Failure(Map(error, "Download S3 object")); }
    }

    /// <inheritdoc />
    public async Task<Result<byte[]>> DownloadBytesAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageDownloadOptions();
        var limit = options.MaxBufferedBytes ?? _maxBufferedDownloadBytes;
        var download = await DownloadAsync(path, options, cancellationToken).ConfigureAwait(false);
        if (download.IsFailure) return Result<byte[]>.Failure(download.Error!);
        await using var source = download.Value!;
        using var target = new MemoryStream();
        var buffer = new byte[81_920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (target.Length > limit - read || target.Length > int.MaxValue - read)
                return Result<byte[]>.Failure(StorageErrors.TooLarge($"The download exceeds the {limit} byte buffering limit."));
            target.Write(buffer, 0, read);
        }
        return Result<byte[]>.Success(target.ToArray());
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync(string path, StorageDeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageDeleteOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return validation;
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result.Failure(normalized.Error!);
        try
        {
            var info = await GetInfoAsync(normalized.Value!, cancellationToken).ConfigureAwait(false);
            if (info.IsFailure)
                return options.IgnoreMissing && info.Error?.Code == StorageErrors.NotFoundCode ? Result.Success() : Result.Failure(info.Error!);

            if (info.Value!.ItemType == StorageItemType.Directory)
            {
                if (options.Condition is { IsEmpty: false })
                    return Result.Failure(StorageErrors.Unsupported(
                        "S3 virtual directories do not have one atomic identity condition."));
                var prefix = ToDirectoryPrefix(normalized.Value!);
                string? token = null;
                do
                {
                    var page = await _client.ListObjectsV2Async(new ListObjectsV2Request
                    {
                        BucketName = _bucket,
                        Prefix = prefix,
                        ContinuationToken = token,
                        MaxKeys = 1000
                    }, cancellationToken).ConfigureAwait(false);
                    if (!options.Recursive && (page.S3Objects?.Any(item => item.Key != prefix) ?? false))
                        return Result.Failure(StorageErrors.Conflict("The S3 directory is not empty."));
                    foreach (var item in page.S3Objects ?? [])
                        await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = item.Key }, cancellationToken).ConfigureAwait(false);
                    token = page.NextContinuationToken;
                } while (!string.IsNullOrEmpty(token));
                await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = prefix }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var condition = ValidateCurrentCondition(info.Value, options.Condition, "S3 object");
                if (condition.IsFailure) return condition;
                // Checked just above; sent as If-Match too only where the server enforces it on deletes.
                var sendIfMatch = options.Condition is { IsEmpty: false } &&
                    await ConditionStateAsync(S3ProbedCondition.DeleteMatch, cancellationToken).ConfigureAwait(false) == S3ConditionState.Enforced;
                await _client.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = _bucket,
                    Key = ToKey(normalized.Value!),
                    IfMatch = !sendIfMatch ? null : options.Condition?.ExpectedETag ??
                        (options.Condition?.ExpectedVersionId is null ? null : info.Value.ETag)
                }, cancellationToken).ConfigureAwait(false);
            }
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Delete S3 object")); }
    }

    /// <inheritdoc />
    /// <remarks>
    /// A file is copied on the server in one <c>CopyObject</c> up to 5 GiB and part by part above, always pinned to
    /// the source version it read (<c>x-amz-copy-source-if-match</c>, and <see cref="StorageTransferOptions.SourceVersionId"/>
    /// when set), keeping the content type and headers, user metadata, tags, storage class, and SSE-S3/SSE-KMS
    /// settings. A create-only copy sends <c>If-None-Match: *</c>; whether the server enforces it is reported by
    /// <see cref="IStorageConditionEnforcementSource"/> (MinIO ignores it on <c>CopyObject</c>, so there the
    /// existence check made just before is the only guard). A <see cref="StorageTransferOptions.DestinationCondition"/>
    /// is sent as <c>If-Match</c> and refused as unsupported where the server would ignore it.
    /// </remarks>
    public async Task<Result> CopyAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageTransferOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return validation;
        var source = NormalizeRequired(sourcePath);
        if (source.IsFailure) return Result.Failure(source.Error!);
        var destination = NormalizeRequired(destinationPath);
        if (destination.IsFailure) return Result.Failure(destination.Error!);
        var relationship = StorageTransferPath.ValidateDistinct(source.Value!, destination.Value!);
        if (relationship.IsFailure) return relationship;
        var sourceInfo = await SourceHeadAsync(source.Value!, options.SourceVersionId, cancellationToken).ConfigureAwait(false);
        if (sourceInfo.IsFailure) return Result.Failure(sourceInfo.Error!);
        if (sourceInfo.Value.Head is null)
        {
            relationship = StorageTransferPath.ValidateDirectoryDestination(source.Value!, destination.Value!);
            if (relationship.IsFailure) return relationship;
            var relayed = await StorageTransferCoordinator.CopyAsync(
                this,
                source.Value!,
                this,
                destination.Value!,
                options,
                cancellationToken).ConfigureAwait(false);
            return relayed.IsSuccess ? Result.Success() : Result.Failure(relayed.Error!);
        }
        var copied = await CopyFileAsync(source.Value!, destination.Value!, options, sourceInfo.Value.Head, cancellationToken).ConfigureAwait(false);
        return copied.IsSuccess ? Result.Success() : Result.Failure(copied.Error!);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A file is copied as <see cref="CopyAsync"/> does, pinned to the version read, and the source is then deleted
    /// only while it is still that version (<c>If-Match</c>, checked just before as well). Once the copy has
    /// committed, a failure to delete the source (it changed, or the request failed) returns
    /// <c>storage.partial_failure</c> with <c>destinationState=complete</c> and <c>leftBehind</c> naming the source;
    /// cancellation no longer applies then. A directory is copied through the relay and each copied file deleted
    /// under the identity it was listed with; files that changed or appeared meanwhile are kept.
    /// </remarks>
    public async Task<Result> MoveAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageTransferOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return validation;
        var source = NormalizeRequired(sourcePath);
        if (source.IsFailure) return Result.Failure(source.Error!);
        var destination = NormalizeRequired(destinationPath);
        if (destination.IsFailure) return Result.Failure(destination.Error!);
        var relationship = StorageTransferPath.ValidateDistinct(source.Value!, destination.Value!);
        if (relationship.IsFailure) return relationship;
        var sourceInfo = await SourceHeadAsync(source.Value!, options.SourceVersionId, cancellationToken).ConfigureAwait(false);
        if (sourceInfo.IsFailure) return Result.Failure(sourceInfo.Error!);
        if (sourceInfo.Value.Head is null)
        {
            relationship = StorageTransferPath.ValidateDirectoryDestination(source.Value!, destination.Value!);
            if (relationship.IsFailure) return relationship;
            return await ObjectStoreMoves.MoveDirectoryAsync(this, source.Value!, destination.Value!, options, cancellationToken).ConfigureAwait(false);
        }

        var copied = await CopyFileAsync(source.Value!, destination.Value!, options, sourceInfo.Value.Head, cancellationToken).ConfigureAwait(false);
        if (copied.IsFailure) return Result.Failure(copied.Error!);
        // The destination is committed: the source is deleted under the identity that was copied, and a cancel
        // can no longer leave the move half-reported.
        var deleted = await DeleteMovedSourceAsync(ToKey(source.Value!), copied.Value!).ConfigureAwait(false);
        return deleted.IsSuccess
            ? Result.Success()
            : Result.Failure(ObjectStoreMoves.SourceKept("S3", source.Value!, deleted.Error!));
    }

    /// <inheritdoc />
    public async Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = _keyPrefix,
                MaxKeys = 1
            }, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Check S3 health")); }
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyDictionary<string, string>>> GetMetadataAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var info = await GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
        return info.IsSuccess
            ? Result<IReadOnlyDictionary<string, string>>.Success(info.Value!.Metadata)
            : Result<IReadOnlyDictionary<string, string>>.Failure(info.Error!);
    }

    /// <inheritdoc />
    public async Task<Result<StorageItem>> SetMetadataAsync(
        string path,
        IReadOnlyDictionary<string, string> metadata,
        StorageMetadataUpdateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        options ??= new StorageMetadataUpdateOptions();
        var validation = options.Validate(metadata);
        if (validation.IsFailure) return Result<StorageItem>.Failure(validation.Error!);
        if (StorageOptionValidation.MetadataSizeBytes(metadata) > 2 * 1024)
            return Result<StorageItem>.Failure(StorageErrors.TooLarge("S3 user metadata exceeds the 2 KiB limit."));
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        var snapshot = StorageMetadataSnapshot.Create(metadata);
        try
        {
            var key = ToKey(normalized.Value!);
            var current = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _bucket,
                Key = key
            }, cancellationToken).ConfigureAwait(false);
            if (options.ExpectedVersionId is not null &&
                !string.Equals(options.ExpectedVersionId, current.VersionId, StringComparison.Ordinal))
            {
                return Result<StorageItem>.Failure(StorageErrors.Conflict(
                    "The S3 object version no longer matches the metadata update condition."));
            }

            var values = options.Mode == StorageMetadataUpdateMode.Merge
                ? UserMetadata(current.Metadata)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, value) in snapshot)
                values[name] = value;

            var request = new CopyObjectRequest
            {
                SourceBucket = _bucket,
                SourceKey = key,
                DestinationBucket = _bucket,
                DestinationKey = key,
                MetadataDirective = S3MetadataDirective.REPLACE,
                ContentType = current.Headers.ContentType,
                CacheControl = current.Headers.CacheControl,
                ContentDisposition = current.Headers.ContentDisposition,
                ContentEncoding = current.Headers.ContentEncoding,
                ContentLanguage = current.Headers.ContentLanguage,
                // The object is copied onto itself, so pinning the source (x-amz-copy-source-if-match, which every
                // S3-compatible server honours) pins the destination too; If-Match, which some servers ignore or
                // reject on CopyObject, is not needed.
                ETagToMatch = options.ExpectedETag ?? (options.ExpectedVersionId is null ? null : current.ETag)
            };
            foreach (var (name, value) in values)
                request.Metadata[name] = value;
            await _client.CopyObjectAsync(request, cancellationToken).ConfigureAwait(false);
            return await GetInfoAsync(normalized.Value!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AmazonS3Exception error) when (error.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            return Result<StorageItem>.Failure(StorageErrors.Conflict(
                "The S3 object changed before its metadata could be updated."));
        }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Update S3 metadata")); }
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyDictionary<string, string>>> GetTagsAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure)
            return Result<IReadOnlyDictionary<string, string>>.Failure(normalized.Error!);
        try
        {
            var response = await _client.GetObjectTaggingAsync(new GetObjectTaggingRequest
            {
                BucketName = _bucket,
                Key = ToKey(normalized.Value!)
            }, cancellationToken).ConfigureAwait(false);
            return Result<IReadOnlyDictionary<string, string>>.Success(
                StorageMetadataSnapshot.Create(
                    (response.Tagging ?? []).Select(tag =>
                        new KeyValuePair<string, string>(tag.Key, tag.Value))));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            return Result<IReadOnlyDictionary<string, string>>.Failure(Map(error, "Read S3 object tags"));
        }
    }

    /// <inheritdoc />
    public async Task<Result<StorageItem>> SetTagsAsync(
        string path,
        IReadOnlyDictionary<string, string> tags,
        StorageTagUpdateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        options ??= new StorageTagUpdateOptions();
        var validation = options.Validate(tags);
        if (validation.IsFailure) return Result<StorageItem>.Failure(validation.Error!);
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var key = ToKey(normalized.Value!);
            if (options.Mode == StorageTagUpdateMode.Merge)
            {
                var current = await _client.GetObjectTaggingAsync(new GetObjectTaggingRequest
                {
                    BucketName = _bucket,
                    Key = key
                }, cancellationToken).ConfigureAwait(false);
                foreach (var tag in current.Tagging ?? [])
                    values[tag.Key] = tag.Value;
            }
            foreach (var (name, value) in tags)
                values[name] = value;
            validation = options.Validate(values);
            if (validation.IsFailure) return Result<StorageItem>.Failure(validation.Error!);

            await _client.PutObjectTaggingAsync(new PutObjectTaggingRequest
            {
                BucketName = _bucket,
                Key = key,
                Tagging = new Tagging
                {
                    TagSet = values.Select(pair => new Amazon.S3.Model.Tag
                    {
                        Key = pair.Key,
                        Value = pair.Value
                    }).ToList()
                }
            }, cancellationToken).ConfigureAwait(false);
            return await GetInfoAsync(normalized.Value!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Update S3 object tags")); }
    }

    /// <inheritdoc />
    public async Task<Result<StorageVersionPage>> ListVersionsAsync(
        string path,
        StorageVersionListOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageVersionListOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StorageVersionPage>.Failure(validation.Error!);
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageVersionPage>.Failure(normalized.Error!);
        var continuation = DecodeVersionContinuation(options.ContinuationToken);
        if (continuation.IsFailure) return Result<StorageVersionPage>.Failure(continuation.Error!);
        var key = ToKey(normalized.Value!);

        try
        {
            var response = await _client.ListVersionsAsync(new ListVersionsRequest
            {
                BucketName = _bucket,
                Prefix = key,
                MaxKeys = Math.Min(options.PageSize, 1_000),
                KeyMarker = continuation.Value!.KeyMarker,
                VersionIdMarker = continuation.Value.VersionIdMarker
            }, cancellationToken).ConfigureAwait(false);
            var versions = new List<StorageVersion>();
            foreach (var version in response.Versions ?? [])
            {
                if (!string.Equals(version.Key, key, StringComparison.Ordinal) ||
                    (!options.IncludeDeleteMarkers && version.IsDeleteMarker == true))
                {
                    continue;
                }
                if (string.IsNullOrWhiteSpace(version.VersionId))
                    return Result<StorageVersionPage>.Failure(StorageErrors.ProviderError(
                        "S3 returned a version without a version identifier."));
                versions.Add(new StorageVersion
                {
                    Path = normalized.Value!,
                    VersionId = version.VersionId,
                    ETag = version.ETag?.Trim('"'),
                    Size = version.Size,
                    LastModified = version.LastModified.HasValue
                        ? new DateTimeOffset(version.LastModified.Value.ToUniversalTime())
                        : null,
                    IsLatest = version.IsLatest == true,
                    IsDeleteMarker = version.IsDeleteMarker == true
                });
            }

            string? next = null;
            if (response.IsTruncated == true)
            {
                if (string.IsNullOrEmpty(response.NextKeyMarker))
                    return Result<StorageVersionPage>.Failure(StorageErrors.ProviderError(
                        "S3 truncated a version page without returning a continuation marker."));
                next = EncodeVersionContinuation(new S3VersionContinuation(
                    response.NextKeyMarker,
                    response.NextVersionIdMarker));
            }
            return Result<StorageVersionPage>.Success(new StorageVersionPage(versions.AsReadOnly(), next));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageVersionPage>.Failure(Map(error, "List S3 object versions")); }
    }

    /// <inheritdoc />
    public async Task<Result> DeleteVersionAsync(
        string path,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var versionValidation = StorageOptionValidation.OptionalToken(versionId, nameof(versionId));
        if (versionValidation.IsFailure) return versionValidation;
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result.Failure(normalized.Error!);
        try
        {
            await _client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = _bucket,
                Key = ToKey(normalized.Value!),
                VersionId = versionId
            }, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Delete S3 object version")); }
    }

    /// <inheritdoc />
    public async Task<Result<StorageSignedUrl>> CreateSignedUrlAsync(
        string path,
        StorageSignedUrlOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageSignedUrlOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StorageSignedUrl>.Failure(validation.Error!);
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageSignedUrl>.Failure(normalized.Error!);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var expiresAt = DateTimeOffset.UtcNow.Add(options.ExpiresIn);
            var request = new GetPreSignedUrlRequest
            {
                BucketName = _bucket,
                Key = ToKey(normalized.Value!),
                Expires = expiresAt.UtcDateTime,
                Verb = options.Method == StorageSignedUrlMethod.Read ? HttpVerb.GET : HttpVerb.PUT,
                ContentType = options.ContentType,
                VersionId = options.VersionId
            };
            var value = await _client.GetPreSignedURLAsync(request).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!Uri.TryCreate(value, UriKind.Absolute, out var url))
                return Result<StorageSignedUrl>.Failure(StorageErrors.ProviderError(
                    "The S3 provider returned an invalid signed URL."));
            return Result<StorageSignedUrl>.Success(new StorageSignedUrl(url, options.Method, expiresAt));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageSignedUrl>.Failure(Map(error, "Create S3 signed URL")); }
    }

    /// <inheritdoc />
    public bool TryGetNativeClient<TClient>([NotNullWhen(true)] out TClient? client) where TClient : class
    {
        client = _client as TClient;
        return client is not null;
    }

    /// <inheritdoc />
    public Task<Result<NativeConnectionLease<TClient>>> OpenNativeConnectionAsync<TClient>(CancellationToken cancellationToken = default) where TClient : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_client is not TClient typed)
            return Task.FromResult(Result<NativeConnectionLease<TClient>>.Failure(StorageErrors.Unsupported($"S3 does not expose native type '{typeof(TClient).FullName}'.")));
        return Task.FromResult(Result<NativeConnectionLease<TClient>>.Success(new NativeConnectionLease<TClient>(typed, _ => ValueTask.CompletedTask)));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _probeGate.Dispose();
            if (_ownsClient) _client.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private async Task<Result<StorageItem>> GetDirectoryInfoAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = ToDirectoryPrefix(path),
                MaxKeys = 1
            }, cancellationToken).ConfigureAwait(false);
            return (response.KeyCount ?? 0) > 0
                ? Result<StorageItem>.Success(DirectoryItem(path))
                : Result<StorageItem>.Failure(StorageErrors.NotFound($"S3 item '{path}' was not found."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Get S3 directory info")); }
    }

    private PutObjectRequest NewPutRequest(string key, Stream source, string? contentType, IReadOnlyDictionary<string, string>? metadata)
    {
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = source,
            AutoCloseStream = false,
            AutoResetStreamPosition = false,
            DisablePayloadSigning = _disablePayloadSigning,
            DisableDefaultChecksumValidation = _disableChecksumValidation
        };
        if (!string.IsNullOrWhiteSpace(contentType)) request.ContentType = contentType;
        if (metadata is not null)
            foreach (var (name, value) in metadata) request.Metadata[name] = value;
        return request;
    }

    private async Task<Result<string?>> ResolveUploadIfMatchAsync(
        string key,
        StorageMutationCondition? condition,
        CancellationToken cancellationToken)
    {
        if (condition is null or { IsEmpty: true })
            return Result<string?>.Success(null);
        if (condition.ExpectedVersionId is null)
            return Result<string?>.Success(condition.ExpectedETag);
        try
        {
            var current = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _bucket,
                Key = key
            }, cancellationToken).ConfigureAwait(false);
            var item = FromMetadata(FromKey(key), current);
            var validation = ValidateCurrentCondition(item, condition, "S3 object");
            return validation.IsFailure
                ? Result<string?>.Failure(validation.Error!)
                : Result<string?>.Success(condition.ExpectedETag ?? current.ETag);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AmazonS3Exception error) when (IsNotFound(error))
        {
            return Result<string?>.Failure(StorageErrors.Conflict(
                "The S3 object no longer exists for the requested upload condition."));
        }
        catch (Exception error)
        {
            return Result<string?>.Failure(Map(error, "Resolve S3 upload condition"));
        }
    }

    /// <summary>
    /// Checks a write's condition just before it, for a server that does not enforce conditional headers:
    /// a create-only write needs the key absent, a conditional replace needs its ETag or version.
    /// </summary>
    private async Task<Result> CheckBeforeWriteAsync(string key, StorageMutationCondition? condition, bool createOnly, CancellationToken cancellationToken)
    {
        if (!createOnly && condition is null or { IsEmpty: true })
            return Result.Success();
        try
        {
            var current = await HeadAsync(key, cancellationToken).ConfigureAwait(false);
            if (createOnly)
                return current is null ? Result.Success() : Result.Failure(StorageErrors.Conflict("The S3 destination already exists."));
            if (current is null)
                return Result.Failure(StorageErrors.Conflict("The S3 object no longer exists for the requested upload condition."));
            return ValidateCurrentCondition(FromMetadata(FromKey(key), current), condition, "S3 object");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Check S3 write condition")); }
    }

    private static Result ValidateCurrentCondition(
        StorageItem current,
        StorageMutationCondition? condition,
        string itemName)
    {
        if (condition is null or { IsEmpty: true })
            return Result.Success();
        if (condition.ExpectedETag is not null &&
            !string.Equals(
                condition.ExpectedETag.Trim('"'),
                current.ETag?.Trim('"'),
                StringComparison.Ordinal))
        {
            return Result.Failure(StorageErrors.Conflict(
                $"The {itemName} ETag no longer matches the requested condition."));
        }
        return condition.ExpectedVersionId is not null &&
               !string.Equals(condition.ExpectedVersionId, current.VersionId, StringComparison.Ordinal)
            ? Result.Failure(StorageErrors.Conflict(
                $"The {itemName} version no longer matches the requested condition."))
            : Result.Success();
    }

    private async Task<S3UploadCompletion> UploadMultipartAsync(
        string key,
        Stream source,
        StorageUploadOptions options,
        string? ifMatch,
        bool sendConditions,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_multipartPartSizeBytes);
        string? uploadId = null;
        try
        {
            var first = await ReadPartAsync(source, buffer, _multipartPartSizeBytes, cancellationToken).ConfigureAwait(false);
            if (first.EndOfStream)
            {
                await using var content = new MemoryStream(buffer, 0, first.Count, writable: false, publiclyVisible: true);
                var request = NewPutRequest(key, content, options.ContentType, options.Metadata);
                if (!sendConditions) { /* checked before the upload */ }
                else if (!options.Overwrite)
                    request.IfNoneMatch = "*";
                else if (ifMatch is not null)
                    request.IfMatch = ifMatch;
                var response = await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
                return new S3UploadCompletion(response.ETag?.Trim('"'), response.VersionId, first.Count);
            }

            var initiate = new InitiateMultipartUploadRequest
            {
                BucketName = _bucket,
                Key = key,
                ContentType = options.ContentType
            };
            foreach (var (name, value) in options.Metadata)
                initiate.Metadata[name] = value;
            // Not cancelled half-way: an upload the server created but whose id never came back could not be aborted.
            var initiated = await _client.InitiateMultipartUploadAsync(initiate, CancellationToken.None).ConfigureAwait(false);
            uploadId = initiated.UploadId;
            cancellationToken.ThrowIfCancellationRequested();
            var parts = new List<PartETag>();
            long totalBytes = 0;
            var partNumber = 1;
            var current = first;
            while (current.Count > 0)
            {
                if (partNumber > 10_000)
                    throw new StorageMultipartLimitException();
                await using var partStream = new MemoryStream(
                    buffer,
                    0,
                    current.Count,
                    writable: false,
                    publiclyVisible: true);
                var uploaded = await _client.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = _bucket,
                    Key = key,
                    UploadId = uploadId,
                    PartNumber = partNumber,
                    PartSize = current.Count,
                    InputStream = partStream,
                    IsLastPart = current.EndOfStream,
                    DisablePayloadSigning = _disablePayloadSigning,
                    DisableDefaultChecksumValidation = _disableChecksumValidation
                }, cancellationToken).ConfigureAwait(false);
                parts.Add(new PartETag(uploaded));
                totalBytes = checked(totalBytes + current.Count);
                partNumber++;
                if (current.EndOfStream)
                    break;
                current = await ReadPartAsync(source, buffer, _multipartPartSizeBytes, cancellationToken).ConfigureAwait(false);
            }

            var completeRequest = new CompleteMultipartUploadRequest
            {
                BucketName = _bucket,
                Key = key,
                UploadId = uploadId,
                PartETags = parts
            };
            if (!sendConditions) { /* checked before the upload */ }
            else if (!options.Overwrite)
                completeRequest.IfNoneMatch = "*";
            else if (ifMatch is not null)
                completeRequest.IfMatch = ifMatch;
            // Once sent, the completion is not cancelled: it may commit on the server with its reply lost to the cancel.
            cancellationToken.ThrowIfCancellationRequested();
            var completed = await CompleteMultipartAsync(completeRequest).ConfigureAwait(false);
            uploadId = null;
            return new S3UploadCompletion(completed.ETag?.Trim('"'), completed.VersionId, totalBytes);
        }
        catch (StorageMultipartLimitException)
        {
            throw;
        }
        finally
        {
            if (uploadId is not null)
            {
                try
                {
                    await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                    {
                        BucketName = _bucket,
                        Key = key,
                        UploadId = uploadId
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
            }
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<S3PartRead> ReadPartAsync(
        Stream source,
        byte[] buffer,
        int maxCount,
        CancellationToken cancellationToken)
    {
        var count = 0;
        while (count < maxCount)
        {
            var read = await source.ReadAsync(buffer.AsMemory(count, maxCount - count), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return new S3PartRead(count, EndOfStream: true);
            count += read;
        }
        return new S3PartRead(count, EndOfStream: false);
    }

    /// <summary>
    /// Reads a copy or move source: the object's headers (at <paramref name="versionId"/> when set), or no headers
    /// when the path is a directory.
    /// </summary>
    private async Task<Result<(StorageItem Item, GetObjectMetadataResponse? Head)>> SourceHeadAsync(string path, string? versionId, CancellationToken cancellationToken)
    {
        try
        {
            var head = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _bucket,
                Key = ToKey(path),
                VersionId = versionId
            }, cancellationToken).ConfigureAwait(false);
            return Result<(StorageItem, GetObjectMetadataResponse?)>.Success((FromMetadata(path, head), head));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AmazonS3Exception error) when (IsNotFound(error) && versionId is null)
        {
            var directory = await GetDirectoryInfoAsync(path, cancellationToken).ConfigureAwait(false);
            return directory.IsSuccess
                ? Result<(StorageItem, GetObjectMetadataResponse?)>.Success((directory.Value!, null))
                : Result<(StorageItem, GetObjectMetadataResponse?)>.Failure(directory.Error!);
        }
        catch (Exception error) { return Result<(StorageItem, GetObjectMetadataResponse?)>.Failure(Map(error, "Get S3 copy source")); }
    }

    /// <summary>
    /// Copies one object on the server, pinned to the version <paramref name="head"/> describes, and returns that
    /// identity so a move deletes the source under the same condition.
    /// </summary>
    private async Task<Result<S3PinnedSource>> CopyFileAsync(
        string sourcePath,
        string destinationPath,
        StorageTransferOptions options,
        GetObjectMetadataResponse head,
        CancellationToken cancellationToken)
    {
        if (options.ExpectedSourceETag is { } expected && !StagedWriter.SameETag(expected, head.ETag))
            return Result<S3PinnedSource>.Failure(StorageErrors.Conflict(
                $"The S3 source '{sourcePath}' changed since it was read.",
                $"expectedETag={expected.Trim('"')};actualETag={head.ETag?.Trim('"')}"));
        var sourceKey = ToKey(sourcePath);
        var destinationKey = ToKey(destinationPath);
        var multipart = head.ContentLength > MultipartCopyThresholdBytes;
        string? ifMatch = null;
        if (options.DestinationCondition is { IsEmpty: false } condition)
        {
            // A condition the server would ignore must not be sent as if it were kept: the caller falls back to a
            // path that checks it. A large copy commits with CompleteMultipartUpload, so that request must enforce it.
            if (await ConditionStateAsync(multipart ? S3ProbedCondition.PutMatch : S3ProbedCondition.CopyMatch, cancellationToken).ConfigureAwait(false) != S3ConditionState.Enforced)
                return Result<S3PinnedSource>.Failure(StorageErrors.Unsupported(
                    "This S3-compatible server does not enforce If-Match on the request that commits a copy, so a server-side copy cannot keep the destination condition."));
            var resolved = await ResolveUploadIfMatchAsync(destinationKey, condition, cancellationToken).ConfigureAwait(false);
            if (resolved.IsFailure) return Result<S3PinnedSource>.Failure(resolved.Error!);
            ifMatch = resolved.Value;
        }
        var createOnly = !options.Overwrite;
        var sendCreateOnly = false;
        if (createOnly)
        {
            // Where the server enforces If-None-Match on the committing request this is a courtesy; where it does not
            // (MinIO ignores it on CopyObject; some servers reject it), it is the only guard, the header is not sent,
            // and the enforcement is reported as checked before commit.
            sendCreateOnly = await ConditionStateAsync(multipart ? S3ProbedCondition.PutCreateOnly : S3ProbedCondition.CopyCreateOnly, cancellationToken).ConfigureAwait(false) == S3ConditionState.Enforced;
            var exists = await ExistsAsync(destinationPath, cancellationToken).ConfigureAwait(false);
            if (exists.IsFailure) return Result<S3PinnedSource>.Failure(exists.Error!);
            if (exists.Value) return Result<S3PinnedSource>.Failure(StorageErrors.Conflict("The S3 destination already exists."));
            if (BeforeConditionalCopy is { } hook) await hook().ConfigureAwait(false);
        }
        try
        {
            if (multipart)
                await CopyMultipartAsync(sourceKey, destinationKey, head, options.SourceVersionId, sendCreateOnly, ifMatch, cancellationToken).ConfigureAwait(false);
            else
            {
                // Once sent, a CopyObject is not cancelled: it may commit on the server with its reply lost to the
                // cancel, and a move would then keep a source whose copy exists.
                cancellationToken.ThrowIfCancellationRequested();
                await _client.CopyObjectAsync(NewCopyRequest(sourceKey, destinationKey, head, options.SourceVersionId, sendCreateOnly, ifMatch), CancellationToken.None).ConfigureAwait(false);
            }
            return Result<S3PinnedSource>.Success(new S3PinnedSource(head.ETag, options.SourceVersionId));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return Result<S3PinnedSource>.Failure(StorageErrors.Conflict(
                createOnly
                    ? "The S3 copy was refused: the destination now exists, or the source changed since it was read."
                    : "The S3 copy was refused: the source changed since it was read, or the destination no longer matches its condition.",
                ProviderErrorMapper.Details(StorageErrorInfo.HttpStatusKey, "412", error.ErrorCode)));
        }
        catch (Exception error) { return Result<S3PinnedSource>.Failure(Map(error, "Copy S3 object")); }
    }

    /// <summary>A single <c>CopyObject</c>: content type, headers, user metadata, and tags come along by default.</summary>
    private CopyObjectRequest NewCopyRequest(
        string sourceKey,
        string destinationKey,
        GetObjectMetadataResponse head,
        string? sourceVersionId,
        bool sendCreateOnly,
        string? ifMatch)
    {
        var request = new CopyObjectRequest
        {
            SourceBucket = _bucket,
            SourceKey = sourceKey,
            SourceVersionId = sourceVersionId,
            DestinationBucket = _bucket,
            DestinationKey = destinationKey,
            ETagToMatch = head.ETag
        };
        // Only conditions the server was found to enforce are sent (see ConditionStateAsync).
        if (sendCreateOnly) request.IfNoneMatch = "*";
        else if (ifMatch is not null) request.IfMatch = ifMatch;
        // Without these the copy would take the bucket's defaults: STANDARD storage and the default encryption.
        if (IsNonStandard(head.StorageClass)) request.StorageClass = head.StorageClass;
        if (IsEncrypted(head.ServerSideEncryptionMethod))
        {
            request.ServerSideEncryptionMethod = head.ServerSideEncryptionMethod;
            request.ServerSideEncryptionKeyManagementServiceKeyId = head.ServerSideEncryptionKeyManagementServiceKeyId;
            request.BucketKeyEnabled = head.BucketKeyEnabled;
        }
        return request;
    }

    /// <summary>
    /// Copies an object too large for one <c>CopyObject</c> (over 5 GiB) part by part, every part pinned to the
    /// source version, several parts at a time. The object's headers, user metadata, tags, storage class, and
    /// SSE-S3/SSE-KMS settings are set when the upload starts; a failed part aborts the upload.
    /// </summary>
    private async Task CopyMultipartAsync(
        string sourceKey,
        string destinationKey,
        GetObjectMetadataResponse head,
        string? sourceVersionId,
        bool sendCreateOnly,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        var size = head.ContentLength;
        var initiate = new InitiateMultipartUploadRequest
        {
            BucketName = _bucket,
            Key = destinationKey,
            ContentType = head.Headers.ContentType,
            WebsiteRedirectLocation = head.WebsiteRedirectLocation
        };
        initiate.Headers.CacheControl = head.Headers.CacheControl;
        initiate.Headers.ContentDisposition = head.Headers.ContentDisposition;
        initiate.Headers.ContentEncoding = head.Headers.ContentEncoding;
        initiate.Headers.ContentLanguage = head.Headers.ContentLanguage;
        if (!string.IsNullOrEmpty(head.ExpiresString)) initiate.Headers["Expires"] = head.ExpiresString;
        foreach (var (name, value) in UserMetadata(head.Metadata))
            initiate.Metadata[name] = value;
        if (IsNonStandard(head.StorageClass)) initiate.StorageClass = head.StorageClass;
        if (IsEncrypted(head.ServerSideEncryptionMethod))
        {
            initiate.ServerSideEncryptionMethod = head.ServerSideEncryptionMethod;
            initiate.ServerSideEncryptionKeyManagementServiceKeyId = head.ServerSideEncryptionKeyManagementServiceKeyId;
            initiate.BucketKeyEnabled = head.BucketKeyEnabled;
        }
        if (head.TagsCount is > 0)
        {
            var tags = await _client.GetObjectTaggingAsync(new GetObjectTaggingRequest
            {
                BucketName = _bucket,
                Key = sourceKey,
                VersionId = sourceVersionId
            }, cancellationToken).ConfigureAwait(false);
            initiate.TagSet = tags.Tagging;
        }
        // Not cancelled half-way: an upload the server created but whose id never came back could not be aborted.
        var initiated = await _client.InitiateMultipartUploadAsync(initiate, CancellationToken.None).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var partSize = MultipartCopyPartSize(size, _multipartPartSizeBytes, Math.Min(MultipartCopyMinPartBytes, MultipartCopyThresholdBytes));
            var count = checked((int)((size + partSize - 1) / partSize));
            var parts = new PartETag[count];
            Exception? failure = null;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var slots = new SemaphoreSlim(MultipartCopyConcurrency);
            var running = new List<Task>(count);
            async Task CopyPartAsync(int number, long first, long last)
            {
                try
                {
                    var copied = await _client.CopyPartAsync(new CopyPartRequest
                    {
                        SourceBucket = _bucket,
                        SourceKey = sourceKey,
                        SourceVersionId = sourceVersionId,
                        ETagToMatch = [head.ETag],
                        DestinationBucket = _bucket,
                        DestinationKey = destinationKey,
                        UploadId = initiated.UploadId,
                        PartNumber = number,
                        FirstByte = first,
                        LastByte = last
                    }, stop.Token).ConfigureAwait(false);
                    parts[number - 1] = new PartETag(number, copied.ETag);
                }
                catch (Exception error) when (!(error is OperationCanceledException && stop.IsCancellationRequested))
                {
                    Interlocked.CompareExchange(ref failure, error, null);
                    await stop.CancelAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Another part failed, or the caller cancelled.
                }
                finally { slots.Release(); }
            }
            try
            {
                for (var index = 0; index < count; index++)
                {
                    await slots.WaitAsync(stop.Token).ConfigureAwait(false);
                    var first = index * partSize;
                    running.Add(CopyPartAsync(index + 1, first, Math.Min(first + partSize, size) - 1));
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // A part failed or the caller cancelled; the parts already running finish first.
            }
            await Task.WhenAll(running).ConfigureAwait(false);
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            cancellationToken.ThrowIfCancellationRequested();

            var complete = new CompleteMultipartUploadRequest
            {
                BucketName = _bucket,
                Key = destinationKey,
                UploadId = initiated.UploadId,
                PartETags = [.. parts]
            };
            if (sendCreateOnly) complete.IfNoneMatch = "*";
            else if (ifMatch is not null) complete.IfMatch = ifMatch;
            // Once sent, the completion is not cancelled: it may commit with its reply lost to the cancel.
            await CompleteMultipartAsync(complete).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = _bucket, Key = destinationKey, UploadId = initiated.UploadId }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception) { /* The bucket's lifecycle rules remove abandoned uploads. */ }
            throw;
        }
    }

    /// <summary>How many parts of a large copy run at once.</summary>
    internal const int MultipartCopyConcurrency = 4;

    /// <summary>The smallest part a large server-side copy uses (each part is a request; the data never passes through the client).</summary>
    internal long MultipartCopyMinPartBytes { get; init; } = 128L * 1024 * 1024;

    /// <summary>
    /// The part size for copying <paramref name="size"/> bytes: the larger of the preferred size and
    /// <paramref name="minimum"/>, larger still when needed to stay within 10,000 parts, and never above S3's 5 GiB part limit.
    /// </summary>
    internal static long MultipartCopyPartSize(long size, long preferred, long minimum = 128L * 1024 * 1024) =>
        Math.Min(5L * 1024 * 1024 * 1024, Math.Max(Math.Max(preferred, minimum), (size + 9_999) / 10_000));

    /// <summary>
    /// Completes a multipart upload, never cancelled once sent. When the reply to a completion that did commit is
    /// lost, the SDK's retry is refused (the upload is gone, or a condition no longer holds against our own object);
    /// the object is then read back, and if it carries the multipart ETag these parts produce, the upload is ours and
    /// succeeded. In a versioned bucket another writer may already have put a newer version on top; the key's versions
    /// are then searched for ours, so its version is reported rather than a failure.
    /// </summary>
    private async Task<CompleteMultipartUploadResponse> CompleteMultipartAsync(CompleteMultipartUploadRequest request)
    {
        try
        {
            return await _client.CompleteMultipartUploadAsync(request, CancellationToken.None).ConfigureAwait(false);
        }
        catch (AmazonS3Exception error) when (error.ErrorCode == "NoSuchUpload" || error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
        {
            var expected = MultipartETag(request.PartETags.Select(part => part.ETag));
            if (expected is null) throw;
            var current = await HeadAsync(request.Key, CancellationToken.None).ConfigureAwait(false);
            if (current is not null && string.Equals(current.ETag?.Trim('"'), expected, StringComparison.OrdinalIgnoreCase))
                return new CompleteMultipartUploadResponse { BucketName = request.BucketName, Key = request.Key, ETag = current.ETag, VersionId = current.VersionId };
            var version = await FindVersionAsync(request.Key, expected).ConfigureAwait(false);
            if (version is null) throw;
            return new CompleteMultipartUploadResponse { BucketName = request.BucketName, Key = request.Key, ETag = version.ETag, VersionId = version.VersionId };
        }
    }

    /// <summary>
    /// A recent version of <paramref name="key"/> with the given ETag, or null when there is none or the bucket
    /// cannot list versions (unversioned buckets list only the current object, which was already compared).
    /// </summary>
    private async Task<S3ObjectVersion?> FindVersionAsync(string key, string etag)
    {
        try
        {
            var versions = await _client.ListVersionsAsync(new ListVersionsRequest { BucketName = _bucket, Prefix = key, MaxKeys = 100 }, CancellationToken.None).ConfigureAwait(false);
            return (versions.Versions ?? []).FirstOrDefault(version =>
                version.Key == key && version.IsDeleteMarker != true && !string.IsNullOrEmpty(version.VersionId) && version.VersionId != "null" &&
                string.Equals(version.ETag?.Trim('"'), etag, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception) { return null; }
    }

    /// <summary>The ETag S3 gives a multipart upload of these parts (MD5 of the part MD5s, then the part count), or null when a part ETag is not an MD5.</summary>
    internal static string? MultipartETag(IEnumerable<string?> partETags)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var count = 0;
        foreach (var etag in partETags)
        {
            var hex = etag?.Trim().Trim('"');
            if (hex is not { Length: 32 } || !hex.All(Uri.IsHexDigit))
                return null;
            md5.AppendData(Convert.FromHexString(hex));
            count++;
        }
        return count == 0 ? null : $"{Convert.ToHexStringLower(md5.GetHashAndReset())}-{count.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Reads an object's headers, or null when it does not exist.</summary>
    private async Task<GetObjectMetadataResponse?> HeadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucket, Key = key }, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception error) when (IsNotFound(error))
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes a moved source only while it is still the version that was copied: its ETag (and version, when one
    /// was pinned) is compared first, and the delete carries <c>If-Match</c> where the server enforces it on deletes
    /// (elsewhere the comparison just before is the guard). Runs after the destination committed, so it is not cancelled.
    /// </summary>
    private async Task<Result> DeleteMovedSourceAsync(string sourceKey, S3PinnedSource copied)
    {
        try
        {
            var sendIfMatch = await ConditionStateAsync(S3ProbedCondition.DeleteMatch, CancellationToken.None).ConfigureAwait(false) == S3ConditionState.Enforced;
            var current = await HeadAsync(sourceKey, CancellationToken.None).ConfigureAwait(false);
            if (current is null)
                return Result.Success();
            if (!StagedWriter.SameETag(copied.ETag, current.ETag) ||
                (copied.VersionId is not null && !string.Equals(copied.VersionId, current.VersionId, StringComparison.Ordinal)))
                return Result.Failure(StorageErrors.Conflict("The S3 source changed after it was copied, so it was not deleted."));
            var request = new DeleteObjectRequest { BucketName = _bucket, Key = sourceKey };
            if (sendIfMatch) request.IfMatch = current.ETag;
            await _client.DeleteObjectAsync(request, CancellationToken.None).ConfigureAwait(false);
            return Result.Success();
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return Result.Failure(StorageErrors.Conflict("The S3 source changed after it was copied, so it was not deleted."));
        }
        catch (Exception error) { return Result.Failure(Map(error, "Delete moved S3 source")); }
    }

    private static bool IsNonStandard(S3StorageClass? storageClass) =>
        storageClass is not null && !string.IsNullOrEmpty(storageClass.Value) && storageClass != S3StorageClass.Standard;

    private static bool IsEncrypted(ServerSideEncryptionMethod? method) =>
        method is not null && !string.IsNullOrEmpty(method.Value) && method != ServerSideEncryptionMethod.None;

    /// <summary>
    /// Whether an upload's condition (create-only, or <see cref="StorageUploadOptions.Condition"/>) can be sent with the
    /// request that commits: the server enforces it there. Otherwise it is checked just before instead. An upload
    /// without a condition needs no probe.
    /// </summary>
    private async ValueTask<bool> UploadConditionsEnforcedAsync(StorageUploadOptions options, CancellationToken cancellationToken)
    {
        if (options.Overwrite && options.Condition is null or { IsEmpty: true }) return true;
        var condition = options.Overwrite ? S3ProbedCondition.PutMatch : S3ProbedCondition.PutCreateOnly;
        return await ConditionStateAsync(condition, cancellationToken).ConfigureAwait(false) == S3ConditionState.Enforced;
    }

    /// <inheritdoc />
    /// <remarks>
    /// With <see cref="S3ConditionalRequestSupport.Auto"/> the answer comes from the connection's probe (run here when
    /// it has not run yet): <see cref="StorageConditionEnforcement.Atomic"/> only for a condition the server was seen to
    /// enforce on the request that commits (<c>PutObject</c>/<c>CompleteMultipartUpload</c> for uploads,
    /// <c>CopyObject</c> for server-side copies, <c>DeleteObject</c> for deletes). MinIO, for one, ignores both
    /// conditions on <c>CopyObject</c> and <c>If-Match</c> on <c>DeleteObject</c>. An upload with a version condition is
    /// staged and promoted with <c>CopyObject</c> unless the server enforces <c>If-Match</c> on both requests (the
    /// <see cref="StorageFeature.ConditionalUpdate"/> flag), so it is <see cref="StorageConditionEnforcement.Atomic"/>
    /// only then.
    /// </remarks>
    async ValueTask<StorageConditionEnforcement> IStorageConditionEnforcementSource.GetEnforcementAsync(StorageConditionKind kind, bool serverSideCopy, CancellationToken cancellationToken)
    {
        var probed = kind switch
        {
            StorageConditionKind.CreateOnly => serverSideCopy ? S3ProbedCondition.CopyCreateOnly : S3ProbedCondition.PutCreateOnly,
            StorageConditionKind.MatchVersion => serverSideCopy ? S3ProbedCondition.CopyMatch : S3ProbedCondition.PutMatch,
            _ => S3ProbedCondition.DeleteMatch
        };
        var enforced = await ConditionStateAsync(probed, cancellationToken).ConfigureAwait(false) == S3ConditionState.Enforced;
        // As StorageTransferPipeline.NeedsConditionStaging decides: the promote's CopyObject must enforce it too.
        if (enforced && kind == StorageConditionKind.MatchVersion && !serverSideCopy)
            enforced = await ConditionStateAsync(S3ProbedCondition.CopyMatch, cancellationToken).ConfigureAwait(false) == S3ConditionState.Enforced;
        return enforced ? StorageConditionEnforcement.Atomic : StorageConditionEnforcement.CheckedBeforeCommit;
    }

    /// <summary>How long after a probe that could not finish the next one may run; doubled for each further failure, up to 32 times.</summary>
    internal TimeSpan InconclusiveProbeBackoff { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The clock the probe back-off is measured with.</summary>
    internal Func<DateTimeOffset> Clock { get; init; } = static () => DateTimeOffset.UtcNow;

    private int _inconclusiveProbes;

    /// <summary>
    /// Whether the server enforces a condition: by setting, or by the connection's probe. A conclusive probe is kept
    /// for the connection's life. One that could not finish is kept too, as "not enforced", until its back-off ends,
    /// so a failing server is not probed again by every conditional request.
    /// </summary>
    private async ValueTask<S3ConditionState> ConditionStateAsync(S3ProbedCondition condition, CancellationToken cancellationToken)
    {
        switch (ConditionalRequests)
        {
            case S3ConditionalRequestSupport.NotEnforced:
                return S3ConditionState.Ignored;
            case S3ConditionalRequestSupport.Enforced:
                return S3ConditionState.Enforced;
        }
        var probe = Volatile.Read(ref _conditionProbe);
        if (probe is null || (!probe.Conclusive && Clock() >= probe.RetryAt))
        {
            await _probeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                probe = _conditionProbe;
                if (probe is null || (!probe.Conclusive && Clock() >= probe.RetryAt))
                {
                    probe = await ProbeConditionsAsync(cancellationToken).ConfigureAwait(false);
                    if (!probe.Conclusive)
                    {
                        var failures = Math.Min(++_inconclusiveProbes, 6);
                        probe = probe with { RetryAt = Clock() + InconclusiveProbeBackoff * (1 << (failures - 1)) };
                    }
                    Volatile.Write(ref _conditionProbe, probe);
                }
            }
            finally { _probeGate.Release(); }
        }
        return probe.StateOf(condition);
    }

    /// <summary>
    /// Asks the server whether it enforces <c>If-None-Match</c> and <c>If-Match</c> on <c>PutObject</c> and on
    /// <c>CopyObject</c>, and <c>If-Match</c> on <c>DeleteObject</c>, with two one-byte <c>.cl-storage-probe-*</c>
    /// objects under the prefix that are removed afterwards. A request answered 412 enforces its condition; one that
    /// succeeds ignores it; one answered 400 or 501 rejects the header. <c>CompleteMultipartUpload</c> is taken to
    /// behave as <c>PutObject</c> does.
    /// </summary>
    /// <remarks>
    /// The probe's requests are real writes (four PUTs, two COPYs, three DELETEs): on a versioned bucket they leave
    /// noncurrent versions and delete markers of the probe objects, on an Object Lock bucket versions that cannot be
    /// deleted until their retention ends, and they raise event notifications and replication like any write. See
    /// <see cref="S3ConditionalRequestSupport.Auto"/>.
    /// </remarks>
    private async Task<S3ConditionProbe> ProbeConditionsAsync(CancellationToken cancellationToken)
    {
        var stem = $"{_keyPrefix}.cl-storage-probe-{Guid.NewGuid():N}";
        var first = stem + "-a";
        var second = stem + "-b";
        const string WrongETag = "\"00000000000000000000000000000000\"";
        try
        {
            foreach (var key in new[] { first, second })
            {
                await using var content = new MemoryStream([0x2A], writable: false);
                await _client.PutObjectAsync(NewPutRequest(key, content, "application/octet-stream", null), cancellationToken).ConfigureAwait(false);
            }
            var putCreateOnly = await ProbeAsync(async () =>
            {
                await using var content = new MemoryStream([0x2B], writable: false);
                var request = NewPutRequest(second, content, "application/octet-stream", null);
                request.IfNoneMatch = "*";
                await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
            var putMatch = await ProbeAsync(async () =>
            {
                await using var content = new MemoryStream([0x2C], writable: false);
                var request = NewPutRequest(second, content, "application/octet-stream", null);
                request.IfMatch = WrongETag;
                await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
            var copyCreateOnly = await ProbeAsync(() => _client.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = _bucket,
                SourceKey = first,
                DestinationBucket = _bucket,
                DestinationKey = second,
                IfNoneMatch = "*"
            }, cancellationToken)).ConfigureAwait(false);
            var copyMatch = await ProbeAsync(() => _client.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = _bucket,
                SourceKey = first,
                DestinationBucket = _bucket,
                DestinationKey = second,
                IfMatch = WrongETag
            }, cancellationToken)).ConfigureAwait(false);
            var delete = await ProbeAsync(() => _client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = _bucket,
                Key = second,
                IfMatch = WrongETag
            }, cancellationToken)).ConfigureAwait(false);
            return new S3ConditionProbe(putCreateOnly, putMatch, copyCreateOnly, copyMatch, delete, Conclusive: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Without an answer nothing is assumed enforced, and no header is sent that the server might reject.
            return new S3ConditionProbe(S3ConditionState.Unknown, S3ConditionState.Unknown, S3ConditionState.Unknown,
                S3ConditionState.Unknown, S3ConditionState.Unknown, Conclusive: false);
        }
        finally
        {
            foreach (var key in new[] { first, second })
            {
                try { await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = key }, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { /* A probe object left behind is an internal name that listings hide. */ }
            }
        }
    }

    private static async Task<S3ConditionState> ProbeAsync(Func<Task> request)
    {
        try
        {
            await request().ConfigureAwait(false);
            return S3ConditionState.Ignored;
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return S3ConditionState.Enforced;
        }
        catch (AmazonS3Exception error) when (error.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotImplemented)
        {
            return S3ConditionState.Rejected;
        }
    }

    private Result<string> Normalize(string path) => StoragePath.Normalize(path);
    private Result<string> NormalizeRequired(string path)
    {
        var result = Normalize(path);
        return result.IsFailure || result.Value!.Length > 0
            ? result
            : Result<string>.Failure(StorageErrors.InvalidPath("A non-root storage path is required."));
    }

    private string ToKey(string path) => _keyPrefix + path;
    private string ToDirectoryPrefix(string path) => path.Length == 0 ? _keyPrefix : ToKey(path).TrimEnd('/') + "/";
    private string FromKey(string key) => _keyPrefix.Length == 0 ? key : key.StartsWith(_keyPrefix, StringComparison.Ordinal) ? key[_keyPrefix.Length..] : string.Empty;
    private static string NameOf(string path) => path.Split('/')[^1];
    /// <summary>
    /// User metadata under the names it was written with: the SDK reports keys with the <c>x-amz-meta-</c>
    /// header prefix, which would otherwise leak into every read and round trip.
    /// </summary>
    private static Dictionary<string, string> UserMetadata(MetadataCollection metadata) =>
        metadata.Keys.ToDictionary(
            key => key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase) ? key["x-amz-meta-".Length..] : key,
            key => metadata[key],
            StringComparer.Ordinal);

    private static StorageItem DirectoryItem(string path) => new() { Path = path, Name = path.Length == 0 ? string.Empty : NameOf(path), ItemType = StorageItemType.Directory };

    private static StorageItem FromMetadata(string path, GetObjectMetadataResponse response) => new()
    {
        Path = path,
        Name = NameOf(path),
        ItemType = StorageItemType.File,
        Size = response.ContentLength,
        LastModified = response.LastModified.HasValue ? new DateTimeOffset(response.LastModified.Value) : null,
        ContentType = response.Headers.ContentType,
        ETag = response.ETag?.Trim('"'),
        VersionId = response.VersionId,
        Metadata = UserMetadata(response.Metadata)
    };

    private static StorageItem FromListedObject(string path, S3Object item) => new()
    {
        Path = path,
        Name = NameOf(path),
        ItemType = StorageItemType.File,
        Size = item.Size,
        LastModified = item.LastModified.HasValue ? new DateTimeOffset(item.LastModified.Value) : null,
        ETag = item.ETag?.Trim('"')
    };

    /// <inheritdoc />
    /// <remarks>
    /// SHA-256 comes from a stored <c>x-amz-checksum-sha256</c>. MD5 comes from the ETag, which equals
    /// the content MD5 only for single-part uploads without SSE-KMS, so other ETags are not reported.
    /// </remarks>
    public async Task<Result<StorageChecksum>> GetServerChecksumAsync(string path, StorageChecksumAlgorithm algorithm, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(path);
        if (normalized.IsFailure) return Result<StorageChecksum>.Failure(normalized.Error!);
        if (algorithm is not (StorageChecksumAlgorithm.Md5 or StorageChecksumAlgorithm.Sha256))
            return ProviderChecksums.Unavailable(algorithm, $"S3 does not store {algorithm} checksums.");
        try
        {
            var response = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _bucket,
                Key = ToKey(normalized.Value!),
                ChecksumMode = ChecksumMode.ENABLED
            }, cancellationToken).ConfigureAwait(false);
            if (algorithm == StorageChecksumAlgorithm.Sha256)
                return ProviderChecksums.FromBase64(algorithm, response.ChecksumSHA256);
            var encryption = response.ServerSideEncryptionMethod?.Value ?? string.Empty;
            if (encryption.StartsWith("aws:kms", StringComparison.Ordinal))
                return ProviderChecksums.Unavailable(algorithm, "An SSE-KMS object's ETag is not its MD5.");
            // SSE-C: the ETag is derived from the encrypted content, not the plaintext. (Servers that answer a HEAD
            // without the customer key say so here; AWS refuses that HEAD with 400, handled below.)
            if (response.ServerSideEncryptionCustomerMethod is { Value.Length: > 0 } customer && customer != ServerSideEncryptionCustomerMethod.None)
                return ProviderChecksums.Unavailable(algorithm, "An SSE-C object's ETag is not its MD5.");
            return ProviderChecksums.FromHex(algorithm, response.ETag);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.BadRequest)
        {
            // AWS answers a HEAD of an SSE-C object without the customer key with 400 (a HEAD has no error body to
            // say more). Such an object's ETag is not its MD5 anyway, and this connection cannot send the key.
            return ProviderChecksums.Unavailable(algorithm,
                "S3 refused to read the object's headers (400): it is probably encrypted with a customer key (SSE-C), whose ETag is not its MD5.");
        }
        catch (Exception error) { return Result<StorageChecksum>.Failure(Map(error, "Get S3 checksum")); }
    }

    private static bool IsNotFound(AmazonS3Exception error) => error.StatusCode == HttpStatusCode.NotFound ||
        error.ErrorCode is "NoSuchKey" or "NoSuchBucket" or "NotFound";

    /// <summary>Maps an S3 SDK or transport exception to a storage error (internal for tests).</summary>
    internal static Error Map(Exception exception, string operation)
    {
        if (exception is StorageMultipartLimitException)
            return StorageErrors.TooLarge($"{operation}: the multipart upload exceeds 10,000 parts.");
        if (exception is AmazonS3Exception s3)
        {
            var details = ProviderErrorMapper.Details(
                StorageErrorInfo.HttpStatusKey, ((int)s3.StatusCode).ToString(CultureInfo.InvariantCulture), s3.ErrorCode);
            if (!string.IsNullOrWhiteSpace(s3.RequestId))
                details += ";requestId=" + ProviderErrorMapper.SafeDetailValue(s3.RequestId);
            if (IsNotFound(s3)) return StorageErrors.NotFound($"{operation}: item was not found.", details);
            if (s3.ErrorCode is "InvalidAccessKeyId" or "SignatureDoesNotMatch" or "ExpiredToken" or "InvalidToken")
                return StorageErrors.AuthenticationFailed($"{operation}: S3 rejected the credentials.", details);
            if (s3.ErrorCode is "AccessDenied" or "AllAccessDisabled")
                return StorageErrors.PermissionDenied($"{operation}: access was denied.", details);
            if (s3.ErrorCode is "SlowDown" or "RequestLimitExceeded" or "ServiceUnavailable")
                return StorageErrors.ServerBusy($"{operation}: S3 is throttling requests.", details);
            if (s3.ErrorCode is "QuotaExceeded")
                return StorageErrors.QuotaExceeded($"{operation}: the S3 storage quota was exceeded.", details);
            if (s3.ErrorCode is "EntityTooLarge")
                return StorageErrors.TooLarge($"{operation}: S3 rejected the object size.", details);
            if ((int)s3.StatusCode > 0)
                return ProviderErrorMapper.FromHttpStatus((int)s3.StatusCode, operation, "S3", providerCode: s3.ErrorCode);
            return StorageErrors.ProviderError($"{operation}: S3 request failed.", details);
        }
        if (ProviderErrorMapper.FromTransport(exception, operation, "S3") is { } transport)
            return transport;
        // The SDK refused the request before sending it (configuration, signing, checksums). Its messages are
        // written by the SDK, not the server, and say what to fix; keep a sanitized copy.
        if (exception is AmazonClientException client && ProviderErrorMapper.SafeProviderMessage(client.Message) is { Length: > 0 } reason)
            return StorageErrors.ProviderError($"{operation}: the S3 client refused the request: {reason}", ProviderErrorMapper.ExceptionDetails(exception));
        return StorageErrors.ProviderError($"{operation}: S3 provider failed.", ProviderErrorMapper.ExceptionDetails(exception));
    }

    private static string EncodeVersionContinuation(S3VersionContinuation continuation) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(continuation)));

    private static Result<S3VersionContinuation> DecodeVersionContinuation(string? continuationToken)
    {
        if (string.IsNullOrEmpty(continuationToken))
            return Result<S3VersionContinuation>.Success(new S3VersionContinuation(null, null));
        try
        {
            var value = JsonSerializer.Deserialize<S3VersionContinuation>(
                Encoding.UTF8.GetString(Convert.FromBase64String(continuationToken)));
            return value is null
                ? Result<S3VersionContinuation>.Failure(StorageErrors.InvalidPath(
                    "The S3 version continuation token is invalid."))
                : Result<S3VersionContinuation>.Success(value);
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            return Result<S3VersionContinuation>.Failure(StorageErrors.InvalidPath(
                "The S3 version continuation token is invalid."));
        }
    }

    private sealed record S3UploadCompletion(string? ETag, string? VersionId, long? Bytes);
    /// <summary>The source version a copy was pinned to.</summary>
    private sealed record S3PinnedSource(string? ETag, string? VersionId);
    /// <summary>What a connection's server does with upload, copy, and delete conditions.</summary>
    private sealed record S3ConditionProbe(
        S3ConditionState PutCreateOnly,
        S3ConditionState PutMatch,
        S3ConditionState CopyCreateOnly,
        S3ConditionState CopyMatch,
        S3ConditionState DeleteMatch,
        bool Conclusive)
    {
        /// <summary>For a probe that could not finish: when the next may run.</summary>
        public DateTimeOffset RetryAt { get; init; }

        public S3ConditionState StateOf(S3ProbedCondition condition) => condition switch
        {
            S3ProbedCondition.PutCreateOnly => PutCreateOnly,
            S3ProbedCondition.PutMatch => PutMatch,
            S3ProbedCondition.CopyCreateOnly => CopyCreateOnly,
            S3ProbedCondition.CopyMatch => CopyMatch,
            _ => DeleteMatch
        };

        /// <summary>The capabilities with only the conditional flags this server enforces on every request that commits.</summary>
        public StorageCapabilities Capabilities(StorageCapabilities all)
        {
            var features = all.Features;
            if (PutCreateOnly != S3ConditionState.Enforced || CopyCreateOnly != S3ConditionState.Enforced) features &= ~StorageFeature.ConditionalCreate;
            if (PutMatch != S3ConditionState.Enforced || CopyMatch != S3ConditionState.Enforced) features &= ~StorageFeature.ConditionalUpdate;
            if (DeleteMatch != S3ConditionState.Enforced) features &= ~StorageFeature.ConditionalDelete;
            return features == all.Features ? all : new StorageCapabilities(features, all.Limits);
        }
    }
    private enum S3ProbedCondition { PutCreateOnly, PutMatch, CopyCreateOnly, CopyMatch, DeleteMatch }
    private enum S3ConditionState { Enforced, Ignored, Rejected, Unknown }
    private sealed record S3VersionContinuation(string? KeyMarker, string? VersionIdMarker);
    private readonly record struct S3PartRead(int Count, bool EndOfStream);
    private sealed class StorageMultipartLimitException : Exception { }
}
