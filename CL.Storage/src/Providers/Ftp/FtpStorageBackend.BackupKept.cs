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

    /// <summary>A replacement that failed and could not put the previous destination back: it is in the backup.</summary>
    private Error BackupKept(string backupPath) => StorageErrors.PartialFailure(
        "The FTP replacement failed and its previous destination could not be restored.",
        $"{StorageErrorInfo.DestinationStateKey}=backup;{StorageErrorInfo.LeftBehindKey}={_paths.FromRemotePath(backupPath) ?? backupPath}");

    private static Task<bool> MovePathAsync(
        AsyncFtpClient client,
        string sourcePath,
        string destinationPath,
        FtpObjectType sourceType,
        CancellationToken cancellationToken) =>
        sourceType == FtpObjectType.Directory
            ? client.MoveDirectory(sourcePath, destinationPath, FtpRemoteExists.Skip, cancellationToken)
            : client.MoveFile(sourcePath, destinationPath, FtpRemoteExists.Skip, cancellationToken);

    /// <summary>
    /// A new internal name in <paramref name="parent"/>. Random, so it is not probed: on servers that hide dot
    /// names from LIST a probe that misses costs a listing of the whole folder. Uploads and renames onto it refuse
    /// an existing name anyway.
    /// </summary>
    private static Task<Result<string>> AllocateTemporaryPathAsync(
        AsyncFtpClient client,
        string parent,
        string purpose,
        CancellationToken cancellationToken)
    {
        _ = client;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Result<string>.Success(parent.TrimEnd('/') + $"/.cl-storage-{purpose}-{Guid.NewGuid():N}.tmp"));
    }

    private static async Task<bool> IsCommitCompleteAsync(
        AsyncFtpClient client,
        string sourcePath,
        string destinationPath)
    {
        try
        {
            var source = await StatAsync(client, sourcePath, CancellationToken.None).ConfigureAwait(false);
            var destination = await StatAsync(client, destinationPath, CancellationToken.None).ConfigureAwait(false);
            return source is null && destination is not null;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TryRestoreBackupAsync(
        AsyncFtpClient client,
        string backupPath,
        string destinationPath,
        FtpObjectType backupType)
    {
        try
        {
            if (await StatAsync(client, destinationPath, CancellationToken.None).ConfigureAwait(false) is not null)
                return false;
            return await MovePathAsync(
                client,
                backupPath,
                destinationPath,
                backupType,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Runs before the backup of a replaced file is removed, so a test can make the removal fail.</summary>
    internal Func<AsyncFtpClient, string, Task>? BeforeBackupDelete { get; init; }

    /// <summary>Removes the backup of a replaced file once the replacement committed.</summary>
    private async Task<Result> DeleteBackupAsync(AsyncFtpClient client, string backupPath)
    {
        try
        {
            if (BeforeBackupDelete is { } hook) await hook(client, backupPath).ConfigureAwait(false);
            await client.DeleteFile(backupPath, CancellationToken.None).ConfigureAwait(false);
            return Result.Success();
        }
        catch
        {
            return Result.Failure(StorageErrors.PartialFailure(
                "The FTP destination committed, but its temporary backup could not be removed.",
                $"{StorageErrorInfo.DestinationStateKey}=complete;backupState=present;{StorageErrorInfo.LeftBehindKey}={_paths.FromRemotePath(backupPath) ?? backupPath}"));
        }
    }

    private static async Task TryDeletePathAsync(AsyncFtpClient client, string path)
    {
        try
        {
            var item = await StatAsync(client, path, CancellationToken.None).ConfigureAwait(false);
            if (item is null)
                return;
            if (item.Type == FtpObjectType.Directory)
                await client.DeleteDirectory(path, FtpListOption.Recursive | FtpListOption.AllFiles, CancellationToken.None).ConfigureAwait(false);
            else
                await client.DeleteFile(path, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cleanup must not mask the primary upload result.
        }
    }

    private Task<AsyncFtpClient> OpenClientAsync(CancellationToken cancellationToken) =>
        _clients.RentAsync(cancellationToken, _sessionOpened);

    /// <summary>Returns a client to the pool so the next operation reuses its control connection.</summary>
    private ValueTask ReleaseClientAsync(AsyncFtpClient client) => _clients.ReturnAsync(client);

    /// <summary>Sends NOOP so a session the server silently dropped is detected before reuse.</summary>
    private static async Task<bool> ProbeAsync(AsyncFtpClient client, CancellationToken cancellationToken)
    {
        var reply = await client.Execute("NOOP", cancellationToken).ConfigureAwait(false);
        return reply.Success && client.IsConnected;
    }

    private static async ValueTask DestroyClientAsync(AsyncFtpClient client)
    {
        try
        {
            if (client.IsConnected)
                await client.Disconnect(CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
        finally { client.Dispose(); }
    }

    /// <summary>Maps a failure and retires the session when the failure left it unusable.</summary>
    private Error Fail(AsyncFtpClient? client, Exception exception, string operation)
    {
        var error = Map(exception, operation);
        if (client is not null && IsSessionFault(error))
            MarkFaulted(client, operation, error);
        return error;
    }

    /// <summary>Exception filter that retires a faulted session without catching the exception.</summary>
    private bool Observe(AsyncFtpClient? client, Exception exception, string operation)
    {
        if (client is not null && exception is not OperationCanceledException)
            _ = Fail(client, exception, operation);
        return false;
    }

    private void MarkFaulted(AsyncFtpClient client, string operation, Error? error = null)
    {
        _clients.MarkFaulted(client);
        try { _observer?.SessionFaulted(ConnectionId, StorageProvider.Ftp, operation, error ?? StorageErrors.ConnectionLost($"{operation}: the transfer was interrupted.")); }
        catch { /* Observers must never change the operation's outcome. */ }
    }

    /// <summary>Failures after which the session's protocol state is unknown and it must not be reused.</summary>
    private static bool IsSessionFault(Error error) =>
        StorageErrorInfo.IsConnectionFault(error) || error.Code == StorageErrors.TimeoutCode;

    private static StorageItem ToItem(string path, FtpListItem item)
    {
        var name = NameOf(path);
        return new StorageItem
        {
            Path = path,
            Name = name,
            ItemType = item.Type switch
            {
                FtpObjectType.Directory => StorageItemType.Directory,
                FtpObjectType.Link => StorageItemType.Link,
                _ => StorageItemType.File
            },
            Size = item.Type == FtpObjectType.File && item.Size >= 0 ? item.Size : null,
            LastModified = Utc(item.Modified),
            ModifiedPrecision = ListedPrecision(item),
            Created = Utc(item.Created),
            UnixMode = ModeOf(item.Chmod),
            Owner = string.IsNullOrWhiteSpace(item.RawOwner) ? null : item.RawOwner,
            Group = string.IsNullOrWhiteSpace(item.RawGroup) ? null : item.RawGroup,
            LinkTarget = string.IsNullOrWhiteSpace(item.LinkTarget) ? null : item.LinkTarget,
            IsHidden = name.StartsWith('.')
        };
    }

    /// <summary>
    /// How coarse a listed time is. A LIST line gives minutes ("Sep 24 12:34", "09-24-26 12:34PM"), or only a date
    /// for files older than about six months ("Sep 24 2025"); MLSD/MLST ("modify=") and MDTM, which leaves no line,
    /// give exact seconds. Comparing an exact time with a listed one must allow for this, or an unchanged file on a
    /// LIST-only server would look changed every time.
    /// </summary>
    internal static TimeSpan? ListedPrecision(FtpListItem item)
    {
        var line = item.Input;
        if (string.IsNullOrEmpty(line) || item.Modified == DateTime.MinValue) return null;
        if (line.Contains("modify=", StringComparison.OrdinalIgnoreCase)) return null;
        return ListedTimeOfDay.IsMatch(line) ? TimeSpan.FromMinutes(1) : TimeSpan.FromDays(1);
    }

    /// <summary>
    /// The client converts listing times to UTC; an unspecified kind must not be reinterpreted as the
    /// local machine's time zone, which shifted times by the client's UTC offset.
    /// </summary>
    internal static DateTimeOffset? Utc(DateTime value) => value == DateTime.MinValue
        ? null
        : new DateTimeOffset(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc));

    /// <summary>FluentFTP reports modes as decimal digits (755); converts them to permission bits.</summary>
    internal static int? ModeOf(int chmod) =>
        chmod > 0 && UnixPermissions.TryParseOctal(chmod.ToString(System.Globalization.CultureInfo.InvariantCulture), out var mode) ? mode : null;

    private static StorageItem DirectoryItem(string path) => new()
    {
        Path = path,
        Name = path.Length == 0 ? string.Empty : NameOf(path),
        ItemType = StorageItemType.Directory
    };

    private static StorageItem FileItem(string path, long? size) => new()
    {
        Path = path,
        Name = NameOf(path),
        ItemType = StorageItemType.File,
        Size = size,
        LastModified = DateTimeOffset.UtcNow
    };

    private static string NameOf(string path) => path.Split('/')[^1];
}
