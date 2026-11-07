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
public sealed class SwiftStorageBackend : IStorageBackend, IStorageMetadataService, IStorageChecksumService, IStorageConditionEnforcementSource
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
        await using var source = new MemoryStream(content, writable: false);
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
        HttpResponseMessage? response = null;
        try
        {
            response = await SendAsync(() =>
            {
                // A container with object versioning (X-Versions-Enabled) serves an older version by its id.
                var request = new HttpRequestMessage(HttpMethod.Get, ObjectUri(ToKey(normalized.Value!), options.VersionId));
                if (options.Offset > 0 || options.Length.HasValue)
                {
                    var end = options.Length.HasValue ? options.Offset + options.Length.Value - 1 : (long?)null;
                    request.Headers.Range = new RangeHeaderValue(options.Offset, end);
                }
                return request;
            }, cancellationToken, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = FromStatus(response, "Download Swift object");
                response.Dispose();
                response = null;
                return Result<Stream>.Failure(error);
            }
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var owned = new OwnedResourceStream(stream, response);
            response = null;
            return Result<Stream>.Success(owned);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<Stream>.Failure(Map(error, "Download Swift object")); }
        finally { response?.Dispose(); }
    }

    /// <inheritdoc />
    public async Task<Result<byte[]>> DownloadBytesAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageDownloadOptions();
        var limit = options.MaxBufferedBytes ?? _maxBufferedDownloadBytes;
        var download = await DownloadAsync(path, options, cancellationToken).ConfigureAwait(false);
        if (download.IsFailure) return Result<byte[]>.Failure(download.Error!);
        await using var source = download.Value!;
        using var destination = new MemoryStream();
        var buffer = new byte[81_920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (destination.Length > Math.Min(limit, int.MaxValue) - read)
                return Result<byte[]>.Failure(StorageErrors.TooLarge($"The download exceeds the {limit} byte buffering limit."));
            destination.Write(buffer, 0, read);
        }
        return Result<byte[]>.Success(destination.ToArray());
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
            if (info.IsFailure) return options.IgnoreMissing && info.Error?.Code == StorageErrors.NotFoundCode
                ? Result.Success()
                : Result.Failure(info.Error!);
            if (info.Value!.ItemType == StorageItemType.Directory)
            {
                if (options.Condition is { IsEmpty: false })
                    return Result.Failure(StorageErrors.Unsupported(
                        "Swift virtual directories do not have one atomic identity condition."));
                var listed = await ListAllNamesAsync(DirectoryPrefix(normalized.Value!), cancellationToken).ConfigureAwait(false);
                if (listed.IsFailure) return Result.Failure(listed.Error!);
                var names = listed.Value!;
                if (!options.Recursive && names.Any(name => name != DirectoryPrefix(normalized.Value!)))
                    return Result.Failure(StorageErrors.Conflict("The Swift directory is not empty."));
                foreach (var name in names)
                {
                    var deleted = await DeleteObjectAsync(name, cancellationToken).ConfigureAwait(false);
                    if (deleted.IsFailure) return deleted;
                }
                var markerDelete = await DeleteObjectAsync(DirectoryPrefix(normalized.Value!), cancellationToken, ignoreMissing: true).ConfigureAwait(false);
                if (markerDelete.IsFailure) return markerDelete;
            }
            else
            {
                // Swift ignores If-Match on DELETE: the condition is checked just before.
                var check = CheckCondition(info.Value, options.Condition, "delete");
                if (check.IsFailure) return check;
                return await DeleteObjectAsync(ToKey(normalized.Value!), cancellationToken).ConfigureAwait(false);
            }
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Delete Swift object")); }
    }

    /// <inheritdoc />
    /// <remarks>
    /// An object is copied on the server with <c>COPY</c>, pinned to the version it read (<c>If-Match</c>, which
    /// Swift applies to the source, and <c>?version-id=</c> for <see cref="StorageTransferOptions.SourceVersionId"/>).
    /// A create-only copy is checked just before: Swift has no destination condition on <c>COPY</c>, so a
    /// <see cref="StorageTransferOptions.DestinationCondition"/> is unsupported.
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
        try
        {
            var info = await SourceInfoAsync(source.Value!, options.SourceVersionId, cancellationToken).ConfigureAwait(false);
            if (info.IsFailure) return Result.Failure(info.Error!);
            if (info.Value!.ItemType == StorageItemType.Directory)
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
            return await CopyFileAsync(source.Value!, destination.Value!, info.Value, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Copy Swift object")); }
    }

    /// <inheritdoc />
    /// <remarks>
    /// An object is copied as <see cref="CopyAsync"/> does, then deleted only while it is still the version that was
    /// copied, compared just before the delete (Swift ignores <c>If-Match</c> on <c>DELETE</c>). Once the copy
    /// committed, a failure to delete the source returns <c>storage.partial_failure</c> with
    /// <c>destinationState=complete</c> and <c>leftBehind</c> naming the source, and cancellation no longer applies.
    /// A directory is copied through the relay and each copied object deleted under the identity it was listed with.
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
        StorageItem copied;
        try
        {
            var info = await SourceInfoAsync(source.Value!, options.SourceVersionId, cancellationToken).ConfigureAwait(false);
            if (info.IsFailure) return Result.Failure(info.Error!);
            if (info.Value!.ItemType == StorageItemType.Directory)
            {
                relationship = StorageTransferPath.ValidateDirectoryDestination(source.Value!, destination.Value!);
                if (relationship.IsFailure) return relationship;
                return await ObjectStoreMoves.MoveDirectoryAsync(this, source.Value!, destination.Value!, options, cancellationToken).ConfigureAwait(false);
            }
            var result = await CopyFileAsync(source.Value!, destination.Value!, info.Value, options, cancellationToken).ConfigureAwait(false);
            if (result.IsFailure) return result;
            copied = info.Value;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Move Swift object")); }

        // Committed: the source goes only while it is the version that was copied, and a cancel no longer applies.
        try
        {
            var current = await GetInfoAsync(source.Value!, CancellationToken.None).ConfigureAwait(false);
            if (current.IsFailure)
                return current.Error!.Code == StorageErrors.NotFoundCode
                    ? Result.Success()
                    : Result.Failure(ObjectStoreMoves.SourceKept("Swift", source.Value!, current.Error!));
            if (!StagedWriter.SameETag(copied.ETag, current.Value!.ETag) ||
                (options.SourceVersionId is not null && !string.Equals(copied.VersionId, current.Value.VersionId, StringComparison.Ordinal)))
                return Result.Failure(ObjectStoreMoves.SourceKept("Swift", source.Value!,
                    StorageErrors.Conflict("The Swift source changed after it was copied, so it was not deleted.")));
            var deleted = await DeleteObjectAsync(ToKey(source.Value!), CancellationToken.None, ignoreMissing: true).ConfigureAwait(false);
            return deleted.IsSuccess ? deleted : Result.Failure(ObjectStoreMoves.SourceKept("Swift", source.Value!, deleted.Error!));
        }
        catch (Exception error)
        {
            return Result.Failure(ObjectStoreMoves.SourceKept("Swift", source.Value!, Map(error, "Delete moved Swift object")));
        }
    }

    /// <inheritdoc />
    ValueTask<StorageConditionEnforcement> IStorageConditionEnforcementSource.GetEnforcementAsync(StorageConditionKind kind, bool serverSideCopy, CancellationToken cancellationToken) =>
        ValueTask.FromResult(kind == StorageConditionKind.CreateOnly && !serverSideCopy
            ? StorageConditionEnforcement.Atomic
            : StorageConditionEnforcement.CheckedBeforeCommit);

    /// <summary>A copy or move source: the object (at <paramref name="versionId"/> when set), or a directory.</summary>
    private async Task<Result<StorageItem>> SourceInfoAsync(string path, string? versionId, CancellationToken cancellationToken)
    {
        if (versionId is null)
            return await GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Head, ObjectUri(ToKey(path), versionId)),
            cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? Result<StorageItem>.Success(ToItem(path, response))
            : Result<StorageItem>.Failure(FromStatus(response, "Get Swift object version"));
    }

    /// <summary>Copies one object on the server, pinned to the version <paramref name="source"/> describes.</summary>
    private async Task<Result> CopyFileAsync(string sourcePath, string destinationPath, StorageItem source, StorageTransferOptions options, CancellationToken cancellationToken)
    {
        if (options.DestinationCondition is { IsEmpty: false })
            return Result.Failure(StorageErrors.Unsupported(
                "Swift has no destination condition on COPY, so a server-side copy cannot keep the destination condition."));
        if (options.ExpectedSourceETag is { } expected && !StagedWriter.SameETag(expected, source.ETag))
            return Result.Failure(StorageErrors.Conflict(
                $"The Swift source '{sourcePath}' changed since it was read.",
                $"expectedETag={expected.Trim('"')};actualETag={source.ETag}"));
        var destinationKey = ToKey(destinationPath);
        if (!options.Overwrite)
        {
            // Swift applies If-None-Match on COPY to the source, so the destination can only be checked first.
            using var head = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Head, ObjectUri(destinationKey)),
                cancellationToken).ConfigureAwait(false);
            if (head.IsSuccessStatusCode) return Result.Failure(StorageErrors.Conflict("The Swift destination already exists."));
            if (head.StatusCode != HttpStatusCode.NotFound) return Result.Failure(FromStatus(head, "Check Swift copy destination"));
        }
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(new HttpMethod("COPY"), ObjectUri(ToKey(sourcePath), options.SourceVersionId));
            request.Headers.TryAddWithoutValidation("Destination", "/" + Uri.EscapeDataString(_configuration.Container) + "/" + EncodeObjectPath(destinationKey));
            // Applied to the source: the copy fails with 412 if the source is no longer the version read.
            if (source.ETag is not null) request.Headers.TryAddWithoutValidation("If-Match", QuoteETag(source.ETag));
            return request;
        }, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return Result.Success();
        return Result.Failure(response.StatusCode == HttpStatusCode.PreconditionFailed
            ? StorageErrors.Conflict($"The Swift source '{sourcePath}' changed since it was read.", "httpStatus=412")
            : FromStatus(response, "Copy Swift object"));
    }

    /// <summary>Compares an object with a condition the server would not enforce itself.</summary>
    private static Result CheckCondition(StorageItem current, StorageMutationCondition? condition, string operation)
    {
        if (condition is null or { IsEmpty: true }) return Result.Success();
        if (condition.ExpectedETag is { } etag && !StagedWriter.SameETag(etag, current.ETag))
            return Result.Failure(StorageErrors.Conflict($"The Swift object ETag no longer matches the requested {operation} condition."));
        if (condition.ExpectedVersionId is { } version && !string.Equals(version, current.VersionId, StringComparison.Ordinal))
            return Result.Failure(StorageErrors.Conflict($"The Swift object version no longer matches the requested {operation} condition."));
        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Head, ContainerUri()),
                cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? Result.Success()
                : Result.Failure(FromStatus(response, "Check Swift health"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Check Swift health")); }
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
        if (options.ExpectedVersionId is not null)
            return Result<StorageItem>.Failure(StorageErrors.Unsupported(
                "This Swift endpoint does not expose a portable version condition for metadata updates."));
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        var snapshot = StorageMetadataSnapshot.Create(metadata);
        try
        {
            var info = await GetInfoAsync(normalized.Value!, cancellationToken).ConfigureAwait(false);
            if (info.IsFailure) return Result<StorageItem>.Failure(info.Error!);
            if (info.Value!.ItemType != StorageItemType.File)
                return Result<StorageItem>.Failure(StorageErrors.Conflict(
                    "Swift metadata can only be updated on an object."));
            // The If-Match sent below is not enforced by every Swift deployment: the ETag is compared here as well.
            if (options.ExpectedETag is { } expectedETag && !StagedWriter.SameETag(expectedETag, info.Value.ETag))
                return Result<StorageItem>.Failure(StorageErrors.Conflict(
                    "The Swift object ETag no longer matches the metadata update condition."));

            using var response = await SendAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, ObjectUri(ToKey(normalized.Value!)));
                if (options.Mode == StorageMetadataUpdateMode.Replace)
                    request.Headers.TryAddWithoutValidation("X-Fresh-Metadata", "True");
                if (options.ExpectedETag is not null)
                    request.Headers.TryAddWithoutValidation("If-Match", QuoteETag(options.ExpectedETag));
                foreach (var (name, value) in snapshot)
                    request.Headers.TryAddWithoutValidation("X-Object-Meta-" + name, value);
                return request;
            }, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Result<StorageItem>.Failure(FromStatus(response, "Update Swift metadata"));
            return await GetInfoAsync(normalized.Value!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Update Swift metadata")); }
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
            return Task.FromResult(Result<NativeConnectionLease<TClient>>.Failure(StorageErrors.Unsupported($"Swift does not expose native type '{typeof(TClient).FullName}'.")));
        return Task.FromResult(Result<NativeConnectionLease<TClient>>.Success(new NativeConnectionLease<TClient>(typed, _ => ValueTask.CompletedTask)));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _authenticationGate.Dispose();
            if (_ownsClient) _client.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
    {
        await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
        var request = requestFactory();
        request.Headers.TryAddWithoutValidation("X-Auth-Token", _token);
        var response = await _client.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
        var canRetry = request.Content is null;
        request.Dispose();
        if (response.StatusCode != HttpStatusCode.Unauthorized ||
            _configuration.AuthenticationMode == SwiftAuthenticationMode.StaticToken)
            return response;

        InvalidateAuthentication();
        if (!canRetry)
            return response;
        response.Dispose();
        await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
        request = requestFactory();
        request.Headers.TryAddWithoutValidation("X-Auth-Token", _token);
        response = await _client.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
        request.Dispose();
        return response;
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_token) && !string.IsNullOrWhiteSpace(_storageUrl) &&
            DateTimeOffset.UtcNow < _tokenExpiresAt - TimeSpan.FromMinutes(1))
            return;
        await _authenticationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(_token) && !string.IsNullOrWhiteSpace(_storageUrl) &&
                DateTimeOffset.UtcNow < _tokenExpiresAt - TimeSpan.FromMinutes(1))
                return;
            if (_configuration.AuthenticationMode == SwiftAuthenticationMode.TempAuthV1)
                await AuthenticateTempAuthAsync(cancellationToken).ConfigureAwait(false);
            else
                await AuthenticateKeystoneAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _authenticationGate.Release(); }
    }

    /// <summary>Authenticates with TempAuth v1: credentials in headers, token and storage URL in the reply.</summary>
    private async Task AuthenticateTempAuthAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _configuration.AuthenticationUrl);
        request.Headers.TryAddWithoutValidation("X-Auth-User", _configuration.Username);
        request.Headers.TryAddWithoutValidation("X-Auth-Key", _configuration.Password);
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"TempAuth authentication failed with HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        if (!response.Headers.TryGetValues("X-Auth-Token", out var tokens))
            throw new InvalidOperationException("TempAuth did not return X-Auth-Token.");
        var storageUrl = _configuration.StorageUrl;
        if (string.IsNullOrWhiteSpace(storageUrl) && response.Headers.TryGetValues("X-Storage-Url", out var urls))
            storageUrl = CheckedStorageUrl(urls.First(), "TempAuth");
        var expires = response.Headers.TryGetValues("X-Auth-Token-Expires", out var lifetimes) &&
            long.TryParse(lifetimes.First(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                ? DateTimeOffset.UtcNow.AddSeconds(seconds)
                : DateTimeOffset.UtcNow.AddHours(1);
        _token = tokens.First();
        _storageUrl = storageUrl?.TrimEnd('/') ??
            throw new InvalidOperationException("TempAuth did not return X-Storage-Url.");
        _tokenExpiresAt = expires;
    }

    private async Task AuthenticateKeystoneAsync(CancellationToken cancellationToken)
    {
        var endpoint = KeystoneTokenUri(_configuration.AuthenticationUrl!);
        var payload = new
        {
            auth = new
            {
                identity = new
                {
                    methods = new[] { "password" },
                    password = new
                    {
                        user = new
                        {
                            name = _configuration.Username,
                            domain = new { name = _configuration.UserDomainName },
                            password = _configuration.Password
                        }
                    }
                },
                scope = new
                {
                    project = new
                    {
                        name = _configuration.ProjectName,
                        domain = new { name = _configuration.ProjectDomainName }
                    }
                }
            }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Keystone authentication failed with HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        if (!response.Headers.TryGetValues("X-Subject-Token", out var tokens))
            throw new InvalidOperationException("Keystone did not return X-Subject-Token.");
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var tokenNode = document.RootElement.GetProperty("token");
        var expires = tokenNode.TryGetProperty("expires_at", out var expiresNode) &&
            DateTimeOffset.TryParse(expiresNode.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow.AddHours(1);
        var storageUrl = _configuration.StorageUrl;
        if (string.IsNullOrWhiteSpace(storageUrl) && FindObjectStoreEndpoint(tokenNode) is { } catalogUrl)
            storageUrl = CheckedStorageUrl(catalogUrl, "Keystone");
        _token = tokens.First();
        _storageUrl = storageUrl?.TrimEnd('/') ??
            throw new InvalidOperationException("Keystone catalog did not contain a matching object-store endpoint.");
        _tokenExpiresAt = expires;
    }

    /// <summary>
    /// The storage URL an authentication reply returned, after the same scheme check the configured URLs get:
    /// the token is sent to every request on it, so an <c>http://</c> endpoint from the server is refused unless
    /// <see cref="SwiftConnectionConfig.AllowInsecureHttp"/> is on (the failure maps to <c>storage.provider_error</c>).
    /// </summary>
    private string CheckedStorageUrl(string url, string source) =>
        _configuration.IsAllowedUrl(url)
            ? url
            : throw new InvalidOperationException(
                $"{source} returned a storage URL whose scheme is not allowed for this connection (set AllowInsecureHttp to accept http).");

    private string? FindObjectStoreEndpoint(JsonElement tokenNode)
    {
        if (!tokenNode.TryGetProperty("catalog", out var catalog)) return null;
        foreach (var service in catalog.EnumerateArray())
        {
            if (!service.TryGetProperty("type", out var type) || type.GetString() != "object-store") continue;
            foreach (var endpoint in service.GetProperty("endpoints").EnumerateArray())
            {
                var endpointInterface = endpoint.TryGetProperty("interface", out var interfaceNode) ? interfaceNode.GetString() : null;
                var region = endpoint.TryGetProperty("region", out var regionNode) ? regionNode.GetString() : null;
                if (!string.Equals(endpointInterface, _configuration.EndpointInterface, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(_configuration.Region) &&
                    !string.Equals(region, _configuration.Region, StringComparison.OrdinalIgnoreCase)) continue;
                if (endpoint.TryGetProperty("url", out var url)) return url.GetString();
            }
        }
        return null;
    }

    private void InvalidateAuthentication()
    {
        _token = null;
        if (_configuration.AuthenticationMode != SwiftAuthenticationMode.StaticToken) _storageUrl = null;
        _tokenExpiresAt = default;
    }

    private async Task<Result<StorageItem>> GetDirectoryInfoAsync(string path, CancellationToken cancellationToken)
    {
        var page = await ListProviderPageAsync(DirectoryPrefix(path), null, 1, null, cancellationToken).ConfigureAwait(false);
        return page.IsFailure
            ? Result<StorageItem>.Failure(page.Error!)
            : page.Value!.Items.Count > 0
                ? Result<StorageItem>.Success(DirectoryItem(path))
                : Result<StorageItem>.Failure(StorageErrors.NotFound($"Swift item '{path}' was not found."));
    }

    private async Task<Result<SwiftPage>> ListProviderPageAsync(
        string prefix,
        string? delimiter,
        int limit,
        string? marker,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() =>
        {
            var parameters = new List<string>
            {
                "format=json",
                "prefix=" + Uri.EscapeDataString(prefix),
                "limit=" + limit.ToString(CultureInfo.InvariantCulture)
            };
            if (delimiter is not null) parameters.Add("delimiter=" + Uri.EscapeDataString(delimiter));
            if (!string.IsNullOrWhiteSpace(marker)) parameters.Add("marker=" + Uri.EscapeDataString(marker));
            return new HttpRequestMessage(HttpMethod.Get, ContainerUri() + "?" + string.Join("&", parameters));
        }, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return Result<SwiftPage>.Failure(FromStatus(response, "List Swift objects"));
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var items = new List<SwiftListItem>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.TryGetProperty("subdir", out var subdirectory))
            {
                items.Add(new SwiftListItem(null, subdirectory.GetString(), null, null, null, null));
                continue;
            }
            var name = element.GetProperty("name").GetString();
            long? bytes = element.TryGetProperty("bytes", out var bytesNode) ? bytesNode.GetInt64() : null;
            DateTimeOffset? lastModified = element.TryGetProperty("last_modified", out var modifiedNode) &&
                DateTimeOffset.TryParse(modifiedNode.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var modified)
                    ? modified
                    : null;
            items.Add(new SwiftListItem(
                name,
                null,
                bytes,
                lastModified,
                element.TryGetProperty("content_type", out var contentType) ? contentType.GetString() : null,
                element.TryGetProperty("hash", out var hash) ? hash.GetString() : null));
        }
        var last = items.LastOrDefault();
        var next = items.Count == limit ? last?.Subdirectory ?? last?.Name : null;
        return Result<SwiftPage>.Success(new SwiftPage(items, next));
    }

    private async Task<Result<IReadOnlyList<string>>> ListAllNamesAsync(string prefix, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        string? marker = null;
        do
        {
            var page = await ListProviderPageAsync(prefix, null, 1000, marker, cancellationToken).ConfigureAwait(false);
            if (page.IsFailure) return Result<IReadOnlyList<string>>.Failure(page.Error!);
            names.AddRange(page.Value!.Items.Where(item => item.Name is not null).Select(item => item.Name!));
            marker = page.Value.NextMarker;
        } while (!string.IsNullOrWhiteSpace(marker));
        return Result<IReadOnlyList<string>>.Success(names.AsReadOnly());
    }

    private async Task<Result> DeleteObjectAsync(
        string key,
        CancellationToken cancellationToken,
        bool ignoreMissing = false)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, ObjectUri(key)),
            cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode || ignoreMissing && response.StatusCode == HttpStatusCode.NotFound)
            return Result.Success();
        return Result.Failure(FromStatus(response, "Delete Swift object"));
    }

    private Uri ContainerUri() => new($"{_storageUrl}/{Uri.EscapeDataString(_configuration.Container)}", UriKind.Absolute);
    private Uri ObjectUri(string key, string? versionId = null) =>
        new($"{ContainerUri()}/{EncodeObjectPath(key)}{(versionId is null ? string.Empty : "?version-id=" + Uri.EscapeDataString(versionId))}", UriKind.Absolute);
    private static string EncodeObjectPath(string key) => string.Join("/", key.Split('/').Select(Uri.EscapeDataString));
    private static Uri KeystoneTokenUri(string authenticationUrl)
    {
        var value = authenticationUrl.TrimEnd('/');
        if (value.EndsWith("/auth/tokens", StringComparison.OrdinalIgnoreCase)) return new Uri(value);
        if (!value.EndsWith("/v3", StringComparison.OrdinalIgnoreCase)) value += "/v3";
        return new Uri(value + "/auth/tokens");
    }

    private string ToKey(string path) => _prefix + path;
    private string DirectoryPrefix(string path) => path.Length == 0 ? _prefix : ToKey(path).TrimEnd('/') + "/";
    /// <summary>The storage path of an object name under the mounted prefix, or null for a name the library cannot address (see <see cref="ObjectKeyPaths"/>).</summary>
    private string? FromKey(string key) => ObjectKeyPaths.FromKey(_prefix, key);
    private Result<string> Normalize(string path) => StoragePath.Normalize(path);
    private Result<string> NormalizeRequired(string path)
    {
        var normalized = Normalize(path);
        return normalized.IsFailure || normalized.Value!.Length > 0
            ? normalized
            : Result<string>.Failure(StorageErrors.InvalidPath("A non-root storage path is required."));
    }

    private static StorageItem ToItem(string path, HttpResponseMessage response)
    {
        response.Content.Headers.TryGetValues("Content-Type", out var contentTypes);
        response.Headers.TryGetValues("X-Object-Version-Id", out var versionIds);
        var metadata = response.Headers
            .Where(header => header.Key.StartsWith("X-Object-Meta-", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                header => header.Key["X-Object-Meta-".Length..],
                header => string.Join(",", header.Value),
                StringComparer.OrdinalIgnoreCase);
        return new StorageItem
        {
            Path = path,
            Name = NameOf(path),
            ItemType = path.EndsWith('/') ? StorageItemType.Directory : StorageItemType.File,
            Size = path.EndsWith('/') ? null : response.Content.Headers.ContentLength,
            LastModified = response.Content.Headers.LastModified,
            ContentType = contentTypes?.FirstOrDefault(),
            ETag = RawETag(response),
            VersionId = versionIds?.FirstOrDefault(),
            Metadata = metadata
        };
    }

    /// <summary>
    /// Swift sends its ETag unquoted, which the typed <c>ETag</c> header parser rejects as invalid,
    /// so the header is read raw. Quotes are stripped when a proxy or middleware adds them.
    /// </summary>
    internal static string? RawETag(HttpResponseMessage response) =>
        response.Headers.TryGetValues("ETag", out var values) && values.FirstOrDefault() is { Length: > 0 } raw
            ? raw.Trim().Trim('"')
            : null;

    private static string QuoteETag(string etag) =>
        etag.StartsWith('"') && etag.EndsWith('"') ? etag : $"\"{etag}\"";

    private static StorageItem DirectoryItem(string path) => new()
    {
        Path = path,
        Name = path.Length == 0 ? string.Empty : NameOf(path),
        ItemType = StorageItemType.Directory
    };

    private static string NameOf(string path) => path.Split('/')[^1];

    private static Error FromStatus(HttpResponseMessage response, string operation) =>
        ProviderErrorMapper.FromHttpStatus(
            (int)response.StatusCode,
            operation,
            "Swift",
            ProviderErrorMapper.RetryAfter(response.Headers.RetryAfter));

    private static Error Map(Exception exception, string operation)
    {
        if (exception is HttpRequestException { StatusCode: { } status })
            return ProviderErrorMapper.FromHttpStatus((int)status, operation, "Swift");
        return ProviderErrorMapper.FromTransport(exception, operation, "Swift")
            ?? StorageErrors.ProviderError($"{operation}: Swift provider failed.", ProviderErrorMapper.ExceptionDetails(exception));
    }

    private sealed record SwiftPage(IReadOnlyList<SwiftListItem> Items, string? NextMarker);
    private sealed record SwiftListItem(
        string? Name,
        string? Subdirectory,
        long? Bytes,
        DateTimeOffset? LastModified,
        string? ContentType,
        string? Hash);

    private sealed class NonDisposingReadStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { }
    }
}
