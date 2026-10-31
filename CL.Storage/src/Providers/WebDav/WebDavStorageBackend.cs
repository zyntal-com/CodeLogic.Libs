using System.Diagnostics.CodeAnalysis;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CL.Storage.Providers.Local;
using CodeLogic.Core.Results;
using WebDAVClient;
using WebDAVClient.Helpers;
using WebDAVClient.Model;

namespace CL.Storage.Providers.WebDav;

/// <summary>Root-scoped storage over a WebDAV endpoint.</summary>
public sealed class WebDavStorageBackend : IStorageBackend, IStorageMetadataService, IStorageDiagnosticsSource
{
    private static readonly StorageCapabilities WebDavCapabilities = new(
        StorageFeature.PhysicalDirectories |
        StorageFeature.FileCopy |
        StorageFeature.DirectoryCopy |
        StorageFeature.FileMove |
        StorageFeature.DirectoryMove |
        StorageFeature.ServerSideCopy |
        StorageFeature.ServerSideMove |
        // Not AtomicMove: RFC 4918 lets a server move a collection member by member and answer 207 Multi-Status
        // when some members failed, leaving the tree split between source and destination.
        StorageFeature.ConditionalCreate |
        StorageFeature.AtomicReplace |
        StorageFeature.MetadataRead);

    private readonly IClient _client;
    private readonly RemotePathResolver _paths;
    private readonly string _basePath;
    private readonly bool _ownsClient;
    private readonly long _maxBufferedDownloadBytes;
    private readonly ProviderRetryPolicy _retry;
    private readonly HttpClient? _http;
    private readonly Uri? _endpoint;
    private int _disposed;

    /// <summary>Initializes a backend over a WebDAV client.</summary>
    /// <param name="connectionId">Unique connection ID exposed by the storage registry.</param>
    /// <param name="client">Configured WebDAV client used for operations.</param>
    /// <param name="root">Optional provider-neutral root label.</param>
    /// <param name="basePath">Optional remote base path prepended to requests.</param>
    /// <param name="ownsClient">Whether disposal of this backend also disposes the client.</param>
    /// <param name="maxBufferedDownloadBytes">Maximum size accepted by buffered download helpers.</param>
    /// <param name="retry">Transient-failure retry policy; defaults when omitted.</param>
    public WebDavStorageBackend(
        string connectionId,
        IClient client,
        string? root = null,
        string? basePath = null,
        bool ownsClient = false,
        long maxBufferedDownloadBytes = 67_108_864,
        StorageRetryConfig? retry = null)
        : this(connectionId, client, root, basePath, ownsClient, maxBufferedDownloadBytes, retry, observer: null)
    {
    }

