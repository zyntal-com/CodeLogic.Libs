using System.Diagnostics.CodeAnalysis;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CL.Storage.Providers.Local;
using CodeLogic.Core.Results;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;
using Renci.SshNet.Sftp;

namespace CL.Storage.Providers.Sftp;

public sealed partial class SftpStorageBackend
{

    /// <summary>
    /// Uploads through a staging file in the destination folder and renames it into place, on a session the caller
    /// holds (so a copy reads its source and writes the destination on one session).
    /// </summary>
    private async Task<Result<StorageItem>> UploadWithClientAsync(
        SftpClient client,
        ResolvedRemotePath resolved,
        Stream source,
        bool overwrite,
        bool createParents,
        CancellationToken cancellationToken)
    {
        string? stagingPath = null;
        try
        {
            var parent = RemotePathResolver.Parent(resolved.RemotePath);
            if (createParents)
                await EnsureDirectoryAsync(client, parent, cancellationToken).ConfigureAwait(false);
            else if (!await client.ExistsAsync(parent, cancellationToken).ConfigureAwait(false))
                return Result<StorageItem>.Failure(StorageErrors.NotFound("The SFTP destination parent directory was not found."));
            if (await client.ExistsAsync(resolved.RemotePath, cancellationToken).ConfigureAwait(false))
            {
                var existing = await client.GetAttributesAsync(resolved.RemotePath, cancellationToken).ConfigureAwait(false);
                if (existing.IsDirectory)
                    return Result<StorageItem>.Failure(StorageErrors.Conflict("The SFTP upload destination is a directory."));
                if (!overwrite)
                    return Result<StorageItem>.Failure(StorageErrors.Conflict("The SFTP destination already exists."));
            }
            var staging = await AllocateTemporaryPathAsync(client, parent, "upload", cancellationToken).ConfigureAwait(false);
            if (staging.IsFailure)
                return Result<StorageItem>.Failure(staging.Error!);
            stagingPath = staging.Value!;
            await client.UploadFileAsync(source, stagingPath, cancellationToken).ConfigureAwait(false);
            var committed = await CommitPathAsync(
                client,
                stagingPath,
                resolved.RemotePath,
                overwrite,
                cancellationToken).ConfigureAwait(false);
            if (committed.IsFailure)
            {
                // Committed, but the replaced file's backup stayed: the upload succeeded.
                if (StorageErrorInfo.DestinationCommitted(committed.Error)) stagingPath = null;
                return Result<StorageItem>.Failure(committed.Error!);
            }
            stagingPath = null;
            var attributes = await client.GetAttributesAsync(resolved.RemotePath, cancellationToken).ConfigureAwait(false);
            return Result<StorageItem>.Success(ToItem(resolved.StoragePath, attributes));
        }
        finally
        {
            if (stagingPath is not null)
                await TryDeletePathAsync(client, stagingPath).ConfigureAwait(false);
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
        _retry.ExecuteAsync("Download SFTP file", RetryKind.Idempotent, (_, token) => DownloadCoreAsync(path, options, token), cancellationToken);

    private async Task<Result<Stream>> DownloadCoreAsync(string path, StorageDownloadOptions? options, CancellationToken cancellationToken)
    {
        options ??= new StorageDownloadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<Stream>.Failure(validation.Error!);
        if (options.VersionId is not null)
            return Result<Stream>.Failure(StorageErrors.Unsupported(
                "SFTP does not support version-specific downloads."));
        var resolved = _paths.Resolve(path, requireNonRoot: true);
        if (resolved.IsFailure) return Result<Stream>.Failure(resolved.Error!);
        SftpClient? client = null;
        var ownershipTransferred = false;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            if (!await client.ExistsAsync(resolved.Value!.RemotePath, cancellationToken).ConfigureAwait(false))
                return Result<Stream>.Failure(StorageErrors.NotFound($"SFTP file '{resolved.Value.StoragePath}' was not found."));
            var attributes = await client.GetAttributesAsync(resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            if (attributes.IsDirectory)
                return Result<Stream>.Failure(StorageErrors.Conflict("An SFTP directory cannot be downloaded as a file."));
            if (options.Offset > attributes.Size)
                return Result<Stream>.Failure(StorageErrors.InvalidPath("The range offset exceeds the SFTP file length."));
            Stream stream = client.OpenRead(resolved.Value.RemotePath);
            stream.Position = options.Offset;
            if (options.Length.HasValue)
                stream = new RangeReadStream(stream, Math.Min(options.Length.Value, attributes.Size - options.Offset));
            var owned = new AsyncOwnedResourceStream(stream, () => ReleaseClientAsync(client), () => MarkFaulted(client, "Download SFTP file"));
            ownershipTransferred = true;
            return Result<Stream>.Success(owned);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<Stream>.Failure(Fail(client, error, "Download SFTP file")); }
        finally { if (client is not null && !ownershipTransferred) await ReleaseClientAsync(client).ConfigureAwait(false); }
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
    public Task<Result> DeleteAsync(string path, StorageDeleteOptions? options = null, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Delete SFTP item", RetryKind.NonIdempotent, (_, token) => DeleteCoreAsync(path, options, token), cancellationToken);

    private async Task<Result> DeleteCoreAsync(string path, StorageDeleteOptions? options, CancellationToken cancellationToken)
    {
        options ??= new StorageDeleteOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return validation;
        if (options.Condition is { IsEmpty: false })
            return Result.Failure(StorageErrors.Unsupported(
                "SFTP does not expose portable atomic delete conditions."));
        var resolved = _paths.Resolve(path, requireNonRoot: true);
        if (resolved.IsFailure) return Result.Failure(resolved.Error!);
        SftpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            if (!await client.ExistsAsync(resolved.Value!.RemotePath, cancellationToken).ConfigureAwait(false))
                return options.IgnoreMissing
                    ? Result.Success()
                    : Result.Failure(StorageErrors.NotFound($"SFTP item '{resolved.Value.StoragePath}' was not found."));
            var attributes = await client.GetAttributesAsync(resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            if (attributes.IsDirectory && !options.Recursive && await HasChildrenAsync(client, resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false))
                return Result.Failure(StorageErrors.Conflict("The SFTP directory is not empty."));
            await DeleteRemoteAsync(client, resolved.Value.RemotePath, attributes.IsDirectory, options.Recursive, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Fail(client, error, "Delete SFTP item")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Relayed through the client on one session, which reads the source and writes the destination, so a
    /// connection limited to one session can still copy. SFTP has no ETags or versions: source pinning and
    /// destination conditions are unsupported.
    /// </remarks>
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
        SftpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            if (!await client.ExistsAsync(source.Value.RemotePath, cancellationToken).ConfigureAwait(false))
                return Result.Failure(StorageErrors.NotFound($"SFTP item '{source.Value.StoragePath}' was not found."));
            var attributes = await client.GetAttributesAsync(source.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            if (attributes.IsDirectory)
            {
                relationship = StorageTransferPath.ValidateDirectoryDestination(
                    source.Value.StoragePath,
                    destination.Value.StoragePath);
                if (relationship.IsFailure) return relationship;
                return Result.Failure(StorageErrors.Unsupported("SFTP directory copy is not supported."));
            }
            var stream = await client.OpenAsync(source.Value.RemotePath, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false);
            await using var streamLifetime = stream.ConfigureAwait(false);
            var upload = await UploadWithClientAsync(client, destination.Value, stream, options.Overwrite, options.CreateParents, cancellationToken).ConfigureAwait(false);
            return upload.IsSuccess ? Result.Success() : Result.Failure(upload.Error!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Fail(client, error, "Copy SFTP file")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <summary>SFTP items have no ETag or version, so a copy or move cannot be pinned to one or conditioned on one.</summary>
    private static Result? Unpinnable(StorageTransferOptions options) =>
        options.ExpectedSourceETag is not null || options.SourceVersionId is not null || options.DestinationCondition is { IsEmpty: false }
            ? Result.Failure(StorageErrors.Unsupported("SFTP cannot pin a copy or move to a source version or keep a destination condition."))
            : (Result?)null;

    /// <inheritdoc />
    public Task<Result> MoveAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Move SFTP item", RetryKind.NonIdempotent, (_, token) => MoveCoreAsync(sourcePath, destinationPath, options, token), cancellationToken);
}
