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

    /// <summary>Reads an item's attributes, applies a change, and writes back only what changed.</summary>
    private Task<Result> ChangeAttributesAsync(string path, string operation, Action<SftpFileAttributes> change, CancellationToken cancellationToken) =>
        _retry.ExecuteAsync(operation, RetryKind.Idempotent, async (_, token) =>
        {
            var resolved = _paths.Resolve(path);
            if (resolved.IsFailure) return Result.Failure(resolved.Error!);
            SftpClient? client = null;
            try
            {
                client = await OpenClientAsync(token).ConfigureAwait(false);
                var attributes = await client.GetAttributesAsync(resolved.Value!.RemotePath, token).ConfigureAwait(false);
                change(attributes);
                client.SetAttributes(resolved.Value.RemotePath, attributes);
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

    private Task<SftpClient> OpenClientAsync(CancellationToken cancellationToken) =>
        _clients.RentAsync(cancellationToken, _sessionOpened);

    /// <summary>Returns a client to the pool so the next operation reuses its SSH session.</summary>
    private ValueTask ReleaseClientAsync(SftpClient client) => _clients.ReturnAsync(client);

    /// <summary>Stats the working directory so a session the server silently dropped is detected before reuse.</summary>
    private static async Task<bool> ProbeAsync(SftpClient client, CancellationToken cancellationToken)
    {
        await client.GetAttributesAsync(".", cancellationToken).ConfigureAwait(false);
        return client.IsConnected;
    }

    private static ValueTask DestroyClientAsync(SftpClient client)
    {
        try { if (client.IsConnected) client.Disconnect(); }
        catch { }
        finally
        {
            client.Dispose();
            SftpJumpTunnel.Close(client);
        }
        return ValueTask.CompletedTask;
    }

    private async Task<List<StorageItem>> CollectListingAsync(SftpClient client, string remoteRoot, bool recursive, CancellationToken cancellationToken)
    {
        var items = new List<StorageItem>();
        var pending = new Queue<string>();
        pending.Enqueue(remoteRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();
            await foreach (var item in client.ListDirectoryAsync(directory, cancellationToken).ConfigureAwait(false))
            {
                if (item.Name is "." or "..") continue;
                var relative = _paths.FromRemotePath(item.FullName);
                if (relative is null || relative.Length == 0) continue;
                items.Add(ToItem(relative, item));
                if (recursive && item.IsDirectory && !item.IsSymbolicLink)
                    pending.Enqueue(item.FullName);
            }
        }
        return items;
    }

    /// <summary>
    /// Renames <paramref name="sourcePath"/> onto <paramref name="destinationPath"/>. An existing file destination
    /// (with <paramref name="overwrite"/>) is first renamed to a backup, restored if the rename fails, and removed
    /// after it succeeds; an existing directory is never replaced. A backup that cannot be removed after the
    /// commit is reported with <c>destinationState=complete</c> and <c>leftBehind</c>.
    /// </summary>
    private async Task<Result> CommitPathAsync(
        SftpClient client,
        string sourcePath,
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        if (!await client.ExistsAsync(destinationPath, cancellationToken).ConfigureAwait(false))
        {
            await client.RenameFileAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        if (!overwrite)
            return Result.Failure(StorageErrors.Conflict("The SFTP destination already exists."));

        var destinationAttributes = await client.GetAttributesAsync(destinationPath, cancellationToken).ConfigureAwait(false);
        // Replacing a directory would delete everything in it (the backup is removed recursively): refuse, as a
        // local move does; the caller can merge through a copy instead.
        if (destinationAttributes.IsDirectory)
            return Result.Failure(StorageErrors.Conflict("The SFTP destination is an existing directory, which is not replaced."));
        var backup = await AllocateTemporaryPathAsync(
            client,
            RemotePathResolver.Parent(destinationPath),
            "backup",
            cancellationToken).ConfigureAwait(false);
        if (backup.IsFailure)
            return Result.Failure(backup.Error!);
        var backupPath = backup.Value!;
        await client.RenameFileAsync(destinationPath, backupPath, cancellationToken).ConfigureAwait(false);

        try
        {
            await client.RenameFileAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (await IsCommitCompleteAsync(client, sourcePath, destinationPath).ConfigureAwait(false))
                return await DeleteBackupAsync(client, backupPath).ConfigureAwait(false);
            _ = await TryRestoreBackupAsync(client, backupPath, destinationPath).ConfigureAwait(false);
            throw;
        }
        catch (Exception error)
        {
            if (await IsCommitCompleteAsync(client, sourcePath, destinationPath).ConfigureAwait(false))
                return await DeleteBackupAsync(client, backupPath).ConfigureAwait(false);
            var restored = await TryRestoreBackupAsync(client, backupPath, destinationPath).ConfigureAwait(false);
            return restored
                ? Result.Failure(Map(error, "Commit SFTP replacement"))
                : Result.Failure(StorageErrors.PartialFailure(
                    "The SFTP replacement failed and its previous destination could not be restored.",
                    $"{StorageErrorInfo.DestinationStateKey}=backup;{StorageErrorInfo.LeftBehindKey}={_paths.FromRemotePath(backupPath) ?? backupPath}"));
        }

        return await DeleteBackupAsync(client, backupPath).ConfigureAwait(false);
    }

    private static async Task<Result<string>> AllocateTemporaryPathAsync(
        SftpClient client,
        string parent,
        string purpose,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = parent.TrimEnd('/') + $"/.cl-storage-{purpose}-{Guid.NewGuid():N}.tmp";
            if (!await client.ExistsAsync(candidate, cancellationToken).ConfigureAwait(false))
                return Result<string>.Success(candidate);
        }
        return Result<string>.Failure(StorageErrors.Conflict(
            "Unable to allocate a unique SFTP staging path."));
    }

    private static async Task<bool> IsCommitCompleteAsync(
        SftpClient client,
        string sourcePath,
        string destinationPath)
    {
        try
        {
            var sourceExists = await client.ExistsAsync(sourcePath, CancellationToken.None).ConfigureAwait(false);
            var destinationExists = await client.ExistsAsync(destinationPath, CancellationToken.None).ConfigureAwait(false);
            return !sourceExists && destinationExists;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TryRestoreBackupAsync(
        SftpClient client,
        string backupPath,
        string destinationPath)
    {
        try
        {
            if (!await client.ExistsAsync(backupPath, CancellationToken.None).ConfigureAwait(false) ||
                await client.ExistsAsync(destinationPath, CancellationToken.None).ConfigureAwait(false))
                return false;
            await client.RenameFileAsync(backupPath, destinationPath, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Runs before the backup of a replaced file is removed, so a test can make the removal fail.</summary>
    internal Func<SftpClient, string, Task>? BeforeBackupDelete { get; init; }

    /// <summary>Removes the backup of a replaced file once the replacement committed.</summary>
    private async Task<Result> DeleteBackupAsync(SftpClient client, string backupPath)
    {
        try
        {
            if (BeforeBackupDelete is { } hook) await hook(client, backupPath).ConfigureAwait(false);
            await client.DeleteFileAsync(backupPath, CancellationToken.None).ConfigureAwait(false);
            return Result.Success();
        }
        catch
        {
            return Result.Failure(StorageErrors.PartialFailure(
                "The SFTP destination committed, but its temporary backup could not be removed.",
                $"{StorageErrorInfo.DestinationStateKey}=complete;backupState=present;{StorageErrorInfo.LeftBehindKey}={_paths.FromRemotePath(backupPath) ?? backupPath}"));
        }
    }

    private static async Task TryDeletePathAsync(SftpClient client, string path)
    {
        try
        {
            if (!await client.ExistsAsync(path, CancellationToken.None).ConfigureAwait(false))
                return;
            var attributes = await client.GetAttributesAsync(path, CancellationToken.None).ConfigureAwait(false);
            await DeleteRemoteAsync(
                client,
                path,
                attributes.IsDirectory,
                recursive: true,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cleanup must not mask the primary upload result.
        }
    }

    private static async Task EnsureDirectoryAsync(SftpClient client, string remotePath, CancellationToken cancellationToken)
    {
        var current = string.Empty;
        foreach (var segment in remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            if (!await client.ExistsAsync(current, cancellationToken).ConfigureAwait(false))
                await client.CreateDirectoryAsync(current, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> HasChildrenAsync(SftpClient client, string remotePath, CancellationToken cancellationToken)
    {
        await foreach (var item in client.ListDirectoryAsync(remotePath, cancellationToken).ConfigureAwait(false))
            if (item.Name is not "." and not "..") return true;
        return false;
    }
}
