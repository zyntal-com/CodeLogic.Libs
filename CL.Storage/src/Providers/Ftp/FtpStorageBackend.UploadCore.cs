using System.Diagnostics.CodeAnalysis;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CL.Storage.Providers.Local;
using CodeLogic.Core.Results;
using FluentFTP;
using FluentFTP.Exceptions;

namespace CL.Storage.Providers.Ftp;

public sealed partial class FtpStorageBackend
{

    private async Task<Result<StorageItem>> UploadCoreAsync(string path, Stream source, StorageUploadOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new StorageUploadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StorageItem>.Failure(validation.Error!);
        if (options.Condition is { IsEmpty: false })
            return Result<StorageItem>.Failure(StorageErrors.Unsupported(
                "FTP does not expose portable atomic upload conditions."));
        if (options.Metadata.Count > 0)
            return Result<StorageItem>.Failure(StorageErrors.Unsupported(
                "FTP does not support portable user metadata."));
        var resolved = _paths.Resolve(path, requireNonRoot: true);
        if (resolved.IsFailure) return Result<StorageItem>.Failure(resolved.Error!);
        AsyncFtpClient? client = null;
        string? stagingPath = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var parent = RemotePathResolver.Parent(resolved.Value!.RemotePath);
            if (options.CreateParents)
                await client.CreateDirectory(parent, true, cancellationToken).ConfigureAwait(false);
            else if (!await client.DirectoryExists(parent, cancellationToken).ConfigureAwait(false))
                return Result<StorageItem>.Failure(StorageErrors.NotFound("The FTP destination parent directory was not found."));
            var existing = await StatAsync(client, resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            if (existing?.Type == FtpObjectType.Directory)
                return Result<StorageItem>.Failure(StorageErrors.Conflict("The FTP upload destination is a directory."));
            if (existing is not null && !options.Overwrite)
                return Result<StorageItem>.Failure(StorageErrors.Conflict("The FTP destination already exists."));

            var staging = await AllocateTemporaryPathAsync(client, parent, "upload", cancellationToken).ConfigureAwait(false);
            if (staging.IsFailure)
                return Result<StorageItem>.Failure(staging.Error!);
            stagingPath = staging.Value!;
            var status = await client.UploadStream(
                source,
                stagingPath,
                FtpRemoteExists.Skip,
                createRemoteDir: false,
                progress: null,
                cancellationToken).ConfigureAwait(false);
            if (status == FtpStatus.Skipped)
                return Result<StorageItem>.Failure(StorageErrors.Conflict("The FTP destination already exists."));
            if (status != FtpStatus.Success)
                return Result<StorageItem>.Failure(StorageErrors.ProviderError("The FTP server did not accept the upload."));
            var committed = await CommitPathAsync(
                client,
                stagingPath,
                resolved.Value.RemotePath,
                FtpObjectType.File,
                options.Overwrite,
                cancellationToken).ConfigureAwait(false);
            if (committed.IsFailure)
            {
                // Committed, but the replaced file's backup stayed: the staging file is the destination now.
                if (StorageErrorInfo.DestinationCommitted(committed.Error)) stagingPath = null;
                return Result<StorageItem>.Failure(committed.Error!);
            }
            stagingPath = null;
            var item = await StatAsync(client, resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            return item is null
                ? Result<StorageItem>.Success(FileItem(resolved.Value.StoragePath, source.CanSeek ? source.Length : null))
                : Result<StorageItem>.Success(ToItem(resolved.Value.StoragePath, item));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Fail(client, error, "Upload FTP file")); }
        finally
        {
            if (client is not null && stagingPath is not null)
                await TryDeletePathAsync(client, stagingPath).ConfigureAwait(false);
            if (client is not null)
                await ReleaseClientAsync(client).ConfigureAwait(false);
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
        _retry.ExecuteAsync("Download FTP file", RetryKind.Idempotent, (_, token) => DownloadCoreAsync(path, options, token), cancellationToken);

    private async Task<Result<Stream>> DownloadCoreAsync(string path, StorageDownloadOptions? options, CancellationToken cancellationToken)
    {
        options ??= new StorageDownloadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<Stream>.Failure(validation.Error!);
        if (options.VersionId is not null)
            return Result<Stream>.Failure(StorageErrors.Unsupported(
                "FTP does not support version-specific downloads."));
        var resolved = _paths.Resolve(path, requireNonRoot: true);
        if (resolved.IsFailure) return Result<Stream>.Failure(resolved.Error!);
        AsyncFtpClient? client = null;
        var ownershipTransferred = false;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var item = await StatAsync(client, resolved.Value!.RemotePath, cancellationToken).ConfigureAwait(false);
            if (item is null) return Result<Stream>.Failure(StorageErrors.NotFound($"FTP file '{resolved.Value.StoragePath}' was not found."));
            if (item.Type == FtpObjectType.Directory) return Result<Stream>.Failure(StorageErrors.Conflict("An FTP directory cannot be downloaded as a file."));
            if (item.Size >= 0 && options.Offset > item.Size)
                return Result<Stream>.Failure(StorageErrors.InvalidPath("The range offset exceeds the FTP file length."));
            Stream stream = await client.OpenRead(
                resolved.Value.RemotePath,
                client.Config.DownloadDataType,
                options.Offset,
                checkIfFileExists: true,
                cancellationToken).ConfigureAwait(false);
            if (options.Length.HasValue)
                stream = new RangeReadStream(stream, item.Size >= 0
                    ? Math.Min(options.Length.Value, Math.Max(0, item.Size - options.Offset))
                    : options.Length.Value);
            var owned = new AsyncOwnedResourceStream(stream, () => ReleaseClientAsync(client), () => MarkFaulted(client, "Download FTP file"));
            ownershipTransferred = true;
            return Result<Stream>.Success(owned);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<Stream>.Failure(Fail(client, error, "Download FTP file")); }
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
        _retry.ExecuteAsync("Delete FTP item", RetryKind.NonIdempotent, (_, token) => DeleteCoreAsync(path, options, token), cancellationToken);

    private async Task<Result> DeleteCoreAsync(string path, StorageDeleteOptions? options, CancellationToken cancellationToken)
    {
        options ??= new StorageDeleteOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return validation;
        if (options.Condition is { IsEmpty: false })
            return Result.Failure(StorageErrors.Unsupported(
                "FTP does not expose portable atomic delete conditions."));
        var resolved = _paths.Resolve(path, requireNonRoot: true);
        if (resolved.IsFailure) return Result.Failure(resolved.Error!);
        AsyncFtpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var item = await FindAsync(client, resolved.Value!.RemotePath, cancellationToken).ConfigureAwait(false);
            if (item is null) return options.IgnoreMissing
                ? Result.Success()
                : Result.Failure(StorageErrors.NotFound($"FTP item '{resolved.Value.StoragePath}' was not found."));
            if (item.Type == FtpObjectType.Directory)
            {
                if (!options.Recursive)
                    return await RemoveEmptyDirectoryAsync(client, resolved.Value, cancellationToken).ConfigureAwait(false);
                else
                {
                    await client.DeleteDirectory(resolved.Value.RemotePath, FtpListOption.Recursive | FtpListOption.AllFiles, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await client.DeleteFile(resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            }
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Fail(client, error, "Delete FTP item")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <summary>
    /// Defence in depth for arguments spliced into a raw FTP command: the control connection is line based, so a
    /// control character (CR/LF above all) would end the command and start another. <see cref="StoragePath.Normalize"/>
    /// already rejects them; this keeps a raw command safe if a path ever reaches it by another route.
    /// </summary>
    internal static Error? RawArgumentError(string argument) =>
        StoragePath.IndexOfControlCharacter(argument) >= 0
            ? StorageErrors.InvalidPath("An FTP command argument cannot contain control characters.")
            : null;

    /// <summary>
    /// Removes a directory only if it is empty, with a raw <c>RMD</c>: the server itself refuses a directory that
    /// holds anything, hidden names included, so there is no window between a check and the removal. (FluentFTP's
    /// <c>DeleteDirectory</c> deletes the contents first, and its listing can miss names LIST hides.) When the server
    /// refuses, the directory is listed with hidden names to tell "not empty" from other refusals.
    /// </summary>
    private static async Task<Result> RemoveEmptyDirectoryAsync(AsyncFtpClient client, ResolvedRemotePath resolved, CancellationToken cancellationToken)
    {
        if (RawArgumentError(resolved.RemotePath) is { } invalid) return Result.Failure(invalid);
        var reply = await client.Execute($"RMD {resolved.RemotePath}", cancellationToken).ConfigureAwait(false);
        if (reply.Success) return Result.Success();
        FtpListItem[] children;
        try
        {
            children = await client.GetListing(resolved.RemotePath, FtpListOption.Auto | FtpListOption.AllFiles, cancellationToken).ConfigureAwait(false);
        }
        catch (FtpCommandException)
        {
            // A server that rejects "LIST -a" still lists what it shows.
            children = await client.GetListing(resolved.RemotePath, FtpListOption.Auto, cancellationToken).ConfigureAwait(false);
        }
        if (children.Any(child => child.Name is not ("." or "..")))
            return Result.Failure(StorageErrors.Conflict("The FTP directory is not empty.", $"{StorageErrorInfo.FtpReplyKey}={reply.Code}"));
        return Result.Failure(MapReply(new FtpCommandException(reply), "Delete FTP directory"));
    }
}
