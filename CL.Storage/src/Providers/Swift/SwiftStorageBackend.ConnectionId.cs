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

public sealed partial class SwiftStorageBackend
{

    /// <inheritdoc />
    public string ConnectionId { get; }
    /// <inheritdoc />
    public StorageProvider Provider => StorageProvider.OpenStackSwift;
    /// <inheritdoc />
    public string Root { get; }
    /// <inheritdoc />
    public StorageCapabilities Capabilities => SwiftCapabilities;

    /// <inheritdoc />
    public async Task<Result<StorageItem>> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Normalize(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        if (normalized.Value!.Length == 0) return Result<StorageItem>.Success(DirectoryItem(string.Empty));
        try
        {
            using var response = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Head, ObjectUri(ToKey(normalized.Value))),
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return await GetDirectoryInfoAsync(normalized.Value, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Result<StorageItem>.Failure(FromStatus(response, "Get Swift object info"));
            return Result<StorageItem>.Success(ToItem(normalized.Value, response));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Get Swift object info")); }
    }

    /// <inheritdoc />
    /// <remarks>A Swift ETag is the content MD5, except for segmented large objects, which are not reported.</remarks>
    public async Task<Result<StorageChecksum>> GetServerChecksumAsync(string path, StorageChecksumAlgorithm algorithm, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(path);
        if (normalized.IsFailure) return Result<StorageChecksum>.Failure(normalized.Error!);
        if (algorithm != StorageChecksumAlgorithm.Md5)
            return ProviderChecksums.Unavailable(algorithm, "Swift stores only MD5 checksums.");
        try
        {
            using var response = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Head, ObjectUri(ToKey(normalized.Value!))),
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Result<StorageChecksum>.Failure(FromStatus(response, "Get Swift checksum"));
            if (response.Headers.Contains("X-Object-Manifest") || response.Headers.Contains("X-Static-Large-Object"))
                return ProviderChecksums.Unavailable(algorithm, "A segmented Swift object's ETag is not its MD5.");
            return ProviderChecksums.FromHex(algorithm, RawETag(response));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageChecksum>.Failure(Map(error, "Get Swift checksum")); }
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
                return Result<StoragePage>.Failure(StorageErrors.Conflict(
                    "A Swift object cannot be listed as a directory."));
        }
        try
        {
            var (nativeToken, previous) = options.Recursive
                ? ImplicitDirectories.Unwrap(options.ContinuationToken)
                : (options.ContinuationToken, null);
            var page = await ListProviderPageAsync(
                DirectoryPrefix(normalized.Value!),
                options.Recursive ? null : "/",
                options.PageSize,
                nativeToken,
                cancellationToken).ConfigureAwait(false);
            if (page.IsFailure) return Result<StoragePage>.Failure(page.Error!);
            var keys = new Dictionary<string, string>(StringComparer.Ordinal);
            var items = page.Value!.Items.Select(item =>
            {
                var providerPath = item.Subdirectory ?? item.Name!;
                // An unaddressable name ("a/../b", "a//b") is skipped: the library could never name it back.
                if (FromKey(providerPath) is not { } key) return null;
                var relative = key.TrimEnd('/');
                keys[relative] = key;
                return relative.Length == 0
                    ? null
                    : item.Subdirectory is not null || providerPath.EndsWith('/')
                        ? DirectoryItem(relative)
                        : new StorageItem
                        {
                            Path = relative,
                            Name = NameOf(relative),
                            ItemType = StorageItemType.File,
                            Size = item.Bytes,
                            LastModified = item.LastModified,
                            ContentType = item.ContentType,
                            ETag = item.Hash
                        };
            }).Where(item => item is not null).Cast<StorageItem>().ToArray();
            if (options.Recursive)
            {
                var withFolders = new List<StorageItem>();
                foreach (var item in items)
                {
                    ImplicitDirectories.AddParents(withFolders, item.Path, normalized.Value!, previous, DirectoryItem);
                    // The key itself, so a folder marker ("a/b/") that ends a page still covers "a/b" on the next.
                    previous = keys.GetValueOrDefault(item.Path, item.Path);
                    withFolders.Add(item);
                }
                items = [.. withFolders.GroupBy(item => item.Path, StringComparer.Ordinal).Select(group => group.First())
                    .OrderBy(item => item.Path, StringComparer.Ordinal)];
            }
            var next = options.Recursive ? ImplicitDirectories.Wrap(page.Value.NextMarker, previous) : page.Value.NextMarker;
            return Result<StoragePage>.Success(new StoragePage(StorageListFilter.Apply(items, options, normalized.Value!), next));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StoragePage>.Failure(Map(error, "List Swift objects")); }
    }

    /// <inheritdoc />
    public async Task<Result> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Normalize(path);
        if (normalized.IsFailure) return Result.Failure(normalized.Error!);
        if (normalized.Value!.Length == 0) return Result.Success();
        try
        {
            using var response = await SendAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Put, ObjectUri(ToKey(normalized.Value!.TrimEnd('/') + "/")));
                request.Content = new ByteArrayContent([]);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-directory");
                return request;
            }, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? Result.Success()
                : Result.Failure(FromStatus(response, "Create Swift directory"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Create Swift directory")); }
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
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        var sourceStart = source.CanSeek ? source.Position : (long?)null;
        try
        {
            if (options.Overwrite && options.Condition is { IsEmpty: false } condition)
            {
                // Swift ignores If-Match on PUT: the condition is checked here, just before the upload.
                var current = await GetInfoAsync(normalized.Value!, cancellationToken).ConfigureAwait(false);
                if (current.IsFailure)
                    return Result<StorageItem>.Failure(current.Error!.Code == StorageErrors.NotFoundCode
                        ? StorageErrors.Conflict("The Swift object no longer exists for the requested upload condition.")
                        : current.Error!);
                var check = CheckCondition(current.Value!, condition, "upload");
                if (check.IsFailure) return Result<StorageItem>.Failure(check.Error!);
            }
            using var response = await SendAsync(() =>
            {
                if (sourceStart.HasValue) source.Position = sourceStart.Value;
                var request = new HttpRequestMessage(HttpMethod.Put, ObjectUri(ToKey(normalized.Value!)));
                request.Content = new StreamContent(new NonDisposingReadStream(source));
                if (!string.IsNullOrWhiteSpace(options.ContentType))
                    request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(options.ContentType);
                if (!options.Overwrite) request.Headers.TryAddWithoutValidation("If-None-Match", "*");
                foreach (var (name, value) in options.Metadata)
                    request.Headers.TryAddWithoutValidation("X-Object-Meta-" + name, value);
                return request;
            }, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Result<StorageItem>.Failure(FromStatus(response, "Upload Swift object"));
            return await GetInfoAsync(normalized.Value!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Upload Swift object")); }
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
}
