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
    /// <remarks>Uses <c>APPE</c>. Not retried: a lost reply cannot tell whether the bytes were appended.</remarks>
    public async Task<Result<StorageItem>> AppendAsync(string path, Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var resolved = _paths.Resolve(path, requireNonRoot: true);
        if (resolved.IsFailure) return Result<StorageItem>.Failure(resolved.Error!);
        AsyncFtpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            // Forward-only, because FluentFTP rewinds seekable streams and would re-send bytes before a resume offset.
            var status = await client.UploadStream(new ForwardOnlyStream(source), resolved.Value!.RemotePath, FtpRemoteExists.AddToEnd, createRemoteDir: true, progress: null, cancellationToken).ConfigureAwait(false);
            if (status != FtpStatus.Success)
                return Result<StorageItem>.Failure(StorageErrors.ProviderError("The FTP server did not accept the append."));
            var item = await StatAsync(client, resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            return item is null
                ? Result<StorageItem>.Failure(StorageErrors.NotFound("The appended FTP file was not found."))
                : Result<StorageItem>.Success(ToItem(resolved.Value.StoragePath, item));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Fail(client, error, "Append FTP file")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public async Task<Result<StorageCommandResult>> ExecuteCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        if (!AllowRawCommands)
            return Result<StorageCommandResult>.Failure(StorageErrors.Unsupported("Raw commands are disabled for this connection (AllowRawCommands)."));
        if (string.IsNullOrWhiteSpace(command) || StoragePath.IndexOfControlCharacter(command) >= 0)
            return Result<StorageCommandResult>.Failure(StorageErrors.InvalidContent("A command must be one non-empty line without control characters."));
        AsyncFtpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var reply = await client.Execute(command, cancellationToken).ConfigureAwait(false);
            var text = string.IsNullOrEmpty(reply.InfoMessages) ? reply.Message : $"{reply.InfoMessages}\n{reply.Message}";
            return Result<StorageCommandResult>.Success(new StorageCommandResult(reply.Success, reply.Code, text ?? string.Empty, string.Empty));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageCommandResult>.Failure(Fail(client, error, "Execute FTP command")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    /// <remarks>Uses <c>AVBL</c>, which reports available bytes on servers that implement it.</remarks>
    public async Task<Result<StorageSpaceInfo>> GetSpaceAsync(string path = "", CancellationToken cancellationToken = default)
    {
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result<StorageSpaceInfo>.Failure(resolved.Error!);
        if (RawArgumentError(resolved.Value!.RemotePath) is { } invalid) return Result<StorageSpaceInfo>.Failure(invalid);
        AsyncFtpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var reply = await client.Execute($"AVBL {resolved.Value!.RemotePath}", cancellationToken).ConfigureAwait(false);
            return reply.Success && long.TryParse(reply.Message?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var available)
                ? Result<StorageSpaceInfo>.Success(new StorageSpaceInfo(null, available, null))
                : Result<StorageSpaceInfo>.Failure(StorageErrors.Unsupported("The FTP server does not report free space (AVBL)."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageSpaceInfo>.Failure(Fail(client, error, "Get FTP free space")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <summary>
    /// Looks up one item. Servers without MLST answer through LIST, and many (vsftpd, for one) hide names
    /// starting with a dot from it, including the library's own staging files; those are found by listing the
    /// parent with hidden files included.
    /// </summary>
    private static async Task<FtpListItem?> FindAsync(AsyncFtpClient client, string remotePath, CancellationToken cancellationToken)
    {
        var name = remotePath.TrimEnd('/');
        var slash = name.LastIndexOf('/');
        var hidden = slash >= 0 && name[(slash + 1)..].StartsWith('.');
        // Without MLST a lookup lists the parent folder, and LIST hides dot names on many servers (vsftpd, for one):
        // a hidden name, such as the library's own staging files, is found by SIZE or a directory change instead.
        if (hidden && !client.HasFeature(FtpCapability.MLST) && client.HasFeature(FtpCapability.SIZE))
            return await StatAsync(client, remotePath, cancellationToken).ConfigureAwait(false);
        var item = await client.GetObjectInfo(remotePath, true, cancellationToken).ConfigureAwait(false);
        if (item is not null || !hidden || client.HasFeature(FtpCapability.MLST)) return item;
        var parent = slash == 0 ? "/" : name[..slash];
        FtpListItem[] listing;
        try
        {
            listing = await client.GetListing(parent, FtpListOption.Auto | FtpListOption.AllFiles, cancellationToken).ConfigureAwait(false);
        }
        catch (FluentFTP.Exceptions.FtpCommandException)
        {
            // A server that rejects "LIST -a" still lists what it shows.
            listing = await client.GetListing(parent, FtpListOption.Auto, cancellationToken).ConfigureAwait(false);
        }
        return listing.FirstOrDefault(entry => string.Equals(entry.FullName.TrimEnd('/'), name, StringComparison.Ordinal));
    }

    /// <summary>
    /// Looks up whether a path exists and whether it is a file or a directory without listing its folder: MLST
    /// where the server has it, otherwise SIZE (and MDTM) for a file and a directory change for a folder, which
    /// on a LIST-only server keeps a staged upload in a large folder from costing several full listings. A link
    /// is reported as what it points to; <see cref="FindAsync"/> gives the full listing entry.
    /// </summary>
    private static async Task<FtpListItem?> StatAsync(AsyncFtpClient client, string remotePath, CancellationToken cancellationToken)
    {
        if (client.HasFeature(FtpCapability.MLST) || !client.HasFeature(FtpCapability.SIZE))
            return await FindAsync(client, remotePath, cancellationToken).ConfigureAwait(false);
        var name = remotePath.TrimEnd('/');
        var leaf = name[(name.LastIndexOf('/') + 1)..];
        var size = await client.GetFileSize(name, -1, cancellationToken).ConfigureAwait(false);
        if (size >= 0)
        {
            var modified = client.HasFeature(FtpCapability.MDTM)
                ? await client.GetModifiedTime(name, cancellationToken).ConfigureAwait(false)
                : DateTime.MinValue;
            return new FtpListItem(leaf, size, FtpObjectType.File, modified) { FullName = name };
        }
        return await client.DirectoryExists(name, cancellationToken).ConfigureAwait(false)
            ? new FtpListItem(leaf, 0, FtpObjectType.Directory, DateTime.MinValue) { FullName = name }
            : null;
    }

    private StorageLinkInfo LinkInfo(string rawTarget, string linkParent)
    {
        var absolute = rawTarget.StartsWith('/') ? rawTarget : RemotePathResolver.Combine(linkParent, rawTarget);
        return new StorageLinkInfo(rawTarget, _paths.FromRemotePath(absolute));
    }

    private Task<Result> WithClientAsync(
        string path,
        string operation,
        RetryKind kind,
        Func<AsyncFtpClient, string, CancellationToken, Task> action,
        CancellationToken cancellationToken) =>
        _retry.ExecuteAsync(operation, kind, async (_, token) =>
        {
            var resolved = _paths.Resolve(path);
            if (resolved.IsFailure) return Result.Failure(resolved.Error!);
            AsyncFtpClient? client = null;
            try
            {
                client = await OpenClientAsync(token).ConfigureAwait(false);
                await action(client, resolved.Value!.RemotePath, token).ConfigureAwait(false);
                return Result.Success();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { return Result.Failure(Fail(client, error, operation)); }
            finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
        }, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return _pool.ReleaseAsync();
    }

    /// <summary>
    /// Renames <paramref name="sourcePath"/> onto <paramref name="destinationPath"/>. An existing file destination
    /// (with <paramref name="overwrite"/>) is first renamed to a backup, restored if the rename fails, and removed
    /// after it succeeds; an existing directory is never replaced. A backup that cannot be removed after the
    /// commit is reported with <c>destinationState=complete</c> and <c>leftBehind</c>.
    /// </summary>
    private async Task<Result> CommitPathAsync(
        AsyncFtpClient client,
        string sourcePath,
        string destinationPath,
        FtpObjectType sourceType,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var destination = await StatAsync(client, destinationPath, cancellationToken).ConfigureAwait(false);
        if (destination is null)
        {
            var moved = await MovePathAsync(
                client,
                sourcePath,
                destinationPath,
                sourceType,
                cancellationToken).ConfigureAwait(false);
            return moved
                ? Result.Success()
                : Result.Failure(StorageErrors.ProviderError("The FTP server did not commit the item."));
        }
        if (!overwrite)
            return Result.Failure(StorageErrors.Conflict("The FTP destination already exists."));
        // Replacing a directory would delete everything in it (the backup is removed recursively): refuse, as a
        // local move does; the caller can merge through a copy instead.
        if (destination.Type == FtpObjectType.Directory)
            return Result.Failure(StorageErrors.Conflict("The FTP destination is an existing directory, which is not replaced."));

        var backup = await AllocateTemporaryPathAsync(
            client,
            RemotePathResolver.Parent(destinationPath),
            "backup",
            cancellationToken).ConfigureAwait(false);
        if (backup.IsFailure)
            return Result.Failure(backup.Error!);
        var backupPath = backup.Value!;
        if (!await MovePathAsync(
            client,
            destinationPath,
            backupPath,
            destination.Type,
            cancellationToken).ConfigureAwait(false))
            return Result.Failure(StorageErrors.ProviderError(
                "The FTP server could not stage the existing destination for replacement."));

        try
        {
            if (!await MovePathAsync(
                client,
                sourcePath,
                destinationPath,
                sourceType,
                cancellationToken).ConfigureAwait(false))
            {
                var restored = await TryRestoreBackupAsync(
                    client,
                    backupPath,
                    destinationPath,
                    destination.Type).ConfigureAwait(false);
                return restored
                    ? Result.Failure(StorageErrors.ProviderError("The FTP server did not commit the replacement."))
                    : Result.Failure(BackupKept(backupPath));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (await IsCommitCompleteAsync(client, sourcePath, destinationPath).ConfigureAwait(false))
                return await DeleteBackupAsync(client, backupPath).ConfigureAwait(false);
            _ = await TryRestoreBackupAsync(client, backupPath, destinationPath, destination.Type).ConfigureAwait(false);
            throw;
        }
        catch (Exception error)
        {
            if (await IsCommitCompleteAsync(client, sourcePath, destinationPath).ConfigureAwait(false))
                return await DeleteBackupAsync(client, backupPath).ConfigureAwait(false);
            var restored = await TryRestoreBackupAsync(
                client,
                backupPath,
                destinationPath,
                destination.Type).ConfigureAwait(false);
            return restored
                ? Result.Failure(Map(error, "Commit FTP replacement"))
                : Result.Failure(BackupKept(backupPath));
        }

        return await DeleteBackupAsync(client, backupPath).ConfigureAwait(false);
    }
}
