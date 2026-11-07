using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CL.Storage.Providers;
using CL.Storage.Providers.Azure;
using CL.Storage.Providers.Ftp;
using CL.Storage.Providers.GoogleCloud;
using CL.Storage.Providers.Local;
using CL.Storage.Providers.S3;
using CL.Storage.Providers.Sftp;
using CL.Storage.Providers.Swift;
using CL.Storage.Providers.WebDav;
using CL.Storage.Registry;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using CodeLogic.Framework.Libraries;

namespace CL.Storage;

public sealed partial class StorageLibrary
{

    /// <summary>
    /// Downloads a complete storage directory into a caller-selected local directory with per-file
    /// atomic replacement and rollback of changes made by a failed transfer.
    /// </summary>
    public async Task<Result<StorageDirectoryTransferReport>> DownloadDirectoryAsync(
        string sourceConnectionId,
        string sourcePath,
        string destinationDirectoryPath,
        StorageTransferOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionId(sourceConnectionId);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageTransferOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
            return Result<StorageDirectoryTransferReport>.Failure(validation.Error!);
        var normalizedSource = StoragePath.Normalize(sourcePath);
        if (normalizedSource.IsFailure)
            return Result<StorageDirectoryTransferReport>.Failure(normalizedSource.Error!);
        if (string.IsNullOrWhiteSpace(destinationDirectoryPath))
            return Result<StorageDirectoryTransferReport>.Failure(StorageErrors.InvalidPath(
                "A local destination directory path is required."));

        string parent;
        string localDestinationName;
        try
        {
            var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectoryPath));
            if (File.Exists(destination))
                return Result<StorageDirectoryTransferReport>.Failure(StorageErrors.Conflict(
                    "The local directory destination is an existing file."));
            parent = Path.GetDirectoryName(destination) ?? string.Empty;
            localDestinationName = Path.GetFileName(destination);
            if (parent.Length == 0 || localDestinationName.Length == 0)
                return Result<StorageDirectoryTransferReport>.Failure(StorageErrors.InvalidPath(
                    "The local destination must identify a named directory below a parent directory."));
            if (options.CreateParents)
                Directory.CreateDirectory(parent);
            else if (!Directory.Exists(parent))
                return Result<StorageDirectoryTransferReport>.Failure(StorageErrors.NotFound(
                    "The local destination parent directory was not found."));
        }
        catch (Exception error)
        {
            return Result<StorageDirectoryTransferReport>.Failure(StorageErrors.FromException(
                error,
                "Resolve local download directory"));
        }

        var destinationBackend = new LocalStorageBackend(
            ".cl-storage-directory-download",
            new LocalConnectionConfig { RootPath = parent, FollowLinks = false },
            _storageConfig?.MaxBufferedDownloadBytes ?? LocalStorageBackend.DefaultMaxBufferedDownloadBytes);
        await using var destinationBackendLifetime = destinationBackend.ConfigureAwait(false);
        BackendEntry.BackendOperationLease? sourceLease = null;
        StorageEventPublisher? publisher = null;
        StorageDirectoryTransferReport? report = null;
        string? sourceId = null;
        StorageProvider sourceProvider = default;
        try
        {
            sourceLease = AcquireOperation(sourceConnectionId);
            sourceId = sourceLease.Backend.ConnectionId;
            sourceProvider = sourceLease.Backend.Provider;
            var sourceInfo = await sourceLease.Backend.GetInfoAsync(
                normalizedSource.Value!,
                cancellationToken).ConfigureAwait(false);
            if (sourceInfo.IsFailure)
                return Result<StorageDirectoryTransferReport>.Failure(sourceInfo.Error!);
            if (sourceInfo.Value!.ItemType != StorageItemType.Directory)
                return Result<StorageDirectoryTransferReport>.Failure(StorageErrors.Conflict(
                    "DownloadDirectoryAsync requires a storage directory source."));

            var copied = await StorageTransferCoordinator.CopyAsync(
                sourceLease.Backend,
                normalizedSource.Value!,
                destinationBackend,
                localDestinationName,
                options,
                cancellationToken).ConfigureAwait(false);
            if (copied.IsFailure)
                return Result<StorageDirectoryTransferReport>.Failure(copied.Error!);
            report = new StorageDirectoryTransferReport(
                copied.Value!.Files,
                copied.Value.Directories,
                copied.Value.Bytes,
                copied.Value.SkippedFiles);
            publisher = CaptureEventPublisher();
        }
        finally
        {
            sourceLease?.Dispose();
        }

        await publisher!.PublishAsync(new StorageDirectoryDownloadedEvent(
            sourceId!,
            sourceProvider,
            normalizedSource.Value!,
            report!.Files,
            report.Directories,
            report.Bytes,
            DateTimeOffset.UtcNow)).ConfigureAwait(false);
        return Result<StorageDirectoryTransferReport>.Success(report);
    }

    /// <summary>
    /// Moves a file or directory between mounted connections. The source is deleted only after the
    /// complete destination tree has committed successfully, and a single file only while it is still the
    /// version that was copied.
    /// </summary>
    /// <returns>What happened, including whether the source was deleted.</returns>
    public Task<StorageTransferReport> MoveAsync(
        string sourceConnectionId,
        string sourcePath,
        string destinationConnectionId,
        string destinationPath,
        StorageTransferOptions? options = null,
        CancellationToken cancellationToken = default) =>
        TransferAsync(
            sourceConnectionId,
            sourcePath,
            destinationConnectionId,
            destinationPath,
            options,
            move: true,
            cancellationToken);
}
