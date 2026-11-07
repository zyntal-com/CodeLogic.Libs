using System.Buffers;
using System.IO.Pipelines;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers;
using CodeLogic.Core.Results;

namespace CL.Storage.Registry;

internal static partial class StorageTransferCoordinator
{

    internal static async Task<Result<StorageTransferSummary>> CopyAsync(
        IStorageBackend source,
        string sourcePath,
        IStorageBackend destination,
        string destinationPath,
        StorageTransferOptions options,
        CancellationToken cancellationToken,
        TransferState? state = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        state ??= new TransferState();

        var sourceInfo = await ResolveSourceAsync(source, sourcePath, options.SourceVersionId, cancellationToken).ConfigureAwait(false);
        if (sourceInfo.IsFailure)
            return Result<StorageTransferSummary>.Failure(sourceInfo.Error!);
        state.Source = sourceInfo.Value;
        if (sourceInfo.Value!.ItemType != StorageItemType.File && options.HasSingleFileGuarantees)
            return Result<StorageTransferSummary>.Failure(StorageErrors.InvalidContent(
                "DestinationCondition, source pinning, ExpectedSourceLength, ExpectedSha256, and ResumeToken apply to single-file transfers only."));

        AggregateProgress? aggregate = null;
        if (options.Progress is { } progress)
        {
            long? totalBytes = sourceInfo.Value.ItemType == StorageItemType.File ? sourceInfo.Value.Size : null;
            long? totalFiles = sourceInfo.Value.ItemType == StorageItemType.File ? 1 : null;
            if (options.PreScan && sourceInfo.Value.ItemType == StorageItemType.Directory)
                (totalBytes, totalFiles) = await ScanAsync(source, sourcePath, cancellationToken).ConfigureAwait(false);
            aggregate = new AggregateProgress(progress, totalBytes, totalFiles);
            options = options with { Progress = aggregate };
        }

        if (ReferenceEquals(source, destination) && sourceInfo.Value!.ItemType == StorageItemType.Directory)
        {
            var relationship = StorageTransferPath.ValidateDirectoryDestination(sourcePath, destinationPath);
            if (relationship.IsFailure)
                return Result<StorageTransferSummary>.Failure(relationship.Error!);
        }

        var cleanup = new TransferCleanupTracker(destination, sourceInfo.Value!.ItemType == StorageItemType.Directory);
        Result<StorageTransferSummary> result;
        try
        {
            result = sourceInfo.Value!.ItemType switch
            {
                StorageItemType.File => await CopyFileAsync(
                    source,
                    sourceInfo.Value,
                    destination,
                    destinationPath,
                    options,
                    cleanup,
                    cancellationToken,
                    state).ConfigureAwait(false),
                StorageItemType.Directory => await CopyDirectoryAsync(
                    source,
                    sourceInfo.Value,
                    destination,
                    destinationPath,
                    options,
                    cleanup,
                    cancellationToken,
                    state).ConfigureAwait(false),
                _ => await TransferLinkAsync(
                    source,
                    sourceInfo.Value,
                    treeRoot: null,
                    destination,
                    destinationPath,
                    options,
                    cleanup,
                    cancellationToken).ConfigureAwait(false)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelledRollback = await cleanup.RollbackAsync(KeptStaging(state)).ConfigureAwait(false);
            if (cleanup.HadReplacements) state.BackupRestored = cancelledRollback.IsSuccess;
            if (cancelledRollback.IsFailure)
            {
                // Not a clean cancel: part of the destination could not be put back.
                state.RollbackIncomplete = true;
                state.BackupLeftBehind ??= cleanup.RetainedBackup;
                return Result<StorageTransferSummary>.Failure(StorageErrors.Cancelled(
                    "The transfer was cancelled and destination rollback was incomplete.",
                    $"rollbackError={cancelledRollback.Error!.Code};{cancelledRollback.Error.Details}"));
            }
            throw;
        }
        catch (Exception error)
        {
            // A throwing provider, phase callback, or progress sink is a failure like any other: staging and
            // backups are settled below instead of leaking.
            result = Result<StorageTransferSummary>.Failure(StorageErrors.FromException(error, "Transfer"));
        }

        if (result.IsFailure)
        {
            state.DirectoriesCreated = cleanup.CreatedDirectories;
            var rollback = await cleanup.RollbackAsync(KeptStaging(state)).ConfigureAwait(false);
            if (cleanup.HadReplacements)
                state.BackupRestored = rollback.IsSuccess;
            if (rollback.IsFailure)
            {
                state.RollbackIncomplete = true;
                state.BackupLeftBehind ??= cleanup.RetainedBackup;
                return Result<StorageTransferSummary>.Failure(StorageErrors.PartialFailure(
                    "The transfer failed and destination rollback was incomplete.",
                    $"transferError={result.Error!.Code};rollbackError={rollback.Error!.Code};{rollback.Error.Details}"));
            }
        }
        else
        {
            // Committed. A replacement backup that cannot be removed is reported as left behind; the transfer
            // itself succeeded.
            var retained = await cleanup.CommitAsync().ConfigureAwait(false);
            result = Result<StorageTransferSummary>.Success(result.Value! with
            {
                BackupLeftBehind = result.Value.BackupLeftBehind ?? retained,
                CreatedDirectories = cleanup.CreatedDirectories
            });
            aggregate?.Complete();
        }
        return result;
    }

    /// <summary>A resumable part file kept for a token; the folders holding it are not rolled back.</summary>
    private static string? KeptStaging(TransferState state) => state.StagingResumable ? state.StagingLeftBehind : null;

    /// <summary>
    /// Describes the source as it will be read: the latest item, or with <paramref name="versionId"/> the pinned
    /// version's size, ETag, and time, so every check, the resume key, and a move's source deletion refer to the
    /// version that is actually copied.
    /// </summary>
    internal static async Task<Result<StorageItem>> ResolveSourceAsync(
        IStorageService source,
        string path,
        string? versionId,
        CancellationToken cancellationToken)
    {
        var latest = await source.GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
        if (versionId is null)
            return latest;
        if (latest.IsFailure)
        {
            // A delete marker as the latest version hides the object, but an older version can still be read.
            if (latest.Error!.Code != StorageErrors.NotFoundCode || source is not IStorageVersionService || !source.Capabilities.Supports(StorageFeature.Versioning))
                return latest;
            var name = path[(path.LastIndexOf('/') + 1)..];
            latest = Result<StorageItem>.Success(new StorageItem { Path = path, Name = name, ItemType = StorageItemType.File });
        }
        if (latest.Value!.ItemType != StorageItemType.File)
            return Result<StorageItem>.Failure(StorageErrors.InvalidContent("SourceVersionId applies to single-file transfers only."));
        if (latest.Value.VersionId == versionId)
            return latest;
        if (source is not IStorageVersionService versions || !source.Capabilities.Supports(StorageFeature.Versioning))
            return Result<StorageItem>.Failure(StorageErrors.Unsupported("The source connection cannot read a specific version."));
        string? token = null;
        do
        {
            var page = await versions.ListVersionsAsync(path, new StorageVersionListOptions { ContinuationToken = token }, cancellationToken).ConfigureAwait(false);
            if (page.IsFailure)
                return Result<StorageItem>.Failure(page.Error!);
            if (page.Value!.Versions.FirstOrDefault(version => version.VersionId == versionId) is { } pinned)
            {
                return pinned.IsDeleteMarker
                    ? Result<StorageItem>.Failure(StorageErrors.NotFound($"The source version '{versionId}' of '{path}' is a delete marker."))
                    : Result<StorageItem>.Success(latest.Value with
                    {
                        Size = pinned.Size,
                        ETag = pinned.ETag,
                        LastModified = pinned.LastModified,
                        VersionId = pinned.VersionId
                    });
            }
            token = page.Value.ContinuationToken;
        } while (token is not null);
        return Result<StorageItem>.Failure(StorageErrors.NotFound($"The source version '{versionId}' of '{path}' was not found."));
    }

    /// <summary>
    /// Adds up per-file progress into one running total for a whole relayed transfer, with totals when they
    /// are known (a single file, or a pre-scanned directory).
    /// </summary>
    private sealed class AggregateProgress(IProgress<StorageTransferProgress> target, long? totalBytes, long? totalFiles) : IProgress<StorageTransferProgress>
    {
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private long _completedBytes;
        private long _completedFiles;

        public void Report(StorageTransferProgress value)
        {
            var done = Interlocked.Read(ref _completedBytes) + value.BytesTransferred;
            if (value.IsCompleted)
            {
                Interlocked.Add(ref _completedBytes, value.BytesTransferred);
                Interlocked.Increment(ref _completedFiles);
            }
            var rate = Rate(done);
            target.Report(new StorageTransferProgress(done, totalBytes, false, rate, Remaining(done, rate), value.ItemPath)
            {
                FilesCompleted = Interlocked.Read(ref _completedFiles),
                FilesTotal = totalFiles
            });
        }

        /// <summary>Counts a file that took no bytes through the relay: skipped, or copied on the server.</summary>
        public void FileDone(string path, long bytes)
        {
            Interlocked.Add(ref _completedBytes, bytes);
            Interlocked.Increment(ref _completedFiles);
            var done = Interlocked.Read(ref _completedBytes);
            var rate = Rate(done);
            target.Report(new StorageTransferProgress(done, totalBytes, false, rate, Remaining(done, rate), path)
            {
                FilesCompleted = Interlocked.Read(ref _completedFiles),
                FilesTotal = totalFiles
            });
        }

        public void Complete()
        {
            var done = Interlocked.Read(ref _completedBytes);
            target.Report(new StorageTransferProgress(done, totalBytes ?? done, true, Rate(done), TimeSpan.Zero)
            {
                FilesCompleted = Interlocked.Read(ref _completedFiles),
                FilesTotal = totalFiles ?? Interlocked.Read(ref _completedFiles)
            });
        }

        private double Rate(long bytes) => _clock.Elapsed.TotalSeconds > 0 ? bytes / _clock.Elapsed.TotalSeconds : 0;

        private TimeSpan? Remaining(long done, double rate) =>
            totalBytes is { } total && rate > 0 && total >= done ? TimeSpan.FromSeconds((total - done) / rate) : null;
    }

    /// <summary>Lists a directory once to count its files and bytes for progress totals.</summary>
    private static async Task<(long? Bytes, long? Files)> ScanAsync(IStorageBackend source, string path, CancellationToken cancellationToken)
    {
        long bytes = 0;
        long files = 0;
        await foreach (var item in source.EnumerateItemsAsync(path, new StorageListOptions { Recursive = true }, cancellationToken).ConfigureAwait(false))
        {
            if (item.IsFailure) return (null, null);
            if (item.Value!.ItemType != StorageItemType.File) continue;
            files++;
            bytes += item.Value.Size ?? 0;
        }
        return (bytes, files);
    }
}
