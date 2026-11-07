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

    /// <inheritdoc />
    /// <remarks>
    /// Relayed through the client: FTP has no server-side copy, and a download and an upload need two sessions.
    /// FTP items have no ETag or version, so source pinning and destination conditions are unsupported.
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
        var info = await GetInfoAsync(source.Value.StoragePath, cancellationToken).ConfigureAwait(false);
        if (info.IsFailure) return Result.Failure(info.Error!);
        if (info.Value!.ItemType == StorageItemType.Directory)
        {
            relationship = StorageTransferPath.ValidateDirectoryDestination(
                source.Value.StoragePath,
                destination.Value.StoragePath);
            if (relationship.IsFailure) return relationship;
            return Result.Failure(StorageErrors.Unsupported("FTP directory copy is not supported."));
        }
        var download = await DownloadAsync(source.Value.StoragePath, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (download.IsFailure) return Result.Failure(download.Error!);
        var stream = download.Value!;
        await using var streamLifetime = stream.ConfigureAwait(false);
        var upload = await UploadAsync(destination.Value.StoragePath, stream, new StorageUploadOptions
        {
            Overwrite = options.Overwrite,
            CreateParents = options.CreateParents
        }, cancellationToken).ConfigureAwait(false);
        return upload.IsSuccess ? Result.Success() : Result.Failure(upload.Error!);
    }

    /// <summary>FTP items have no ETag or version, so a copy or move cannot be pinned to one or conditioned on one.</summary>
    private static Result? Unpinnable(StorageTransferOptions options) =>
        options.ExpectedSourceETag is not null || options.SourceVersionId is not null || options.DestinationCondition is { IsEmpty: false }
            ? Result.Failure(StorageErrors.Unsupported("FTP cannot pin a copy or move to a source version or keep a destination condition."))
            : (Result?)null;

    /// <inheritdoc />
    public Task<Result> MoveAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Move FTP item", RetryKind.NonIdempotent, (_, token) => MoveCoreAsync(sourcePath, destinationPath, options, token), cancellationToken);

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
        AsyncFtpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var item = await StatAsync(client, source.Value!.RemotePath, cancellationToken).ConfigureAwait(false);
            if (item is null) return Result.Failure(StorageErrors.NotFound($"FTP item '{source.Value.StoragePath}' was not found."));
            if (item.Type == FtpObjectType.Directory)
            {
                relationship = StorageTransferPath.ValidateDirectoryDestination(
                    source.Value.StoragePath,
                    destination.Value!.StoragePath);
                if (relationship.IsFailure) return relationship;
            }
            if (options.CreateParents)
                await client.CreateDirectory(RemotePathResolver.Parent(destination.Value!.RemotePath), true, cancellationToken).ConfigureAwait(false);
            else if (!await client.DirectoryExists(RemotePathResolver.Parent(destination.Value!.RemotePath), cancellationToken).ConfigureAwait(false))
                return Result.Failure(StorageErrors.NotFound("The FTP destination parent directory was not found."));
            return await CommitPathAsync(
                client,
                source.Value.RemotePath,
                destination.Value!.RemotePath,
                item.Type,
                options.Overwrite,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Fail(client, error, "Move FTP item")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Check FTP health", RetryKind.Idempotent, (_, token) => CheckHealthCoreAsync(token), cancellationToken);

    private async Task<Result> CheckHealthCoreAsync(CancellationToken cancellationToken)
    {
        AsyncFtpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            return await client.DirectoryExists(_paths.RemoteRoot, cancellationToken).ConfigureAwait(false)
                ? Result.Success()
                : Result.Failure(StorageErrors.NotFound("The configured FTP root was not found."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Fail(client, error, "Check FTP health")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public bool TryGetNativeClient<TClient>([NotNullWhen(true)] out TClient? client) where TClient : class
    {
        client = null;
        return false;
    }

    /// <inheritdoc />
    public async Task<Result<NativeConnectionLease<TClient>>> OpenNativeConnectionAsync<TClient>(CancellationToken cancellationToken = default) where TClient : class
    {
        if (typeof(TClient) != typeof(AsyncFtpClient))
            return Result<NativeConnectionLease<TClient>>.Failure(StorageErrors.Unsupported($"FTP does not expose native type '{typeof(TClient).FullName}'."));
        AsyncFtpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var typed = (TClient)(object)client;
            client = null;
            return Result<NativeConnectionLease<TClient>>.Success(new NativeConnectionLease<TClient>(
                typed,
                value => _clients.DiscardAsync((AsyncFtpClient)(object)value)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<NativeConnectionLease<TClient>>.Failure(Fail(client, error, "Open native FTP connection")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    /// <remarks>Uses <c>SITE CHMOD</c>, which Unix-style servers implement and Windows servers usually do not.</remarks>
    public Task<Result> SetPermissionsAsync(string path, int unixMode, CancellationToken cancellationToken = default) =>
        WithClientAsync(path, "Set FTP permissions", RetryKind.Idempotent, (client, remote, token) =>
            client.Chmod(remote, UnixPermissions.ToOctalDigits(unixMode), token), cancellationToken);

    /// <inheritdoc />
    public Task<Result> SetOwnerAsync(string path, long? ownerId, long? groupId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure(StorageErrors.Unsupported("FTP has no command for changing file ownership.")));

    /// <inheritdoc />
    /// <remarks>Uses <c>MFMT</c> (or the two-argument <c>MDTM</c>); the access time cannot be set over FTP and is ignored.</remarks>
    public Task<Result> SetTimestampsAsync(string path, DateTimeOffset? lastModified, DateTimeOffset? lastAccessed = null, CancellationToken cancellationToken = default) =>
        lastModified is not { } modified
            ? Task.FromResult(Result.Failure(StorageErrors.Unsupported("FTP can only set the modification time.")))
            : WithClientAsync(path, "Set FTP timestamps", RetryKind.Idempotent, (client, remote, token) =>
                client.SetModifiedTime(remote, modified.UtcDateTime, token), cancellationToken);

    /// <inheritdoc />
    public Task<Result> CreateLinkAsync(string linkPath, string targetPath, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure(StorageErrors.Unsupported("FTP has no standard command for creating links.")));

    /// <inheritdoc />
    public Task<Result<StorageLinkInfo>> ReadLinkAsync(string path, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Read FTP link", RetryKind.Idempotent, async (_, token) =>
        {
            var resolved = _paths.Resolve(path, requireNonRoot: true);
            if (resolved.IsFailure) return Result<StorageLinkInfo>.Failure(resolved.Error!);
            AsyncFtpClient? client = null;
            try
            {
                client = await OpenClientAsync(token).ConfigureAwait(false);
                // The link entry itself is only visible in its parent's listing; stat-style calls follow it.
                var parent = RemotePathResolver.Parent(resolved.Value!.RemotePath);
                var listing = await client.GetListing(parent, FtpListOption.Auto, token).ConfigureAwait(false);
                var entry = listing.FirstOrDefault(item => _paths.FromRemotePath(item.FullName) == resolved.Value.StoragePath);
                if (entry is null)
                    return Result<StorageLinkInfo>.Failure(StorageErrors.NotFound($"FTP item '{resolved.Value.StoragePath}' was not found."));
                if (entry.Type != FtpObjectType.Link || string.IsNullOrWhiteSpace(entry.LinkTarget))
                    return Result<StorageLinkInfo>.Failure(StorageErrors.Conflict($"FTP item '{resolved.Value.StoragePath}' is not a link."));
                return Result<StorageLinkInfo>.Success(LinkInfo(entry.LinkTarget, parent));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { return Result<StorageLinkInfo>.Failure(Fail(client, error, "Read FTP link")); }
            finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
        }, cancellationToken);

    /// <inheritdoc />
    /// <remarks>Uses <c>HASH</c> or the <c>XMD5</c>/<c>XSHA256</c>/<c>XSHA512</c> commands when the server advertises them.</remarks>
    public Task<Result<StorageChecksum>> GetServerChecksumAsync(string path, StorageChecksumAlgorithm algorithm, CancellationToken cancellationToken = default)
    {
        var ftpAlgorithm = algorithm switch
        {
            StorageChecksumAlgorithm.Md5 => FtpHashAlgorithm.MD5,
            StorageChecksumAlgorithm.Sha256 => FtpHashAlgorithm.SHA256,
            StorageChecksumAlgorithm.Sha512 => FtpHashAlgorithm.SHA512,
            _ => FtpHashAlgorithm.NONE
        };
        if (ftpAlgorithm == FtpHashAlgorithm.NONE)
            return Task.FromResult(ProviderChecksums.Unavailable(algorithm, $"FTP has no command for {algorithm} checksums."));
        return _retry.ExecuteAsync("Get FTP checksum", RetryKind.Idempotent, async (_, token) =>
        {
            var resolved = _paths.Resolve(path, requireNonRoot: true);
            if (resolved.IsFailure) return Result<StorageChecksum>.Failure(resolved.Error!);
            AsyncFtpClient? client = null;
            try
            {
                client = await OpenClientAsync(token).ConfigureAwait(false);
                if ((client.HashAlgorithms & ftpAlgorithm) == 0)
                    return ProviderChecksums.Unavailable(algorithm, $"The FTP server does not offer {algorithm} checksums.");
                var hash = await client.GetChecksum(resolved.Value!.RemotePath, ftpAlgorithm, token).ConfigureAwait(false);
                return hash.IsValid && hash.Algorithm == ftpAlgorithm
                    ? ProviderChecksums.FromHex(algorithm, hash.Value)
                    : ProviderChecksums.Unavailable(algorithm);
            }
            catch (FtpHashUnsupportedException)
            {
                return ProviderChecksums.Unavailable(algorithm, $"The FTP server does not offer {algorithm} checksums.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { return Result<StorageChecksum>.Failure(Fail(client, error, "Get FTP checksum")); }
            finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
        }, cancellationToken);
    }
}
