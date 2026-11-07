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

public sealed partial class WebDavStorageBackend
{

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
        var source = new MemoryStream(content, writable: false);
        await using var sourceLifetime = source.ConfigureAwait(false);
        return await UploadAsync(path, source, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<Stream>> DownloadAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default) =>
        await StorageTransferPipeline.MeterAsync(this, path, await DownloadUnmeteredAsync(path, options, cancellationToken).ConfigureAwait(false), options, cancellationToken).ConfigureAwait(false);

    private Task<Result<Stream>> DownloadUnmeteredAsync(string path, StorageDownloadOptions? options, CancellationToken cancellationToken) =>
        _retry.ExecuteAsync("Download WebDAV file", RetryKind.Idempotent, (_, token) => DownloadCoreAsync(path, options, token), cancellationToken);
}
