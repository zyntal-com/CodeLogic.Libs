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

public sealed partial class S3StorageBackend
{

    /// <inheritdoc />
    public string ConnectionId { get; }
    /// <inheritdoc />
    public StorageProvider Provider => StorageProvider.S3;

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
                // An unaddressable key ("a/../b", "a//b") is skipped: the library could never name it back.
                if (FromKey(prefix) is not { } folder) continue;
                var relative = folder.TrimEnd('/');
                if (relative.Length > 0) items.Add(DirectoryItem(relative));
            }
            foreach (var item in response.S3Objects ?? [])
            {
                if (FromKey(item.Key) is not { Length: > 0 } relative) continue;
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
        var empty = new MemoryStream([]);
        await using var emptyLifetime = empty.ConfigureAwait(false);
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
        var stream = new MemoryStream(content, writable: false);
        await using var streamLifetime = stream.ConfigureAwait(false);
        return await UploadAsync(path, stream, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<Stream>> DownloadAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default) =>
        await StorageTransferPipeline.MeterAsync(this, path, await DownloadUnmeteredAsync(path, options, cancellationToken).ConfigureAwait(false), options, cancellationToken).ConfigureAwait(false);
}
