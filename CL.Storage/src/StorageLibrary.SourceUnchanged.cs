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
    /// Succeeds when the source is still the version <paramref name="read"/> describes, read immediately before
    /// a native move that cannot be pinned to it.
    /// </summary>
    private static async Task<Result> SourceUnchangedAsync(IStorageBackend source, string path, StorageItem read, CancellationToken cancellationToken)
    {
        var current = await source.GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
        if (current.IsFailure)
            return Result.Failure(current.Error!);
        return current.Value!.ItemType == read.ItemType && current.Value.Size == read.Size &&
            current.Value.LastModified == read.LastModified && StorageTransferCoordinator.SameVersion(read, current.Value)
            ? Result.Success()
            : Result.Failure(StorageErrors.Conflict($"The source '{path}' changed after it was read, so it was not moved."));
    }

    /// <summary>Counts the files, folders and bytes below a directory, ignoring the caller's cancel; zeros when it cannot be listed.</summary>
    private static async Task<(long Files, long Directories, long Bytes)> CountTreeAsync(IStorageBackend backend, string path)
    {
        long files = 0, directories = 0, bytes = 0;
        try
        {
            await foreach (var item in backend.EnumerateItemsAsync(path, new StorageListOptions { Recursive = true }, CancellationToken.None).ConfigureAwait(false))
            {
                if (item.IsFailure) break;
                if (item.Value!.ItemType == StorageItemType.Directory)
                    directories++;
                else
                {
                    files++;
                    bytes += item.Value.Size ?? 0;
                }
            }
        }
        catch (Exception)
        {
            // Only the report's counts depend on it.
        }
        return (files, directories, bytes);
    }

    /// <summary>Whether a provider's leftover is a backup rather than a staging copy.</summary>
    private static bool IsBackupName(string path) => path.Contains("backup", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads an item after a commit, ignoring the caller's cancel; null when it cannot be read.</summary>
    private static async Task<StorageItem?> TryGetInfoAsync(IStorageBackend backend, string path)
    {
        try
        {
            var info = await backend.GetInfoAsync(path, CancellationToken.None).ConfigureAwait(false);
            return info.IsSuccess ? info.Value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Whether an item exists, ignoring the caller's cancel; null when that cannot be told.</summary>
    private static async Task<bool?> TryExistsAsync(IStorageBackend backend, string path)
    {
        try
        {
            var exists = await backend.ExistsAsync(path, CancellationToken.None).ConfigureAwait(false);
            return exists.IsSuccess ? exists.Value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Whether an item is provably gone (its read answers not-found), ignoring the caller's cancel.</summary>
    private static async Task<bool> IsGoneAsync(IStorageBackend backend, string path)
    {
        try
        {
            var info = await backend.GetInfoAsync(path, CancellationToken.None).ConfigureAwait(false);
            return info.IsFailure && info.Error!.Code == StorageErrors.NotFoundCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Folds a coordinator summary into the report.</summary>
    private static StorageTransferReport SummaryReport(StorageTransferReport report, StorageTransferSummary summary, bool move) => report with
    {
        SourceType = summary.SourceType,
        Files = summary.Files,
        Directories = summary.Directories,
        Bytes = summary.Bytes,
        BytesResumed = summary.BytesResumed,
        SkippedFiles = summary.SkippedFiles,
        WrittenPath = summary.WrittenPath ?? (summary.SourceType == StorageItemType.File ? null : report.DestinationPath),
        SkipReason = summary.SkipReason,
        Sha256 = summary.Sha256,
        VerifiedBy = summary.VerifiedBy,
        DestinationETag = summary.Destination?.ETag,
        DestinationVersionId = summary.Destination?.VersionId,
        // A directory transfer that only reused existing folders and skipped every file wrote nothing.
        DestinationCommitted = summary.Files > 0 || summary.CreatedDirectories > 0,
        ConditionEnforcement = summary.ConditionEnforcement,
        StagingLeftBehind = summary.StagingLeftBehind,
        BackupLeftBehind = summary.BackupLeftBehind,
        SourceDeleted = move ? false : null
    };

    /// <summary>Builds the report for a coordinator failure from what it recorded before stopping.</summary>
    private static StorageTransferReport FailedTransferReport(StorageTransferReport report, Error error, TransferState state, bool move)
    {
        // An error raised after the destination committed (for example a destination that does not hold what
        // was committed) is never a clean failure.
        var committed = state.DestinationCommitted || StorageErrorInfo.DestinationCommitted(error);
        var mixed = committed || error.Code == StorageErrors.PartialFailureCode || state.RollbackIncomplete || state.BackupLeftBehind is not null ||
            (state.StagingLeftBehind is not null && !state.StagingResumable);
        StorageResumeToken? token = null;
        if (state.StagingResumable && state.StagingLeftBehind is { } staging && !committed)
        {
            token = new StorageResumeToken
            {
                DestinationPath = report.DestinationPath,
                StagingPath = staging,
                BytesStaged = state.BytesStaged,
                SourcePath = state.Source?.Path,
                SourceETag = state.Source?.ETag,
                SourceVersionId = state.Source?.VersionId,
                SourceLength = state.Source?.Size,
                SourceLastModified = state.Source?.LastModified
            };
        }
        return report with
        {
            Outcome = mixed ? StorageTransferOutcome.NeedsReconciliation
                : error.Code == StorageErrors.CancelledCode ? StorageTransferOutcome.Cancelled
                : StorageTransferOutcome.Failed,
            Error = error,
            SourceType = state.Source?.ItemType,
            DestinationCommitted = committed,
            SourceDeleted = move ? false : null,
            StagingLeftBehind = state.StagingLeftBehind,
            BackupRestored = state.BackupRestored,
            BackupLeftBehind = state.BackupLeftBehind,
            ConditionEnforcement = state.ConditionEnforcement,
            ResumeToken = token
        };
    }

    /// <summary>
    /// Deletes a moved source file only while it is still the version that was copied: atomically where the
    /// source supports conditional deletes, otherwise by comparing its identity immediately before.
    /// </summary>
    private static async Task<Result> DeleteIfUnchangedAsync(
        IStorageBackend source,
        string path,
        StorageItem? copied,
        bool ignoreMissing,
        CancellationToken cancellationToken)
    {
        StorageMutationCondition? condition = null;
        if (copied is not null)
        {
            if (source.Capabilities.Supports(StorageFeature.ConditionalDelete) && (copied.ETag is not null || copied.VersionId is not null))
            {
                condition = new StorageMutationCondition { ExpectedETag = copied.ETag, ExpectedVersionId = copied.VersionId };
            }
            else
            {
                var current = await source.GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
                if (current.IsFailure)
                    return ignoreMissing && current.Error!.Code == StorageErrors.NotFoundCode ? Result.Success() : Result.Failure(current.Error!);
                if (current.Value!.ItemType != copied.ItemType || current.Value.Size != copied.Size || current.Value.LastModified != copied.LastModified ||
                    (copied.VersionId is not null && copied.VersionId != current.Value.VersionId) ||
                    (copied.ETag is not null && !StagedWriter.SameETag(copied.ETag, current.Value.ETag)))
                    return Result.Failure(StorageErrors.Conflict($"The source '{path}' changed after it was copied, so it was not deleted."));
            }
        }
        return await source.DeleteAsync(
            path,
            new StorageDeleteOptions { IgnoreMissing = ignoreMissing, Condition = condition },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Completes a directory move: deletes each transferred source file only while it is still the version
    /// listed for the copy, then removes folders left empty, deepest first, never recursively. Files skipped on
    /// purpose stay, and so does anything that changed or appeared during the copy — which the result reports,
    /// together with whether the source directory is gone.
    /// </summary>
    private static async Task<(bool SourceDeleted, Error? Error)> DeleteMovedDirectoryAsync(
        IStorageBackend source,
        string sourceRoot,
        StorageTransferSummary summary,
        TransferState state,
        CancellationToken cancellationToken)
    {
        var kept = new HashSet<string>(StringComparer.Ordinal);
        string? firstError = null;
        foreach (var item in summary.TransferredItems ?? [])
        {
            var deleted = await DeleteIfUnchangedAsync(source, item.Path, item, ignoreMissing: true, cancellationToken).ConfigureAwait(false);
            if (deleted.IsFailure)
            {
                kept.Add(item.Path);
                firstError ??= deleted.Error!.Code;
            }
            else
            {
                state.SourceItemsDeleted++;
            }
        }

        var skipped = new HashSet<string>(summary.SkippedSources ?? [], StringComparer.Ordinal);
        var directories = new List<string>();
        var appeared = 0;
        await foreach (var entry in source.EnumerateItemsAsync(sourceRoot, new StorageListOptions { Recursive = true }, cancellationToken).ConfigureAwait(false))
        {
            if (entry.IsFailure)
            {
                firstError ??= entry.Error!.Code;
                break;
            }
            var path = StoragePath.Normalize(entry.Value!.Path);
            var normalized = path.IsSuccess ? path.Value! : entry.Value.Path;
            if (entry.Value.ItemType == StorageItemType.Directory)
                directories.Add(normalized);
            else if (!skipped.Contains(normalized) && !kept.Contains(normalized))
                appeared++;
        }
        directories.Add(sourceRoot);
        foreach (var directory in directories.Distinct(StringComparer.Ordinal).OrderByDescending(path => path.Count(c => c == '/')).ThenByDescending(path => path.Length))
        {
            // Non-recursive: a folder that still holds something fails with a conflict and stays.
            _ = await source.DeleteAsync(directory, new StorageDeleteOptions { IgnoreMissing = true }, cancellationToken).ConfigureAwait(false);
        }
        var rootLeft = await source.ExistsAsync(sourceRoot, cancellationToken).ConfigureAwait(false);
        var sourceDeleted = rootLeft.IsSuccess && !rootLeft.Value;
        if (kept.Count == 0 && appeared == 0 && firstError is null)
            return (sourceDeleted, null);
        return (sourceDeleted, StorageErrors.PartialFailure(
            $"The destination completed, but {kept.Count + appeared} source item(s) changed or appeared during the move and were kept at the source.",
            $"sourceChanged={kept.Count};sourceAdded={appeared};sourceDeleteError={firstError};{StorageErrorInfo.DestinationStateKey}=complete"));
    }
}
