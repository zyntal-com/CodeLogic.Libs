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
        SftpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            if (!await client.ExistsAsync(source.Value!.RemotePath, cancellationToken).ConfigureAwait(false))
                return Result.Failure(StorageErrors.NotFound($"SFTP item '{source.Value.StoragePath}' was not found."));
            var sourceAttributes = await client.GetAttributesAsync(source.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            if (sourceAttributes.IsDirectory)
            {
                relationship = StorageTransferPath.ValidateDirectoryDestination(
                    source.Value.StoragePath,
                    destination.Value!.StoragePath);
                if (relationship.IsFailure) return relationship;
            }
            var parent = RemotePathResolver.Parent(destination.Value!.RemotePath);
            if (options.CreateParents)
                await EnsureDirectoryAsync(client, parent, cancellationToken).ConfigureAwait(false);
            else if (!await client.ExistsAsync(parent, cancellationToken).ConfigureAwait(false))
                return Result.Failure(StorageErrors.NotFound("The SFTP destination parent directory was not found."));
            return await CommitPathAsync(
                client,
                source.Value.RemotePath,
                destination.Value.RemotePath,
                options.Overwrite,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Fail(client, error, "Move SFTP item")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Check SFTP health", RetryKind.Idempotent, (_, token) => CheckHealthCoreAsync(token), cancellationToken);

    private async Task<Result> CheckHealthCoreAsync(CancellationToken cancellationToken)
    {
        SftpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            return await client.ExistsAsync(_paths.RemoteRoot, cancellationToken).ConfigureAwait(false)
                ? Result.Success()
                : Result.Failure(StorageErrors.NotFound("The configured SFTP root was not found."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Fail(client, error, "Check SFTP health")); }
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
        if (typeof(TClient) != typeof(SftpClient))
            return Result<NativeConnectionLease<TClient>>.Failure(StorageErrors.Unsupported($"SFTP does not expose native type '{typeof(TClient).FullName}'."));
        SftpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var typed = (TClient)(object)client;
            client = null;
            return Result<NativeConnectionLease<TClient>>.Success(new NativeConnectionLease<TClient>(
                typed,
                value => _clients.DiscardAsync((SftpClient)(object)value)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<NativeConnectionLease<TClient>>.Failure(Fail(client, error, "Open native SFTP connection")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public Task<Result> SetPermissionsAsync(string path, int unixMode, CancellationToken cancellationToken = default) =>
        ChangeAttributesAsync(path, "Set SFTP permissions", attributes =>
        {
            attributes.SetPermissions((short)UnixPermissions.ToOctalDigits(unixMode & 0x1FF));
            attributes.IsUIDBitSet = (unixMode & 0x800) != 0;
            attributes.IsGroupIDBitSet = (unixMode & 0x400) != 0;
            attributes.IsStickyBitSet = (unixMode & 0x200) != 0;
        }, cancellationToken);

    /// <inheritdoc />
    public Task<Result> SetOwnerAsync(string path, long? ownerId, long? groupId, CancellationToken cancellationToken = default) =>
        ChangeAttributesAsync(path, "Set SFTP owner", attributes =>
        {
            if (ownerId is { } owner) attributes.UserId = checked((int)owner);
            if (groupId is { } group) attributes.GroupId = checked((int)group);
        }, cancellationToken);

    /// <inheritdoc />
    public Task<Result> SetTimestampsAsync(string path, DateTimeOffset? lastModified, DateTimeOffset? lastAccessed = null, CancellationToken cancellationToken = default) =>
        ChangeAttributesAsync(path, "Set SFTP timestamps", attributes =>
        {
            if (lastModified is { } modified) attributes.LastWriteTimeUtc = modified.UtcDateTime;
            if (lastAccessed is { } accessed) attributes.LastAccessTimeUtc = accessed.UtcDateTime;
        }, cancellationToken);

    /// <inheritdoc />
    public Task<Result> CreateLinkAsync(string linkPath, string targetPath, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Create SFTP link", RetryKind.NonIdempotent, async (_, token) =>
        {
            var link = _paths.Resolve(linkPath, requireNonRoot: true);
            if (link.IsFailure) return Result.Failure(link.Error!);
            var target = _paths.Resolve(targetPath);
            if (target.IsFailure) return Result.Failure(target.Error!);
            SftpClient? client = null;
            try
            {
                client = await OpenClientAsync(token).ConfigureAwait(false);
                if (await client.ExistsAsync(link.Value!.RemotePath, token).ConfigureAwait(false))
                    return Result.Failure(StorageErrors.Conflict("The SFTP link path already exists."));
                // SSH.NET swaps the arguments for OpenSSH's reversed SSH_FXP_SYMLINK order itself.
                client.SymbolicLink(target.Value!.RemotePath, link.Value.RemotePath);
                return Result.Success();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { return Result.Failure(Fail(client, error, "Create SFTP link")); }
            finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
        }, cancellationToken);

    /// <inheritdoc />
    /// <remarks>SSH.NET does not expose SFTP <c>readlink</c>, so link targets cannot be read over SFTP.</remarks>
    public Task<Result<StorageLinkInfo>> ReadLinkAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<StorageLinkInfo>.Failure(StorageErrors.Unsupported(
            "Reading SFTP link targets is not supported by the SSH library.")));

    /// <inheritdoc />
    /// <remarks>Not retried: a lost reply cannot tell how many bytes were appended.</remarks>
    public async Task<Result<StorageItem>> AppendAsync(string path, Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var resolved = _paths.Resolve(path, requireNonRoot: true);
        if (resolved.IsFailure) return Result<StorageItem>.Failure(resolved.Error!);
        SftpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            // The server's append flag (SSH_FXF_APPEND, which also creates a missing file): every write lands at the
            // end the server sees, so concurrent appenders do not overwrite each other as a client-side seek would.
            var target = await client.OpenAsync(resolved.Value!.RemotePath, FileMode.Append, FileAccess.Write, cancellationToken).ConfigureAwait(false);
            await using (target.ConfigureAwait(false))
            {
                await source.CopyToAsync(target, 65_536, cancellationToken).ConfigureAwait(false);
            }
            var attributes = await client.GetAttributesAsync(resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            return Result<StorageItem>.Success(ToItem(resolved.Value.StoragePath, attributes));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Fail(client, error, "Append SFTP file")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Runs in an SSH shell session as the connection's account, starting in its home directory rather
    /// than <c>Root</c>. Servers that only allow SFTP (<c>ForceCommand internal-sftp</c>) refuse it.
    /// </remarks>
    public async Task<Result<StorageCommandResult>> ExecuteCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        if (CommandClientFactory is null)
            return Result<StorageCommandResult>.Failure(StorageErrors.Unsupported("Raw commands are disabled for this connection (AllowRawCommands)."));
        if (string.IsNullOrWhiteSpace(command))
            return Result<StorageCommandResult>.Failure(StorageErrors.InvalidContent("A command is required."));
        SshClient? client = null;
        try
        {
            client = CommandClientFactory();
            await SftpHostKeyTracker.ConnectAsync(client, cancellationToken).ConfigureAwait(false);
            using var run = client.CreateCommand(command);
            await run.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            var exit = run.ExitStatus ?? -1;
            return Result<StorageCommandResult>.Success(new StorageCommandResult(
                exit == 0,
                exit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                run.Result ?? string.Empty,
                run.Error ?? string.Empty));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageCommandResult>.Failure(Map(error, "Execute SSH command")); }
        finally
        {
            if (client is not null)
            {
                try { if (client.IsConnected) client.Disconnect(); }
                catch { /* Best effort. */ }
                client.Dispose();
                SftpJumpTunnel.Close(client);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>Uses the <c>statvfs@openssh.com</c> extension, which OpenSSH servers provide.</remarks>
    public async Task<Result<StorageSpaceInfo>> GetSpaceAsync(string path = "", CancellationToken cancellationToken = default)
    {
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result<StorageSpaceInfo>.Failure(resolved.Error!);
        SftpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var status = await client.GetStatusAsync(resolved.Value!.RemotePath, cancellationToken).ConfigureAwait(false);
            var block = (long)status.BlockSize;
            return Result<StorageSpaceInfo>.Success(new StorageSpaceInfo(
                (long)status.TotalBlocks * block,
                (long)status.AvailableBlocks * block,
                ((long)status.TotalBlocks - (long)status.FreeBlocks) * block));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SshException error) when (error.Message.Contains("statvfs", StringComparison.OrdinalIgnoreCase))
        {
            // SSH.NET reports a server without the extension as a plain SshException naming it.
            return Result<StorageSpaceInfo>.Failure(StorageErrors.Unsupported("The SFTP server does not report free space (statvfs@openssh.com)."));
        }
        catch (Exception error) { return Result<StorageSpaceInfo>.Failure(Fail(client, error, "Get SFTP free space")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }
}
