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
        var source = download.Value!;
        await using var sourceLifetime = source.ConfigureAwait(false);
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
}