    internal WebDavStorageBackend(
        string connectionId,
        IClient client,
        string? root,
        string? basePath,
        bool ownsClient,
        long maxBufferedDownloadBytes,
        StorageRetryConfig? retry,
        IStorageConnectionObserver? observer,
        HttpClient? http = null,
        Uri? endpoint = null)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) throw new ArgumentException("Connection ID is required.", nameof(connectionId));
        ArgumentNullException.ThrowIfNull(client);
        if (maxBufferedDownloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBufferedDownloadBytes));
        ConnectionId = connectionId;
        _retry = new ProviderRetryPolicy(retry, connectionId, StorageProvider.WebDav, observer);
        _retry.Enrich = (error, attempt) => TlsDiagnosis.Enrich(error, Identity, attempt);
        _http = http;
        _endpoint = endpoint;
        _client = client;
        _paths = new RemotePathResolver(root);
        _basePath = NormalizeBasePath(basePath);
        _ownsClient = ownsClient;
        _maxBufferedDownloadBytes = maxBufferedDownloadBytes;
    }

    /// <inheritdoc />
    public string ConnectionId { get; }

    /// <summary>Identifies the listing snapshots continuation tokens refer to; settings-based so tokens outlive a registration.</summary>
    internal string? ListingScope { get; init; }

    /// <summary>A resource the HTTP stack uses (a client certificate), disposed with the backend.</summary>
    internal IDisposable? Owned { get; init; }
    /// <inheritdoc />
    public StorageProvider Provider => StorageProvider.WebDav;
    /// <inheritdoc />
    public string Root => _paths.Root;
    /// <inheritdoc />
    public StorageCapabilities Capabilities => WebDavCapabilities;

    /// <inheritdoc />
    public Task<Result<StorageItem>> GetInfoAsync(string path, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Get WebDAV item info", RetryKind.Idempotent, (_, token) => GetInfoCoreAsync(path, token), cancellationToken);

    private async Task<Result<StorageItem>> GetInfoCoreAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result<StorageItem>.Failure(resolved.Error!);
        if (resolved.Value!.StoragePath.Length == 0)
            return Result<StorageItem>.Success(DirectoryItem(string.Empty));
        try
        {
            var item = await FindItemAsync(resolved.Value, cancellationToken).ConfigureAwait(false);
            return item is null
                ? Result<StorageItem>.Failure(StorageErrors.NotFound($"WebDAV item '{resolved.Value.StoragePath}' was not found."))
                : Result<StorageItem>.Success(ToItem(resolved.Value.StoragePath, item));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Get WebDAV item info")); }
    }

    /// <inheritdoc />
    public Task<Result<bool>> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Check WebDAV item existence", RetryKind.Idempotent, (_, token) => ExistsCoreAsync(path, token), cancellationToken);

    private async Task<Result<bool>> ExistsCoreAsync(string path, CancellationToken cancellationToken)
    {
        var info = await GetInfoCoreAsync(path, cancellationToken).ConfigureAwait(false);
        if (info.IsSuccess) return Result<bool>.Success(true);
        return info.Error?.Code == StorageErrors.NotFoundCode
            ? Result<bool>.Success(false)
            : Result<bool>.Failure(info.Error!);
    }

    /// <inheritdoc />
    public Task<Result<StoragePage>> ListAsync(string path, StorageListOptions? options = null, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("List WebDAV directory", RetryKind.Idempotent, (_, token) => ListCoreAsync(path, options, token), cancellationToken);

    private async Task<Result<StoragePage>> ListCoreAsync(string path, StorageListOptions? options, CancellationToken cancellationToken)
    {
        options ??= new StorageListOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StoragePage>.Failure(validation.Error!);
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result<StoragePage>.Failure(resolved.Error!);
        try
        {
            // The subtree walk runs once per listing pass; later pages resume from the cached
            // snapshot rather than re-issuing a PROPFIND per directory.
            var target = resolved.Value!;
            return await ProviderPaging.CreateAsync(
                ProviderPaging.Scope(ListingScope ?? ConnectionId, target.StoragePath, options.Recursive),
                options,
                async token => Result<IEnumerable<StorageItem>>.Success(
                    await CollectListingAsync(target, options.Recursive, token).ConfigureAwait(false)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (WebDAVException error) when (IsNotFound(error))
        {
            return Result<StoragePage>.Failure(StorageErrors.NotFound($"WebDAV directory '{resolved.Value!.StoragePath}' was not found."));
        }
        catch (Exception error) { return Result<StoragePage>.Failure(Map(error, "List WebDAV directory")); }
    }

    /// <inheritdoc />
    public Task<Result> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Create WebDAV directory", RetryKind.Idempotent, (_, token) => CreateDirectoryCoreAsync(path, token), cancellationToken);

    private async Task<Result> CreateDirectoryCoreAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result.Failure(resolved.Error!);
        try
        {
            await EnsureDirectoryAsync(resolved.Value!.RemotePath, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Create WebDAV directory")); }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Uploads are staged and renamed into place, so replaying a seekable source cannot leave a partial file. The
    /// destination is read just before the rename: an existing collection is refused, and <c>Overwrite: T</c> is sent
    /// only when a file was found there (otherwise <c>Overwrite: F</c>, so one created meanwhile is not replaced).
    /// WebDAV has no conditional MOVE this adapter can send, so a file replaced by a collection in the short window
    /// between that read and the MOVE is not detected; a compliant server would then delete the collection.
    /// </remarks>
    public Task<Result<StorageItem>> UploadAsync(string path, Stream source, StorageUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (StorageTransferPipeline.Applies(this, options))
            return StorageTransferPipeline.UploadAsync(this, path, source, options, cancellationToken);
        return _retry.ExecuteUploadAsync("Upload WebDAV file", source, token => UploadCoreAsync(path, source, options, token), cancellationToken);
    }

    private async Task<Result<StorageItem>> UploadCoreAsync(string path, Stream source, StorageUploadOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new StorageUploadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StorageItem>.Failure(validation.Error!);
        if (options.Condition is { IsEmpty: false })
            return Result<StorageItem>.Failure(StorageErrors.Unsupported(
                "This WebDAV adapter does not expose atomic ETag upload conditions."));
        if (options.Metadata.Count > 0)
            return Result<StorageItem>.Failure(StorageErrors.Unsupported(
                "This WebDAV adapter does not support portable user metadata updates."));
        var resolved = _paths.Resolve(path, requireNonRoot: true);
        if (resolved.IsFailure) return Result<StorageItem>.Failure(resolved.Error!);
        string? stagingRemotePath = null;
        try
        {
            var parent = RemotePathResolver.Parent(resolved.Value!.RemotePath);
            if (options.CreateParents)
                await EnsureDirectoryAsync(parent, cancellationToken).ConfigureAwait(false);
            else
            {
                var parentStorage = _paths.FromRemotePath(parent) ?? string.Empty;
                var parentInfo = await GetInfoCoreAsync(parentStorage, cancellationToken).ConfigureAwait(false);
                if (parentInfo.IsFailure) return Result<StorageItem>.Failure(parentInfo.Error!);
            }

            var stagingName = $".clstorage-upload-{Guid.NewGuid():N}";
            stagingRemotePath = EnsureTrailingSlash(parent) + stagingName;
            var success = await _client.Upload(
                EnsureTrailingSlash(parent),
                source,
                stagingName,
                lockToken: null,
                cancellationToken).ConfigureAwait(false);
            if (!success) return Result<StorageItem>.Failure(StorageErrors.ProviderError("The WebDAV server did not accept the upload."));

            // The destination is read just before the commit: a collection is never replaced (MOVE with Overwrite: T
            // deletes it and everything in it first, RFC 4918), and Overwrite: T is sent only when a file was there.
            var existing = await GetInfoCoreAsync(resolved.Value.StoragePath, cancellationToken).ConfigureAwait(false);
            if (existing.IsFailure && existing.Error!.Code != StorageErrors.NotFoundCode)
                return Result<StorageItem>.Failure(existing.Error!);
            if (existing.IsSuccess && existing.Value!.ItemType == StorageItemType.Directory)
                return Result<StorageItem>.Failure(StorageErrors.Conflict("The WebDAV destination is an existing collection, which is not replaced."));
            if (existing.IsSuccess && !options.Overwrite)
                return Result<StorageItem>.Failure(StorageErrors.Conflict("The WebDAV destination already exists."));

            var committed = await TransferAsync("MOVE", stagingRemotePath, resolved.Value.RemotePath, overwrite: existing.IsSuccess, folder: false, cancellationToken).ConfigureAwait(false);
            if (committed != DavTransfer.Done)
            {
                return Result<StorageItem>.Failure(committed == DavTransfer.DestinationExists
                    ? StorageErrors.Conflict("The WebDAV destination already exists.")
                    : StorageErrors.ProviderError("The WebDAV server did not commit the staged upload."));
            }

            stagingRemotePath = null;
            return await GetInfoCoreAsync(resolved.Value.StoragePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Upload WebDAV file")); }
        finally
        {
            if (stagingRemotePath is not null)
            {
                try { await _client.DeleteFile(stagingRemotePath, lockToken: null, CancellationToken.None).ConfigureAwait(false); }
                catch { }
            }
        }
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

    private Task<Result<Stream>> DownloadUnmeteredAsync(string path, StorageDownloadOptions? options, CancellationToken cancellationToken) =>
        _retry.ExecuteAsync("Download WebDAV file", RetryKind.Idempotent, (_, token) => DownloadCoreAsync(path, options, token), cancellationToken);

    private async Task<Result<Stream>> DownloadCoreAsync(string path, StorageDownloadOptions? options, CancellationToken cancellationToken)
    {
        options ??= new StorageDownloadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<Stream>.Failure(validation.Error!);
        if (options.VersionId is not null)
            return Result<Stream>.Failure(StorageErrors.Unsupported(
                "This WebDAV adapter does not support version-specific downloads."));
        var resolved = _paths.Resolve(path, requireNonRoot: true);
        if (resolved.IsFailure) return Result<Stream>.Failure(resolved.Error!);
        try
        {
            var info = await GetInfoCoreAsync(resolved.Value!.StoragePath, cancellationToken).ConfigureAwait(false);
            if (info.IsFailure) return Result<Stream>.Failure(info.Error!);
            if (info.Value!.ItemType == StorageItemType.Directory)
                return Result<Stream>.Failure(StorageErrors.Conflict("A WebDAV directory cannot be downloaded as a file."));
            if (info.Value.Size.HasValue && options.Offset > info.Value.Size.Value)
                return Result<Stream>.Failure(StorageErrors.InvalidPath("The range offset exceeds the WebDAV file length."));
            Stream stream;
            if (options.Offset > 0 || options.Length.HasValue)
            {
                var available = info.Value.Size.HasValue ? info.Value.Size.Value - options.Offset : long.MaxValue;
                var length = options.Length.HasValue ? Math.Min(options.Length.Value, available) : available;
                if (length == long.MaxValue)
                {
                    stream = await _client.Download(resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
                    await SkipAsync(stream, options.Offset, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    if (length == 0)
                        return Result<Stream>.Success(new MemoryStream([], writable: false));
                    stream = await _client.DownloadPartial(
                        resolved.Value.RemotePath,
                        options.Offset,
                        options.Offset + length - 1,
                        cancellationToken).ConfigureAwait(false);
                }
                if (options.Length.HasValue) stream = new RangeReadStream(stream, length);
            }
            else
            {
                stream = await _client.Download(resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            }
            return Result<Stream>.Success(stream);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<Stream>.Failure(Map(error, "Download WebDAV file")); }
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
    /// <remarks>
    /// A non-recursive delete of a collection never sends a bare <c>DELETE</c> (which always removes everything in it):
    /// the collection is locked, listed, and deleted under the lock only when empty. A server without WebDAV locks
    /// (class 2) answers <c>storage.unsupported</c> and the folder is left in place.
    /// </remarks>
    public Task<Result> DeleteAsync(string path, StorageDeleteOptions? options = null, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Delete WebDAV item", RetryKind.NonIdempotent, (_, token) => DeleteCoreAsync(path, options, token), cancellationToken);

    private async Task<Result> DeleteCoreAsync(string path, StorageDeleteOptions? options, CancellationToken cancellationToken)
    {
        options ??= new StorageDeleteOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return validation;
        if (options.Condition is { IsEmpty: false })
            return Result.Failure(StorageErrors.Unsupported(
                "This WebDAV adapter does not expose atomic ETag delete conditions."));
        var resolved = _paths.Resolve(path, requireNonRoot: true);
        if (resolved.IsFailure) return Result.Failure(resolved.Error!);
        try
        {
            var info = await GetInfoCoreAsync(resolved.Value!.StoragePath, cancellationToken).ConfigureAwait(false);
            if (info.IsFailure) return options.IgnoreMissing && info.Error?.Code == StorageErrors.NotFoundCode
                ? Result.Success()
                : Result.Failure(info.Error!);
            if (info.Value!.ItemType == StorageItemType.Directory)
            {
                // DELETE on a collection is always depth infinity: without a lock, a file arriving between an
                // emptiness check and the DELETE would be destroyed.
                if (!options.Recursive)
                    return await DeleteEmptyCollectionAsync(resolved.Value, options.IgnoreMissing, cancellationToken).ConfigureAwait(false);
                await _client.DeleteFolder(resolved.Value.RemotePath, lockToken: null, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _client.DeleteFile(resolved.Value.RemotePath, lockToken: null, cancellationToken).ConfigureAwait(false);
            }
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Delete WebDAV item")); }
    }

    /// <inheritdoc />
    public async Task<Result> CopyAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageTransferOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return validation;
        if (Unpinnable(options) is { } unsupported) return unsupported;
        var source = _paths.Resolve(sourcePath, requireNonRoot: true);
        if (source.IsFailure) return Result.Failure(source.Error!);
        var destination = _paths.Resolve(destinationPath, requireNonRoot: true);
        if (destination.IsFailure) return Result.Failure(destination.Error!);
        var relationship = StorageTransferPath.ValidateDistinct(
            source.Value!.StoragePath,
            destination.Value!.StoragePath);
        if (relationship.IsFailure) return relationship;
        try
        {
            var info = await GetInfoCoreAsync(source.Value!.StoragePath, cancellationToken).ConfigureAwait(false);
            if (info.IsFailure) return Result.Failure(info.Error!);
            if (info.Value!.ItemType == StorageItemType.Directory)
            {
                relationship = StorageTransferPath.ValidateDirectoryDestination(
                    source.Value.StoragePath,
                    destination.Value.StoragePath);
                if (relationship.IsFailure) return relationship;
            }
            return await ServerTransferAsync("COPY", source.Value, destination.Value!, info.Value!.ItemType == StorageItemType.Directory, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Copy WebDAV item")); }
    }

    /// <inheritdoc />
    public Task<Result> MoveAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Move WebDAV item", RetryKind.NonIdempotent, (_, token) => MoveCoreAsync(sourcePath, destinationPath, options, token), cancellationToken);

    private async Task<Result> MoveCoreAsync(string sourcePath, string destinationPath, StorageTransferOptions? options, CancellationToken cancellationToken)
    {
        options ??= new StorageTransferOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return validation;
        if (Unpinnable(options) is { } unsupported) return unsupported;
        var source = _paths.Resolve(sourcePath, requireNonRoot: true);
        if (source.IsFailure) return Result.Failure(source.Error!);
        var destination = _paths.Resolve(destinationPath, requireNonRoot: true);
        if (destination.IsFailure) return Result.Failure(destination.Error!);
        var relationship = StorageTransferPath.ValidateDistinct(
            source.Value!.StoragePath,
            destination.Value!.StoragePath);
        if (relationship.IsFailure) return relationship;
        try
        {
            var info = await GetInfoCoreAsync(source.Value!.StoragePath, cancellationToken).ConfigureAwait(false);
            if (info.IsFailure) return Result.Failure(info.Error!);
            if (info.Value!.ItemType == StorageItemType.Directory)
            {
                relationship = StorageTransferPath.ValidateDirectoryDestination(
                    source.Value.StoragePath,
                    destination.Value.StoragePath);
                if (relationship.IsFailure) return relationship;
            }
            return await ServerTransferAsync("MOVE", source.Value, destination.Value!, info.Value!.ItemType == StorageItemType.Directory, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Move WebDAV item")); }
    }

    /// <summary>This adapter sends no WebDAV <c>If</c> headers, so a copy or move cannot be pinned or conditioned.</summary>
    private static Result? Unpinnable(StorageTransferOptions options) =>
        options.ExpectedSourceETag is not null || options.SourceVersionId is not null || options.DestinationCondition is { IsEmpty: false }
            ? Result.Failure(StorageErrors.Unsupported("This WebDAV adapter cannot pin a copy or move to a source version or keep a destination condition."))
            : (Result?)null;

    /// <summary>
    /// A server-side COPY or MOVE. An existing directory at the destination is never replaced: with
    /// <c>Overwrite: T</c> the server would delete it and everything in it first (RFC 4918), so it is refused, as a
    /// local move does. An absent destination is claimed with <c>Overwrite: F</c>, so one created meanwhile is not
    /// replaced either. A <c>207 Multi-Status</c> answer means some members failed: the tree may be split, and the
    /// result says so. The destination is read immediately before the request, and WebDAV offers this adapter no
    /// condition on it: a file that is replaced by a collection in that short window would be replaced with
    /// <c>Overwrite: T</c>, which a compliant server does by deleting the collection first.
    /// </summary>
    private async Task<Result> ServerTransferAsync(
        string method,
        ResolvedRemotePath source,
        ResolvedRemotePath destination,
        bool folder,
        StorageTransferOptions options,
        CancellationToken cancellationToken)
    {
        var existing = await GetInfoCoreAsync(destination.StoragePath, cancellationToken).ConfigureAwait(false);
        if (existing.IsFailure && existing.Error!.Code != StorageErrors.NotFoundCode)
            return Result.Failure(existing.Error!);
        if (existing.IsSuccess)
        {
            if (existing.Value!.ItemType == StorageItemType.Directory)
                return Result.Failure(StorageErrors.Conflict("The WebDAV destination is an existing collection, which is not replaced."));
            if (!options.Overwrite)
                return Result.Failure(StorageErrors.Conflict("The WebDAV destination already exists."));
        }
        if (options.CreateParents) await EnsureDirectoryAsync(RemotePathResolver.Parent(destination.RemotePath), cancellationToken).ConfigureAwait(false);
        var outcome = await TransferAsync(
            method,
            source.RemotePath,
            destination.RemotePath,
            overwrite: existing.IsSuccess,
            folder,
            cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            DavTransfer.Done => Result.Success(),
            DavTransfer.DestinationExists => Result.Failure(StorageErrors.Conflict("The WebDAV destination already exists.")),
            _ => Result.Failure(StorageErrors.PartialFailure(
                $"The WebDAV {method} of '{source.StoragePath}' failed for some members (207 Multi-Status): part of the tree may be at the destination and part at the source.",
                $"{StorageErrorInfo.HttpStatusKey}=207;{StorageErrorInfo.DestinationStateKey}=partial"))
        };
    }

    /// <summary>How a WebDAV COPY or MOVE ended.</summary>
    private enum DavTransfer
    {
        Done,
        DestinationExists,
        /// <summary>207 Multi-Status: some members failed.</summary>
        Partial
    }

    /// <inheritdoc />
    public Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Check WebDAV health", RetryKind.Idempotent, (_, token) => CheckHealthCoreAsync(token), cancellationToken);

    private async Task<Result> CheckHealthCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var options = await _client.GetServerOptions(_paths.RemoteRoot, cancellationToken).ConfigureAwait(false);
            return options.IsWebDavServer
                ? Result.Success()
                : Result.Failure(StorageErrors.Unavailable("The endpoint did not identify itself as a WebDAV server."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Check WebDAV health")); }
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyDictionary<string, string>>> GetMetadataAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = await GetInfoCoreAsync(path, cancellationToken).ConfigureAwait(false);
        return info.IsSuccess
            ? Result<IReadOnlyDictionary<string, string>>.Success(info.Value!.Metadata)
            : Result<IReadOnlyDictionary<string, string>>.Failure(info.Error!);
    }

    /// <inheritdoc />
    public Task<Result<StorageItem>> SetMetadataAsync(
        string path,
        IReadOnlyDictionary<string, string> metadata,
        StorageMetadataUpdateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(metadata);
        options ??= new StorageMetadataUpdateOptions();
        var validation = options.Validate(metadata);
        if (validation.IsFailure)
            return Task.FromResult(Result<StorageItem>.Failure(validation.Error!));
        return Task.FromResult(Result<StorageItem>.Failure(StorageErrors.Unsupported(
            "This WebDAV adapter exposes discovered properties as read-only metadata; use the native client for server-specific PROPPATCH operations.")));
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
            return Task.FromResult(Result<NativeConnectionLease<TClient>>.Failure(StorageErrors.Unsupported($"WebDAV does not expose native type '{typeof(TClient).FullName}'.")));
        return Task.FromResult(Result<NativeConnectionLease<TClient>>.Success(
            new NativeConnectionLease<TClient>(typed, _ => ValueTask.CompletedTask)));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_ownsClient && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _client.Dispose();
            _http?.Dispose();
            Owned?.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private async Task<Item?> FindItemAsync(ResolvedRemotePath resolved, CancellationToken cancellationToken)
    {
        var parent = RemotePathResolver.Parent(resolved.RemotePath);
        var items = (await _client.List(parent, depth: 1, cancellationToken).ConfigureAwait(false)).ToList();
        var exact = items.FirstOrDefault(item => string.Equals(FromHref(item.Href, parent), resolved.StoragePath, StringComparison.Ordinal));
        if (exact is not null) return exact;
        // The server may compare names without regard to case (IIS, a Windows or macOS share) and answer in its own
        // spelling: when one entry differs only in case, the requested spelling is asked for itself.
        var folded = items.Where(item => string.Equals(FromHref(item.Href, parent), resolved.StoragePath, StringComparison.OrdinalIgnoreCase)).ToList();
        if (folded.Count != 1) return null;
        try
        {
            return folded[0].IsCollection
                ? await _client.GetFolder(resolved.RemotePath, cancellationToken).ConfigureAwait(false)
                : await _client.GetFile(resolved.RemotePath, cancellationToken).ConfigureAwait(false);
        }
        catch (WebDAVException error) when (IsNotFound(error))
        {
            return null;
        }
    }

    private async Task<List<StorageItem>> CollectListingAsync(ResolvedRemotePath root, bool recursive, CancellationToken cancellationToken)
    {
        var result = new List<StorageItem>();
        var pending = new Queue<string>();
        pending.Enqueue(root.RemotePath);
        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();
            var items = await _client.List(directory, depth: 1, cancellationToken).ConfigureAwait(false);
            foreach (var item in items)
            {
                var relative = FromHref(item.Href, directory);
                if (relative is null || relative.Length == 0 || relative == _paths.FromRemotePath(directory)) continue;
                result.Add(ToItem(relative, item));
                if (recursive && item.IsCollection) pending.Enqueue(ToRemotePath(relative));
            }
        }
        return result.GroupBy(item => item.Path, StringComparer.Ordinal).Select(group => group.First()).ToList();
    }

    private async Task EnsureDirectoryAsync(string remotePath, CancellationToken cancellationToken)
    {
        var current = string.Empty;
        foreach (var segment in remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            var storagePath = _paths.FromRemotePath(current);
            if (storagePath is null) continue;
            var exists = await ExistsCoreAsync(storagePath, cancellationToken).ConfigureAwait(false);
            if (exists.IsFailure) throw new WebDAVException(exists.Error!.Message);
            if (exists.Value) continue;
            var parent = RemotePathResolver.Parent(current);
            var created = await _client.CreateDir(EnsureTrailingSlash(parent), segment, cancellationToken).ConfigureAwait(false);
            if (!created) throw new WebDAVException($"Could not create WebDAV directory '{current}'.");
        }
    }

    /// <summary>
    /// Maps a PROPFIND href to a storage path. <paramref name="listed"/> is the remote folder that was listed: a
    /// server that compares names without regard to case answers with its own spelling of that folder
    /// (<c>/Docs/</c> for a request of <c>/docs/</c>), which is taken back to the spelling asked for, so the entries
    /// stay under the requested root instead of being dropped as outside it.
    /// </summary>
    private string? FromHref(string href, string? listed = null)
    {
        // Only http(s) hrefs are absolute: on Unix, Uri also parses "/dir/a%20b" as a file path and keeps
        // "%20" literal, so names needing escapes would never match.
        var value = Uri.TryCreate(href, UriKind.Absolute, out var absolute) && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)
            ? absolute.AbsolutePath
            : href;
        value = Uri.UnescapeDataString(value).Replace('\\', '/');
        if (!value.StartsWith('/')) value = "/" + value;
        if (_basePath != "/" && value.StartsWith(_basePath, StringComparison.OrdinalIgnoreCase))
            value = "/" + value[_basePath.Length..].TrimStart('/');
        if (listed is not null)
        {
            var anchor = "/" + listed.Trim('/');
            if (anchor.Length > 1 && value.StartsWith(anchor, StringComparison.OrdinalIgnoreCase) &&
                !value.StartsWith(anchor, StringComparison.Ordinal) &&
                (value.Length == anchor.Length || value[anchor.Length] == '/'))
                value = anchor + value[anchor.Length..];
        }
        return _paths.FromRemotePath(value.TrimEnd('/'));
    }

    private string ToRemotePath(string storagePath) => _paths.Resolve(storagePath).Value!.RemotePath;

    private static async Task SkipAsync(Stream stream, long count, CancellationToken cancellationToken)
    {
        var buffer = new byte[81_920];
        while (count > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            count -= read;
        }
    }

    private static StorageItem ToItem(string path, Item item) => new()
    {
        Path = path,
        Name = NameOf(path),
        ItemType = item.IsCollection ? StorageItemType.Directory : StorageItemType.File,
        Size = item.IsCollection ? null : item.ContentLength,
        LastModified = item.LastModified.HasValue ? new DateTimeOffset(item.LastModified.Value.ToUniversalTime()) : null,
        ContentType = item.ContentType,
        ETag = item.Etag,
        Metadata = item.FoundProperties?.ToDictionary(pair => $"{pair.Key.Namespace}{pair.Key.LocalName}", pair => pair.Value, StringComparer.Ordinal) ??
            []
    };

    private static StorageItem DirectoryItem(string path) => new()
    {
        Path = path,
        Name = path.Length == 0 ? string.Empty : NameOf(path),
        ItemType = StorageItemType.Directory
    };

    private static string NormalizeBasePath(string? path)
    {
        var value = string.IsNullOrWhiteSpace(path) ? "/" : path.Replace('\\', '/');
        if (!value.StartsWith('/')) value = "/" + value;
        if (!value.EndsWith('/')) value += "/";
        return value;
    }

    /// <summary>
    /// Issues a WebDAV MOVE or COPY. Returns false when the destination exists and overwrite is off.
    /// </summary>
    /// <remarks>
    /// The WebDAV client library does not dispose the responses of its move and copy calls, so each one
    /// strands a pooled connection until garbage collection; with a connection limit the next request
    /// blocks forever. When this backend owns the HTTP stack it sends these requests itself.
    /// </remarks>
    private async Task<DavTransfer> TransferAsync(string method, string sourceRemotePath, string destinationRemotePath, bool overwrite, bool folder, CancellationToken cancellationToken)
    {
        if (_http is null || _endpoint is null)
        {
            // The client library cannot report a 207 Multi-Status; only a backend that owns its HTTP stack can.
            var done = (method, folder) switch
            {
                ("MOVE", true) => await _client.MoveFolder(sourceRemotePath, destinationRemotePath, overwrite, null, null, cancellationToken).ConfigureAwait(false),
                ("MOVE", false) => await _client.MoveFile(sourceRemotePath, destinationRemotePath, overwrite, null, null, cancellationToken).ConfigureAwait(false),
                (_, true) => await _client.CopyFolder(sourceRemotePath, destinationRemotePath, overwrite, null, cancellationToken).ConfigureAwait(false),
                _ => await _client.CopyFile(sourceRemotePath, destinationRemotePath, overwrite, null, cancellationToken).ConfigureAwait(false)
            };
            return done ? DavTransfer.Done : DavTransfer.DestinationExists;
        }

        using var request = new HttpRequestMessage(new HttpMethod(method), ResourceUri(sourceRemotePath, folder));
        request.Headers.TryAddWithoutValidation("Destination", ResourceUri(destinationRemotePath, folder).AbsoluteUri);
        request.Headers.TryAddWithoutValidation("Overwrite", overwrite ? "T" : "F");
        if (folder) request.Headers.TryAddWithoutValidation("Depth", "infinity");
        foreach (var header in _client is Client concrete && concrete.CustomHeaders is { } custom ? custom : [])
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        // 207 is a 2xx status, but for COPY and MOVE it lists the members that failed.
        if (response.StatusCode == System.Net.HttpStatusCode.MultiStatus)
            return DavTransfer.Partial;
        if (response.IsSuccessStatusCode)
            return DavTransfer.Done;
        if (response.StatusCode == System.Net.HttpStatusCode.PreconditionFailed && !overwrite)
            return DavTransfer.DestinationExists;
        throw new WebDAVException((int)response.StatusCode, $"WebDAV {method} failed (Status Code: {(int)response.StatusCode}).");
    }

    private const string LockBody =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><D:lockinfo xmlns:D=\"DAV:\"><D:lockscope><D:exclusive/></D:lockscope>" +
        "<D:locktype><D:write/></D:locktype><D:owner>CL.Storage</D:owner></D:lockinfo>";

    /// <summary>
    /// Deletes a collection only while it is verifiably empty. <c>DELETE</c> on a collection always removes everything
    /// in it, so the collection is first locked (<c>LOCK</c>, depth 0, which RFC 4918 says protects its member list:
    /// nobody else can add a member), then listed, and deleted with the lock token only when the listing is empty. A
    /// server without locks, or a backend that does not own its HTTP stack, gets <c>storage.unsupported</c> and the
    /// folder is left as it is; a folder that holds anything (hidden names included) is a conflict.
    /// </summary>
    private async Task<Result> DeleteEmptyCollectionAsync(ResolvedRemotePath resolved, bool ignoreMissing, CancellationToken cancellationToken)
    {
        if (_http is null || _endpoint is null)
            return Result.Failure(StorageErrors.Unsupported(
                "This WebDAV backend does not own its HTTP client, so it cannot lock a collection to delete it only while empty; the folder was left."));
        var uri = ResourceUri(resolved.RemotePath, folder: true);
        string? token;
        using (var request = new HttpRequestMessage(new HttpMethod("LOCK"), uri))
        {
            request.Content = new StringContent(LockBody, System.Text.Encoding.UTF8, "application/xml");
            request.Headers.TryAddWithoutValidation("Depth", "0");
            request.Headers.TryAddWithoutValidation("Timeout", "Second-60");
            AddCustomHeaders(request);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status == 404)
                return ignoreMissing ? Result.Success() : Result.Failure(StorageErrors.NotFound($"WebDAV item '{resolved.StoragePath}' was not found."));
            if (status == 423)
                return Result.Failure(StorageErrors.Conflict("The WebDAV collection is locked by someone else, so it was not deleted."));
            if (status is 400 or 405 or 412 or 501)
                return Result.Failure(StorageErrors.Unsupported(
                    "The WebDAV server does not lock collections, so a non-recursive delete cannot be made safe; the folder was left."));
            if (!response.IsSuccessStatusCode)
                throw new WebDAVException(status, $"WebDAV LOCK failed (Status Code: {status}).");
            token = response.Headers.TryGetValues("Lock-Token", out var values) ? values.FirstOrDefault()?.Trim() : null;
            if (string.IsNullOrEmpty(token))
                return Result.Failure(StorageErrors.Unsupported(
                    "The WebDAV server granted no lock token, so a non-recursive delete cannot be made safe; the folder was left."));
            if (!token.StartsWith('<')) token = $"<{token}>";
        }

        var deleted = false;
        try
        {
            if (await HasMembersAsync(uri, resolved, cancellationToken).ConfigureAwait(false))
                return Result.Failure(StorageErrors.Conflict("The WebDAV directory is not empty."));
            using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
            // A tagged list: an untagged one would also be checked against the parent (Apache mod_dav does), which
            // holds no such lock, and the DELETE would fail with 424.
            request.Headers.TryAddWithoutValidation("If", $"<{uri.AbsoluteUri}> ({token})");
            AddCustomHeaders(request);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.MultiStatus)
                return Result.Failure(StorageErrors.PartialFailure(
                    $"The WebDAV DELETE of '{resolved.StoragePath}' failed for some members (207 Multi-Status).",
                    $"{StorageErrorInfo.HttpStatusKey}=207"));
            if ((int)response.StatusCode is 412 or 423)
                return Result.Failure(StorageErrors.Conflict("The WebDAV collection changed while it was being deleted, so it was kept."));
            if (!response.IsSuccessStatusCode)
                throw new WebDAVException((int)response.StatusCode, $"WebDAV DELETE failed (Status Code: {(int)response.StatusCode}).");
            deleted = true;
            return Result.Success();
        }
        finally
        {
            if (!deleted)
            {
                try
                {
                    using var unlock = new HttpRequestMessage(new HttpMethod("UNLOCK"), uri);
                    unlock.Headers.TryAddWithoutValidation("Lock-Token", token);
                    AddCustomHeaders(unlock);
                    using var _ = await _http.SendAsync(unlock, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception) { /* The lock times out on its own (60 s). */ }
            }
        }
    }

    /// <summary>
    /// Whether a (locked) collection has any member, hidden names included. Asked with a PROPFIND of just the
    /// resource type: the WebDAV client's own listing asks for every property, and on a locked collection the answer
    /// carries the lock's discovery (with its own <c>href</c>), which that client cannot read.
    /// </summary>
    private async Task<bool> HasMembersAsync(Uri collection, ResolvedRemotePath resolved, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), collection)
        {
            Content = new StringContent(
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><D:propfind xmlns:D=\"DAV:\"><D:prop><D:resourcetype/></D:prop></D:propfind>",
                System.Text.Encoding.UTF8, "application/xml")
        };
        request.Headers.TryAddWithoutValidation("Depth", "1");
        AddCustomHeaders(request);
        using var response = await _http!.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new WebDAVException((int)response.StatusCode, $"WebDAV PROPFIND failed (Status Code: {(int)response.StatusCode}).");
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        System.Xml.Linq.XNamespace dav = "DAV:";
        var hrefs = System.Xml.Linq.XDocument.Parse(body).Root?.Elements(dav + "response").Select(element => element.Element(dav + "href")?.Value) ?? [];
        // Anything but the collection itself is a member; an href that cannot be mapped counts as one too.
        return hrefs.Any(href => href is null || FromHref(href, resolved.RemotePath) is not { } path || path != resolved.StoragePath);
    }

    private void AddCustomHeaders(HttpRequestMessage request)
    {
        foreach (var header in _client is Client concrete && concrete.CustomHeaders is { } custom ? custom : [])
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    /// <summary>Records the certificate each TLS connection is offered; set by the factory.</summary>
    internal ServerIdentityRecorder? Identity { get; init; }

    StorageServerIdentity? IStorageDiagnosticsSource.PresentedIdentity => Identity?.Last;

    StorageSessionPoolStats? IStorageDiagnosticsSource.PoolStats => null;

    /// <summary>Sends <c>OPTIONS</c> to the mounted root and reports the <c>Server</c>, <c>DAV</c>, and <c>Allow</c> headers.</summary>
    async Task<Result<StorageServerDetails>> IStorageDiagnosticsSource.GetServerDetailsAsync(CancellationToken cancellationToken)
    {
        if (_http is null || _endpoint is null)
            return Result<StorageServerDetails>.Failure(StorageErrors.Unsupported("This WebDAV backend does not own its HTTP client."));
        var root = _paths.Resolve(string.Empty);
        if (root.IsFailure) return Result<StorageServerDetails>.Failure(root.Error!);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, ResourceUri(root.Value!.RemotePath, folder: true));
            foreach (var header in _client is Client concrete && concrete.CustomHeaders is { } custom ? custom : [])
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new WebDAVException((int)response.StatusCode, $"WebDAV OPTIONS failed (Status Code: {(int)response.StatusCode}).");
            var server = response.Headers.Server.Count > 0 ? response.Headers.Server.ToString() : null;
            var features = new List<string>();
            if (response.Headers.TryGetValues("DAV", out var dav))
                features.AddRange(dav.SelectMany(value => value.Split(',')).Select(value => value.Trim()).Where(value => value.Length > 0).Select(value => $"DAV {value}"));
            features.AddRange(response.Content.Headers.Allow.Select(method => method.ToUpperInvariant()).Order(StringComparer.Ordinal));
            var negotiated = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["http"] = response.Version.ToString()
            };
            return Result<StorageServerDetails>.Success(new StorageServerDetails(server, server?.Split(' ', 2)[0], features.Distinct(StringComparer.Ordinal).ToArray(), negotiated));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageServerDetails>.Failure(Map(error, "Read WebDAV server details")); }
    }

    private Uri ResourceUri(string remotePath, bool folder)
    {
        var basePath = _basePath.TrimEnd('/');
        var encoded = string.Join('/', remotePath.Split('/').Select(Uri.EscapeDataString));
        if (!encoded.StartsWith('/')) encoded = "/" + encoded;
        if (folder && !encoded.EndsWith('/')) encoded += "/";
        return new Uri(_endpoint!, basePath + encoded);
    }

    private static string EnsureTrailingSlash(string path) => path.EndsWith('/') ? path : path + "/";
    private static string NameOf(string path) => path.Split('/')[^1];
    private static bool IsNotFound(WebDAVException error) => HttpStatusOf(error) == 404;

    /// <summary>Reads the HTTP status from a WebDAV failure.</summary>
    /// <remarks>
    /// The client stores the HTTP status in <c>GetHttpCode()</c>; <c>ErrorCode</c> is usually zero. Some
    /// failures only carry the status name in the message ("Status Code: Unauthorized"), so that is the
    /// last resort.
    /// </remarks>
    internal static int HttpStatusOf(WebDAVException error)
    {
        if (error.GetHttpCode() is > 0 and var http) return http;
        if (error.ErrorCode is >= 100 and < 600) return error.ErrorCode;
        var match = StatusInMessage.Match(error.Message ?? string.Empty);
        if (!match.Success) return 0;
        var token = match.Groups[1].Value;
        if (int.TryParse(token, out var numeric)) return numeric;
        return Enum.TryParse<System.Net.HttpStatusCode>(token, ignoreCase: true, out var named) ? (int)named : 0;
    }

    private static readonly System.Text.RegularExpressions.Regex StatusInMessage =
        new(@"Status Code:\s*([A-Za-z]+|\d{3})", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static Error Map(Exception exception, string operation)
    {
        if (exception is WebDAVConflictException)
            return StorageErrors.Conflict($"{operation}: WebDAV conflict.");
        if (exception is WebDAVException webDav && HttpStatusOf(webDav) is > 0 and var status)
            return ProviderErrorMapper.FromHttpStatus(status, operation, "WebDAV");
        return ProviderErrorMapper.FromTransport(exception, operation, "WebDAV")
            ?? StorageErrors.ProviderError($"{operation}: WebDAV provider failed.", ProviderErrorMapper.ExceptionDetails(exception));
    }
}
