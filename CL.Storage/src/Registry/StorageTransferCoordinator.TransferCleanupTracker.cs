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

    /// <summary>
    /// Records what a transfer created or replaced so a failed directory transfer can be undone. Each committed
    /// file carries the identity it had when it was committed; rolling back touches it only while it still has
    /// that identity, so a write someone else made after the commit is never removed or overwritten.
    /// </summary>
    private sealed class TransferCleanupTracker(IStorageBackend destination, bool forDirectory)
    {
        private readonly List<(string Path, StorageItem? Committed)> _createdFiles = [];
        private readonly List<string> _createdDirectories = [];
        private readonly List<(string DestinationPath, string BackupPath, bool Renamed, StorageItem? Committed)> _replacements = [];

        /// <summary>Whether this is a directory transfer, which may roll back files it already committed.</summary>
        internal bool ForDirectory => forDirectory;
        internal bool HadReplacements { get; private set; }
        internal string? RetainedBackup { get; private set; }
        internal long CreatedDirectories => _createdDirectories.Count;

        internal void TrackFile(string path, StorageItem? committed) => _createdFiles.Add((path, committed));
        internal void TrackDirectory(string path) => _createdDirectories.Add(path);
        internal void TrackReplacement(string destinationPath, string backupPath, bool renamed, StorageItem? committed)
        {
            HadReplacements = true;
            _replacements.Add((destinationPath, backupPath, renamed, committed));
        }

        /// <summary>Removes the backups of a committed transfer; returns a backup that could not be removed.</summary>
        internal async Task<string?> CommitAsync()
        {
            string? retained = null;
            foreach (var replacement in _replacements)
            {
                Result deleted;
                try
                {
                    deleted = await destination.DeleteAsync(
                        replacement.BackupPath,
                        new StorageDeleteOptions { IgnoreMissing = true },
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    deleted = Result.Failure(StorageErrors.FromException(error, "Delete transfer backup"));
                }
                if (deleted.IsFailure)
                    retained ??= replacement.BackupPath;
            }
            _replacements.Clear();
            RetainedBackup = retained;
            return retained;
        }

        /// <summary>
        /// Undoes the transfer. <paramref name="keep"/> is a resumable part file kept for a token: the folders
        /// holding it stay, or the rollback would fail on them.
        /// </summary>
        internal async Task<Result> RollbackAsync(string? keep = null)
        {
            var fileCleanupErrors = new List<string>();
            var restoreErrors = new List<string>();
            var directoryCleanupErrors = new List<string>();
            foreach (var (path, committed) in _createdFiles.AsEnumerable().Reverse())
            {
                var deleted = await DeleteIfStillOursAsync(path, committed).ConfigureAwait(false);
                if (deleted.IsFailure)
                    fileCleanupErrors.Add(deleted.Error!.Code);
            }
            foreach (var replacement in _replacements.AsEnumerable().Reverse())
            {
                var restored = await RestoreIfStillOursAsync(replacement.DestinationPath, replacement.BackupPath, replacement.Renamed, replacement.Committed).ConfigureAwait(false);
                if (restored.IsFailure)
                {
                    restoreErrors.Add(restored.Error!.Code);
                    RetainedBackup ??= replacement.BackupPath;
                }
            }
            foreach (var path in _createdDirectories.AsEnumerable().Reverse())
            {
                if (keep is not null && keep.StartsWith(path + "/", StringComparison.Ordinal))
                    continue;
                var deleted = await DeleteAsync(path).ConfigureAwait(false);
                if (deleted.IsFailure)
                    directoryCleanupErrors.Add(deleted.Error!.Code);
            }

            return fileCleanupErrors.Count == 0 && restoreErrors.Count == 0 && directoryCleanupErrors.Count == 0
                ? Result.Success()
                : Result.Failure(StorageErrors.PartialFailure(
                    "One or more destination rollback operations failed.",
                    $"fileCleanupErrors={string.Join(',', fileCleanupErrors)};" +
                    $"restoreErrors={string.Join(',', restoreErrors)};" +
                    $"directoryCleanupErrors={string.Join(',', directoryCleanupErrors)}"));
        }

        /// <summary>
        /// Reads the destination and says whether it is still what this transfer committed. A committed identity
        /// that is not known (the read after the commit failed) is not taken as ours: such a file is left and
        /// reported rather than undone on a guess. The current item is null when it is gone.
        /// </summary>
        private async Task<Result<(bool Ours, StorageItem? Current)>> StillOursAsync(string path, StorageItem? committed)
        {
            Result<StorageItem> current;
            try { current = await destination.GetInfoAsync(path, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) { return Result<(bool, StorageItem?)>.Failure(StorageErrors.FromException(error, "Rollback transfer destination")); }
            if (current.IsFailure)
                return current.Error!.Code == StorageErrors.NotFoundCode
                    ? Result<(bool, StorageItem?)>.Success((false, null))
                    : Result<(bool, StorageItem?)>.Failure(current.Error);
            return Result<(bool, StorageItem?)>.Success((committed is not null && SameVersion(committed, current.Value!), current.Value));
        }

        /// <summary>
        /// The committed version as a condition the provider can enforce in the same request, or null when it
        /// cannot (the destination was then compared immediately before).
        /// </summary>
        private static StorageMutationCondition? ConditionFor(StorageItem committed) =>
            committed.ETag is not null || committed.VersionId is not null
                ? new StorageMutationCondition { ExpectedETag = committed.ETag, ExpectedVersionId = committed.VersionId }
                : null;

        private async Task<Result> DeleteIfStillOursAsync(string path, StorageItem? committed)
        {
            var ours = await StillOursAsync(path, committed).ConfigureAwait(false);
            if (ours.IsFailure)
                return Result.Failure(ours.Error!);
            if (ours.Value.Current is null)
                return Result.Success();
            if (!ours.Value.Ours)
                // Replaced by someone else after the commit, or not known to be ours: it is left in place.
                return Result.Failure(StorageErrors.Conflict($"The destination '{path}' changed after it was committed, or its committed version is unknown, so it was not rolled back."));
            // Deleted only while it is still that version: in the same request where the server can enforce it.
            var condition = destination.Capabilities.Supports(StorageFeature.ConditionalDelete) ? ConditionFor(committed!) : null;
            return await DeleteAsync(path, condition).ConfigureAwait(false);
        }

        private async Task<Result> RestoreIfStillOursAsync(string destinationPath, string backupPath, bool renamed, StorageItem? committed)
        {
            var ours = await StillOursAsync(destinationPath, committed).ConfigureAwait(false);
            if (ours.IsFailure)
                return Result.Failure(ours.Error!);
            if (!ours.Value.Ours)
                // Changed or deleted by someone else after the commit (or not known to be ours): that is not undone,
                // and the previous version stays in the backup, which is reported.
                return Result.Failure(StorageErrors.Conflict(
                    $"The destination '{destinationPath}' changed after it was committed, so the previous version was not restored over it."));
            try
            {
                // The previous version replaces the committed one only while it is still that version: the
                // provider enforces it in the same request where it can; otherwise it was compared just before.
                var options = new StorageTransferOptions { Overwrite = true, CreateParents = false, DestinationCondition = ConditionFor(committed!) };
                var restored = await PutBackAsync(options).ConfigureAwait(false);
                if (restored.IsFailure && options.DestinationCondition is not null && restored.Error!.Code == StorageErrors.UnsupportedCode)
                    restored = await PutBackAsync(options with { DestinationCondition = null }).ConfigureAwait(false);
                if (restored.IsFailure && !StorageErrorInfo.DestinationCommitted(restored.Error))
                    return restored;
                if (renamed)
                    return Result.Success();
                return await destination.DeleteAsync(
                    backupPath,
                    new StorageDeleteOptions { IgnoreMissing = true },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                return Result.Failure(StorageErrors.FromException(error, "Restore transfer destination"));
            }

            Task<Result> PutBackAsync(StorageTransferOptions options) => renamed
                ? destination.MoveAsync(backupPath, destinationPath, options, CancellationToken.None)
                : destination.CopyAsync(backupPath, destinationPath, options, CancellationToken.None);
        }

        private async Task<Result> DeleteAsync(string path, StorageMutationCondition? condition = null)
        {
            try
            {
                return await destination.DeleteAsync(
                    path,
                    new StorageDeleteOptions { IgnoreMissing = true, Condition = condition },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                return Result.Failure(StorageErrors.FromException(error, "Rollback transfer destination"));
            }
        }
    }
}
