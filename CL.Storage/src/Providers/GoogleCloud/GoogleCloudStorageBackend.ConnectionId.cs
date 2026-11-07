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

public sealed partial class GoogleCloudStorageBackend
{

    /// <inheritdoc />
    public string ConnectionId { get; }
    /// <inheritdoc />
    public StorageProvider Provider => StorageProvider.GoogleCloudStorage;
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
            var item = await _client.GetObjectAsync(_bucket, ToKey(normalized.Value), cancellationToken: cancellationToken).ConfigureAwait(false);
            return Result<StorageItem>.Success(ToItem(normalized.Value, item));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (GoogleApiException error) when (error.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return await GetDirectoryInfoAsync(normalized.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Get Google Cloud object info")); }
    }

    /// <inheritdoc />
    /// <remarks>Google Cloud Storage computes an MD5 for every non-composite object.</remarks>
    public async Task<Result<StorageChecksum>> GetServerChecksumAsync(string path, StorageChecksumAlgorithm algorithm, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(path);
        if (normalized.IsFailure) return Result<StorageChecksum>.Failure(normalized.Error!);
        if (algorithm != StorageChecksumAlgorithm.Md5)
            return ProviderChecksums.Unavailable(algorithm, "Google Cloud Storage stores only MD5 and CRC32C checksums.");
        try
        {
            var item = await _client.GetObjectAsync(_bucket, ToKey(normalized.Value!), cancellationToken: cancellationToken).ConfigureAwait(false);
            return ProviderChecksums.FromBase64(algorithm, item.Md5Hash);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageChecksum>.Failure(Map(error, "Get Google Cloud checksum")); }
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
        var normalized = Normalize(path);
        if (normalized.IsFailure) return Result<StoragePage>.Failure(normalized.Error!);
        if (normalized.Value!.Length > 0)
        {
            var directory = await GetInfoAsync(normalized.Value, cancellationToken).ConfigureAwait(false);
            if (directory.IsFailure) return Result<StoragePage>.Failure(directory.Error!);
            if (directory.Value!.ItemType != StorageItemType.Directory)
                return Result<StoragePage>.Failure(StorageErrors.Conflict(
                    "A Google Cloud object cannot be listed as a directory."));
        }
        var listingPath = normalized.Value!;
        var listingPrefix = DirectoryPrefix(listingPath);
        var continuation = DecodeContinuationToken(options.ContinuationToken);
        if (continuation.IsFailure)
            return Result<StoragePage>.Failure(continuation.Error!);
        try
        {
            var items = new Dictionary<string, StorageItem>(StringComparer.Ordinal);
            var providerPageSize = Math.Min(options.PageSize, 1_000);
            // A flat listing asks the server for one level ("/" delimiter): sub-folders come back as prefixes instead
            // of every descendant object being scanned client-side.
            var listing = _client.ListObjectsAsync(_bucket, listingPrefix, new ListObjectsOptions
            {
                PageSize = providerPageSize,
                PageToken = continuation.Value!.ProviderPageToken,
                Delimiter = options.Recursive ? null : "/"
            });
            IEnumerable<GcsObject> providerItems;
            string? providerNextPageToken;
            if (options.Recursive)
            {
                var providerPage = await listing.ReadPageAsync(providerPageSize, cancellationToken).ConfigureAwait(false);
                providerItems = providerPage;
                providerNextPageToken = providerPage.NextPageToken;
            }
            else
            {
                // Prefixes are only on the raw response; one raw response is one provider page.
                var responses = listing.AsRawResponses().GetAsyncEnumerator(cancellationToken);
                await using var responsesLifetime = responses.ConfigureAwait(false);
                var response = await responses.MoveNextAsync().ConfigureAwait(false) ? responses.Current : null;
                providerItems = response?.Items ?? [];
                providerNextPageToken = response?.NextPageToken;
                foreach (var prefix in response?.Prefixes ?? [])
                {
                    // An unaddressable name ("a/../b", "a//b") is skipped: the library could never name it back.
                    if (FromKey(prefix) is not { } folder) continue;
                    var directory = folder.TrimEnd('/');
                    if (directory.Length > 0 && !string.Equals(directory, listingPath, StringComparison.Ordinal))
                        items[directory] = DirectoryItem(directory);
                }
            }
            // The last key before this provider page, so folders straddling a page boundary are reported once.
            var previous = continuation.Value.Previous;
            foreach (var item in providerItems)
            {
                if (FromKey(item.Name) is not { Length: > 0 } pathValue) continue;
                if (string.Equals(pathValue.TrimEnd('/'), listingPath, StringComparison.Ordinal)) continue;
                if (options.Recursive)
                {
                    var parents = new List<StorageItem>();
                    ImplicitDirectories.AddParents(parents, pathValue, listingPath, previous, DirectoryItem);
                    foreach (var parent in parents) items[parent.Path] = parent;
                    // The key itself, so a folder marker ("a/b/") that ends a page still covers "a/b" on the next.
                    previous = pathValue;
                    if (pathValue.EndsWith('/')) items[pathValue.TrimEnd('/')] = DirectoryItem(pathValue.TrimEnd('/'));
                    else items[pathValue] = ToItem(pathValue, item);
                }
                else
                {
                    var remainder = pathValue[(listingPath.Length == 0 ? 0 : listingPath.Length + 1)..];
                    var slash = remainder.IndexOf('/');
                    if (slash >= 0)
                    {
                        var directory = listingPath.Length == 0 ? remainder[..slash] : listingPath + "/" + remainder[..slash];
                        items[directory] = DirectoryItem(directory);
                    }
                    else
                    {
                        items[pathValue] = ToItem(pathValue, item);
                    }
                }
            }
            var ordered = items.Values.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
            if (continuation.Value.Skip > ordered.Length)
                return Result<StoragePage>.Failure(StorageErrors.InvalidPath(
                    "The Google Cloud continuation token offset is invalid."));
            var pageItems = ordered.Skip(continuation.Value.Skip).Take(options.PageSize).ToArray();
            string? nextToken;
            var nextSkip = continuation.Value.Skip + pageItems.Length;
            if (nextSkip < ordered.Length)
            {
                nextToken = EncodeContinuationToken(new GcsContinuationToken(
                    continuation.Value.ProviderPageToken,
                    nextSkip,
                    continuation.Value.Previous));
            }
            else
            {
                nextToken = string.IsNullOrEmpty(providerNextPageToken)
                    ? null
                    : EncodeContinuationToken(new GcsContinuationToken(providerNextPageToken, 0, previous));
            }
            return Result<StoragePage>.Success(new StoragePage(StorageListFilter.Apply(pageItems, options, listingPath), nextToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StoragePage>.Failure(Map(error, "List Google Cloud objects")); }
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
            await _client.UploadObjectAsync(
                _bucket,
                ToKey(normalized.Value!.TrimEnd('/') + "/"),
                "application/x-directory",
                empty,
                new UploadObjectOptions { ChunkSize = _uploadChunkSize },
                cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Create Google Cloud directory")); }
    }
}
