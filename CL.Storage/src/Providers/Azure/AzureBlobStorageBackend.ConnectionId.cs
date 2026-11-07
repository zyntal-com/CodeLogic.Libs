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

public sealed partial class AzureBlobStorageBackend
{

    /// <inheritdoc />
    public string ConnectionId { get; }
    /// <inheritdoc />
    public StorageProvider Provider => StorageProvider.AzureBlob;
    /// <inheritdoc />
    public string Root { get; }
    /// <inheritdoc />
    public StorageCapabilities Capabilities => _capabilities;

    /// <inheritdoc />
    public async Task<Result<StorageItem>> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Normalize(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        if (normalized.Value!.Length == 0) return Result<StorageItem>.Success(DirectoryItem(string.Empty));
        try
        {
            var properties = await Blob(normalized.Value).GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return Result<StorageItem>.Success(ToItem(normalized.Value, properties.Value));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (RequestFailedException error) when (error.Status == 404)
        {
            return await GetDirectoryInfoAsync(normalized.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Get Azure blob info")); }
    }

    /// <inheritdoc />
    /// <remarks>Azure stores the MD5 (<c>Content-MD5</c>) of blobs uploaded in a single request.</remarks>
    public async Task<Result<StorageChecksum>> GetServerChecksumAsync(string path, StorageChecksumAlgorithm algorithm, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(path);
        if (normalized.IsFailure) return Result<StorageChecksum>.Failure(normalized.Error!);
        if (algorithm != StorageChecksumAlgorithm.Md5)
            return ProviderChecksums.Unavailable(algorithm, $"Azure Blob stores only MD5 checksums.");
        try
        {
            var properties = await Blob(normalized.Value!).GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return ProviderChecksums.FromBytes(algorithm, properties.Value.ContentHash);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageChecksum>.Failure(Map(error, "Get Azure blob checksum")); }
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
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StoragePage>.Failure(validation.Error!);
        if (ProviderPaging.RecursiveTokenOnFlatListing(options) is { } mixed) return Result<StoragePage>.Failure(mixed);
        var normalized = Normalize(path);
        if (normalized.IsFailure) return Result<StoragePage>.Failure(normalized.Error!);
        if (normalized.Value!.Length > 0)
        {
            var directory = await GetInfoAsync(normalized.Value, cancellationToken).ConfigureAwait(false);
            if (directory.IsFailure) return Result<StoragePage>.Failure(directory.Error!);
            if (directory.Value!.ItemType != StorageItemType.Directory)
                return Result<StoragePage>.Failure(StorageErrors.Conflict("An Azure blob cannot be listed as a directory."));
        }
        var prefix = DirectoryPrefix(normalized.Value!);
        try
        {
            if (options.Recursive)
            {
                var (nativeToken, previous) = ImplicitDirectories.Unwrap(options.ContinuationToken);
                await foreach (var page in _container.GetBlobsAsync(
                    BlobTraits.Metadata,
                    BlobStates.None,
                    prefix,
                    cancellationToken).AsPages(nativeToken, options.PageSize).ConfigureAwait(false))
                {
                    var items = new List<StorageItem>();
                    foreach (var blob in page.Values)
                    {
                        if (ToItem(blob) is not { } item) continue;
                        ImplicitDirectories.AddParents(items, item.Path, normalized.Value!, previous, DirectoryItem);
                        // The key itself, so a folder marker ("a/b/") that ends a page still covers "a/b" on the next.
                        previous = FromKey(blob.Name) ?? item.Path;
                        items.Add(item);
                    }
                    var unique = items.GroupBy(item => item.Path, StringComparer.Ordinal).Select(group => group.First())
                        .OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
                    return Result<StoragePage>.Success(new StoragePage(
                        StorageListFilter.Apply(unique, options, normalized.Value!),
                        ImplicitDirectories.Wrap(page.ContinuationToken, previous)));
                }
            }
            else
            {
                await foreach (var page in _container.GetBlobsByHierarchyAsync(
                    BlobTraits.Metadata,
                    BlobStates.None,
                    delimiter: "/",
                    prefix,
                    cancellationToken).AsPages(options.ContinuationToken, options.PageSize).ConfigureAwait(false))
                {
                    var items = page.Values.Select(ToItem).Where(item => item is not null).Cast<StorageItem>().ToArray();
                    return Result<StoragePage>.Success(new StoragePage(StorageListFilter.Apply(items, options), page.ContinuationToken));
                }
            }
            return Result<StoragePage>.Success(new StoragePage([], null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StoragePage>.Failure(Map(error, "List Azure blobs")); }
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
            await Blob(normalized.Value!.TrimEnd('/') + "/").UploadAsync(empty, overwrite: true, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Create Azure blob directory")); }
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
        if (StorageOptionValidation.MetadataSizeBytes(options.Metadata) > 8 * 1024)
            return Result<StorageItem>.Failure(StorageErrors.TooLarge("Azure Blob metadata exceeds the 8 KiB limit."));
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        var normalizedPath = normalized.Value!;
        try
        {
            BlobRequestConditions? conditions;
            if (!options.Overwrite)
            {
                conditions = new BlobRequestConditions { IfNoneMatch = ETag.All };
            }
            else if (options.Condition is { IsEmpty: false } condition)
            {
                ETag? ifMatch = condition.ExpectedETag is null
                    ? null
                    : new ETag(condition.ExpectedETag);
                if (condition.ExpectedVersionId is not null)
                {
                    var current = (await Blob(normalizedPath).GetPropertiesAsync(
                        cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
                    var conditionResult = ValidateCurrentCondition(
                        ToItem(normalizedPath, current),
                        condition,
                        "Azure blob");
                    if (conditionResult.IsFailure)
                        return Result<StorageItem>.Failure(conditionResult.Error!);
                    ifMatch ??= current.ETag;
                }
                conditions = new BlobRequestConditions { IfMatch = ifMatch };
            }
            else
            {
                conditions = null;
            }
            var uploadOptions = new BlobUploadOptions
            {
                HttpHeaders = string.IsNullOrWhiteSpace(options.ContentType) ? null : new BlobHttpHeaders { ContentType = options.ContentType },
                Metadata = options.Metadata.Count == 0 ? null : new Dictionary<string, string>(options.Metadata, StringComparer.Ordinal),
                Conditions = conditions
            };
            await Blob(normalizedPath).UploadAsync(source, uploadOptions, cancellationToken).ConfigureAwait(false);
            return await GetInfoAsync(normalizedPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (RequestFailedException error) when (!options.Overwrite && error.Status is 409 or 412)
        {
            return Result<StorageItem>.Failure(StorageErrors.Conflict("The Azure blob destination already exists."));
        }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Upload Azure blob")); }
    }

    /// <inheritdoc />
    public async Task<Result<StorageItem>> UploadBytesAsync(string path, byte[] content, StorageUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var source = new MemoryStream(content, writable: false);
        await using var sourceLifetime = source.ConfigureAwait(false);
        return await UploadAsync(path, source, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<Stream>> DownloadAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default) =>
        await StorageTransferPipeline.MeterAsync(this, path, await DownloadUnmeteredAsync(path, options, cancellationToken).ConfigureAwait(false), options, cancellationToken).ConfigureAwait(false);

    private async Task<Result<Stream>> DownloadUnmeteredAsync(string path, StorageDownloadOptions? options, CancellationToken cancellationToken)
    {
        options ??= new StorageDownloadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<Stream>.Failure(validation.Error!);
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<Stream>.Failure(normalized.Error!);
        try
        {
            BlobDownloadOptions? downloadOptions = null;
            if (options.Offset > 0 || options.Length.HasValue)
                downloadOptions = new BlobDownloadOptions { Range = new HttpRange(options.Offset, options.Length) };
            var blob = Blob(normalized.Value!);
            if (options.VersionId is not null)
                blob = blob.WithVersion(options.VersionId);
            var response = await blob.DownloadStreamingAsync(downloadOptions, cancellationToken).ConfigureAwait(false);
            return Result<Stream>.Success(response.Value.Content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<Stream>.Failure(Map(error, "Download Azure blob")); }
    }
}
