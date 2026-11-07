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

    private static async Task DeleteRemoteAsync(SftpClient client, string remotePath, bool directory, bool recursive, CancellationToken cancellationToken)
    {
        if (!directory)
        {
            await client.DeleteFileAsync(remotePath, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (recursive)
        {
            var children = new List<ISftpFile>();
            await foreach (var item in client.ListDirectoryAsync(remotePath, cancellationToken).ConfigureAwait(false))
                if (item.Name is not "." and not "..") children.Add(item);
            foreach (var child in children)
                await DeleteRemoteAsync(client, child.FullName, child.IsDirectory && !child.IsSymbolicLink, recursive: true, cancellationToken).ConfigureAwait(false);
        }
        await client.DeleteDirectoryAsync(remotePath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Maps a failure and retires the session when the failure left it unusable.</summary>
    private Error Fail(SftpClient? client, Exception exception, string operation)
    {
        var error = Map(exception, operation);
        if (client is not null && IsSessionFault(error))
            MarkFaulted(client, operation, error);
        return error;
    }

    /// <summary>Exception filter that retires a faulted session without catching the exception.</summary>
    private bool Observe(SftpClient? client, Exception exception, string operation)
    {
        if (client is not null && exception is not OperationCanceledException)
            _ = Fail(client, exception, operation);
        return false;
    }

    private void MarkFaulted(SftpClient client, string operation, Error? error = null)
    {
        _clients.MarkFaulted(client);
        try { _observer?.SessionFaulted(ConnectionId, StorageProvider.Sftp, operation, error ?? StorageErrors.ConnectionLost($"{operation}: the transfer was interrupted.")); }
        catch { /* Observers must never change the operation's outcome. */ }
    }

    /// <summary>Failures after which the session's protocol state is unknown and it must not be reused.</summary>
    private static bool IsSessionFault(Error error) =>
        StorageErrorInfo.IsConnectionFault(error) || error.Code == StorageErrors.TimeoutCode;

    private static StorageItem ToItem(string path, ISftpFile item) => ToItem(path, item.Attributes);

    private static StorageItem ToItem(string path, SftpFileAttributes attributes)
    {
        var name = NameOf(path);
        return new StorageItem
        {
            Path = path,
            Name = name,
            ItemType = attributes.IsSymbolicLink ? StorageItemType.Link : attributes.IsDirectory ? StorageItemType.Directory : StorageItemType.File,
            Size = attributes.IsRegularFile ? attributes.Size : null,
            LastModified = new DateTimeOffset(attributes.LastWriteTimeUtc),
            LastAccessed = new DateTimeOffset(attributes.LastAccessTimeUtc),
            UnixMode = ModeOf(attributes),
            OwnerId = attributes.UserId,
            GroupId = attributes.GroupId,
            IsHidden = name.StartsWith('.')
        };
    }

    /// <summary>Rebuilds the numeric mode from the flags SSH.NET exposes.</summary>
    internal static int ModeOf(SftpFileAttributes attributes)
    {
        var mode = 0;
        if (attributes.IsUIDBitSet) mode |= 0x800;
        if (attributes.IsGroupIDBitSet) mode |= 0x400;
        if (attributes.IsStickyBitSet) mode |= 0x200;
        if (attributes.OwnerCanRead) mode |= 0x100;
        if (attributes.OwnerCanWrite) mode |= 0x80;
        if (attributes.OwnerCanExecute) mode |= 0x40;
        if (attributes.GroupCanRead) mode |= 0x20;
        if (attributes.GroupCanWrite) mode |= 0x10;
        if (attributes.GroupCanExecute) mode |= 0x8;
        if (attributes.OthersCanRead) mode |= 0x4;
        if (attributes.OthersCanWrite) mode |= 0x2;
        if (attributes.OthersCanExecute) mode |= 0x1;
        return mode;
    }

    private static StorageItem DirectoryItem(string path) => new()
    {
        Path = path,
        Name = path.Length == 0 ? string.Empty : NameOf(path),
        ItemType = StorageItemType.Directory
    };

    private static string NameOf(string path) => path.Split('/')[^1];

    internal static Error Map(Exception exception, string operation)
    {
        switch (exception)
        {
            case SftpHostKeyRejectedException rejected:
                return StorageErrors.HostKeyRejected(
                    $"{operation}: the SSH host key was not trusted.",
                    $"presentedFingerprint={rejected.Fingerprint}");
            case SftpPathNotFoundException:
                return StorageErrors.NotFound($"{operation}: item was not found.");
            case SftpPermissionDeniedException:
                return StorageErrors.PermissionDenied($"{operation}: access was denied.");
            case SshAuthenticationException:
                return StorageErrors.AuthenticationFailed($"{operation}: the SSH server rejected the credentials.");
            case SshOperationTimeoutException:
                return StorageErrors.Timeout($"{operation}: operation timed out.");
            case ProxyException:
                return StorageErrors.ConnectionFailed($"{operation}: could not connect through the proxy.");
            case SftpException sftp:
                return MapStatus(sftp, operation);
            case SshConnectionException connection:
                return MapDisconnect(connection, operation);
        }
        if (ProviderErrorMapper.FromTransport(exception, operation, "SFTP") is { } transport)
            return transport;
        if (exception is SshException && IsQuotaMessage(exception.Message))
            return StorageErrors.QuotaExceeded($"{operation}: the SFTP server has insufficient storage.");
        if (exception is ObjectDisposedException)
            return StorageErrors.ConnectionLost($"{operation}: the SFTP connection was lost.");
        return StorageErrors.ProviderError($"{operation}: SFTP provider failed.", ProviderErrorMapper.ExceptionDetails(exception));
    }

    private static Error MapStatus(SftpException sftp, string operation)
    {
        var details = $"{StorageErrorInfo.SftpStatusKey}={sftp.StatusCode}";
        return sftp.StatusCode switch
        {
            StatusCode.NoSuchFile => StorageErrors.NotFound($"{operation}: item was not found.", details),
            StatusCode.PermissionDenied => StorageErrors.PermissionDenied($"{operation}: access was denied.", details),
            StatusCode.NoConnection or StatusCode.ConnectionLost => StorageErrors.ConnectionLost($"{operation}: the SFTP connection was lost.", details),
            StatusCode.OperationUnsupported => StorageErrors.Unsupported($"{operation}: the SFTP server does not support this operation.", details),
            StatusCode.Failure when IsQuotaMessage(sftp.Message) => StorageErrors.QuotaExceeded($"{operation}: the SFTP server has insufficient storage.", details),
            _ => StorageErrors.ProviderError($"{operation}: SFTP request failed.", details)
        };
    }

    private static Error MapDisconnect(SshConnectionException connection, string operation)
    {
        var details = $"disconnectReason={connection.DisconnectReason}";
        return connection.DisconnectReason switch
        {
            DisconnectReason.HostKeyNotVerifiable => StorageErrors.HostKeyRejected($"{operation}: the SSH host key was not trusted.", details),
            DisconnectReason.TooManyConnections => StorageErrors.ServerBusy($"{operation}: the SSH server has too many sessions.", details),
            DisconnectReason.NoMoreAuthenticationMethodsAvailable or DisconnectReason.IllegalUserName or DisconnectReason.AuthenticationCanceledByUser
                => StorageErrors.AuthenticationFailed($"{operation}: the SSH server rejected the credentials.", details),
            DisconnectReason.HostNotAllowedToConnect or DisconnectReason.ProtocolVersionNotSupported
                => StorageErrors.ConnectionFailed($"{operation}: the SSH server refused the connection.", details),
            DisconnectReason.KeyExchangeFailed
                => StorageErrors.ConnectionFailed($"{operation}: SSH key exchange failed; check host key and algorithm settings.", details),
            DisconnectReason.ServiceNotAvailable => StorageErrors.Unavailable($"{operation}: the SFTP subsystem is unavailable.", details),
            _ => StorageErrors.ConnectionLost($"{operation}: the SFTP connection was lost.", details)
        };
    }

    private static bool IsQuotaMessage(string? message) =>
        message is not null &&
        (message.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("no space", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("disk full", StringComparison.OrdinalIgnoreCase));
}
