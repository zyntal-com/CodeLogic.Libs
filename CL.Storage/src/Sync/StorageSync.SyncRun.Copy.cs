using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

public static partial class StorageSync
{
    private sealed partial class SyncRun
    {
        /// <summary>Copies one file through a staged, conditional (and optionally verified) write, and records the pair.</summary>
        private async Task<Result> CopyAsync(
            IStorageService from,
            string fromPath,
            StorageSyncIdentity? planned,
            IStorageService to,
            string toPath,
            StorageSyncIdentity? plannedTarget,
            bool checkFrom,
            bool towardDestination,
            string relative,
            TimeSpan tolerance,
            CancellationToken cancellationToken)
        {
            var read = await Current(from, fromPath, cancellationToken).ConfigureAwait(false);
            if (read.IsFailure) return Result.Failure(read.Error!);
            var fromItem = read.Value;
            if (fromItem is null || fromItem.ItemType != StorageItemType.File || (checkFrom && planned is not null && !planned.Matches(fromItem, tolerance)))
                return Stale(fromPath);
            var target = await Current(to, toPath, cancellationToken).ConfigureAwait(false);
            if (target.IsFailure) return Result.Failure(target.Error!);
            // What a copy replaces must be exactly the version planned: a same-size edit within the time tolerance
            // is still an edit (FTP listings keep minutes), and it is never overwritten.
            if (plannedTarget is null ? target.Value is not null : !plannedTarget.Matches(target.Value, TimeSpan.Zero))
                return Stale(toPath);

            var modified = StorageCompare.EffectiveModified(fromItem);
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            var keepTimeInMetadata = options.PreserveTimestamps && modified is not null &&
                !to.Capabilities.Supports(StorageFeature.SetTimestamps) && to.Capabilities.Supports(StorageFeature.MetadataWrite);
            if (keepTimeInMetadata)
                metadata[StorageCompareOptions.ModifiedMetadataKey] = modified!.Value.ToString("O", CultureInfo.InvariantCulture);

            var written = await StagedWriter.WriteAsync(
                to,
                new StagedWriteRequest
                {
                    Path = toPath,
                    Upload = new StorageUploadOptions
                    {
                        CreateParents = true,
                        ContentType = fromItem.ContentType,
                        Metadata = metadata,
                        Progress = options.Progress is { } progress ? new PathProgress(progress, fromPath) : null
                    },
                    ExpectedLength = fromItem.Size,
                    Verify = options.Verify
                },
                (offset, token) => from.DownloadAsync(fromPath, new StorageDownloadOptions { Offset = offset, VersionId = fromItem.VersionId }, token),
                cancellationToken).ConfigureAwait(false);
            if (written.Cancelled) cancellationToken.ThrowIfCancellationRequested();
            if (!written.IsSuccess) return Result.Failure(written.Error!);
            var staged = written.Content!;

            async Task<Result> Abandon(Result why)
            {
                await StagedWriter.DeleteAsync(to, staged.StagingPath).ConfigureAwait(false);
                return why;
            }

            // Without a pinned version the source is read again: it must not have changed while it streamed, by its
            // ETag, or by size and time where it has none (SFTP, FTP).
            if (fromItem.VersionId is null)
            {
                var after = await Current(from, fromPath, cancellationToken).ConfigureAwait(false);
                if (after.IsFailure) return await Abandon(Result.Failure(after.Error!)).ConfigureAwait(false);
                if (!StorageSyncIdentity.Of(fromItem)!.Matches(after.Value, TimeSpan.Zero)) return await Abandon(Stale(fromPath)).ConfigureAwait(false);
            }
            // The target must still be what was planned right before it is replaced; the promote checks it again.
            StorageMutationCondition? condition = null;
            if (plannedTarget is not null)
            {
                var again = await Current(to, toPath, cancellationToken).ConfigureAwait(false);
                if (again.IsFailure) return await Abandon(Result.Failure(again.Error!)).ConfigureAwait(false);
                if (!plannedTarget.Matches(again.Value, TimeSpan.Zero)) return await Abandon(Stale(toPath)).ConfigureAwait(false);
                if (plannedTarget.ETag is not null || plannedTarget.VersionId is not null)
                    condition = new StorageMutationCondition { ExpectedETag = plannedTarget.ETag, ExpectedVersionId = plannedTarget.VersionId };
            }
            if (cancellationToken.IsCancellationRequested)
            {
                await Abandon(Result.Success()).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            // From here the destination may be committed: nothing is cancelled half-way, and nothing rolled back.
            var promotion = await StagedWriter.PromoteCoreAsync(to, staged.StagingPath, toPath, overwrite: plannedTarget is not null, condition, createParents: true, CancellationToken.None).ConfigureAwait(false);
            var promoted = promotion.Result;
            if (promoted.IsFailure && !StorageErrorInfo.DestinationCommitted(promoted.Error))
                return await Abandon(promoted.Error!.Code == StorageErrors.ConflictCode ? Stale(toPath) : promoted).ConfigureAwait(false);
            if (promoted.IsSuccess && promotion.LeftBehind.Count > 0)
                // Committed, but the provider left its own backup or staging copy: applied, with the leftover reported.
                promoted = Result.Failure(StorageErrors.PartialFailure(
                    $"'{toPath}' was written, but the provider left internal objects behind.",
                    string.Join(';', [$"{StorageErrorInfo.DestinationStateKey}=complete", .. promotion.LeftBehind.Select(path => $"{StorageErrorInfo.LeftBehindKey}={path}")])));

            var timesSet = false;
            if (options.PreserveTimestamps && modified is not null && to.Capabilities.Supports(StorageFeature.SetTimestamps))
                // Best effort: a failure only means the next comparison relies on size and "source newer".
                timesSet = (await to.SetTimestampsAsync(toPath, modified, cancellationToken: CancellationToken.None).ConfigureAwait(false)).IsSuccess;

            // The pair the baseline records: the version read, and the version written — while it is still ours.
            var total = staged.Bytes + staged.BytesResumed;
            var readIdentity = StorageSyncIdentity.Of(fromItem)! with { Sha256 = staged.Sha256 };
            var planWritten = new StorageSyncIdentity(total, timesSet || keepTimeInMetadata ? modified : null, null, null, staged.Sha256);
            // The version written, read back: a read that keeps failing is retried, then the planned identity is
            // recorded only when it has a time to tell a later edit by (size alone would take a same-size edit for
            // ours); otherwise nothing is recorded, and the next run compares the two sides afresh.
            async Task<StorageSyncIdentity?> ReadBackAsync()
            {
                Result<StorageItem?> result = default!;
                for (var attempt = 1; attempt <= ReadBackAttempts; attempt++)
                {
                    result = await Current(to, toPath, CancellationToken.None).ConfigureAwait(false);
                    if (result.IsSuccess || !StorageErrorInfo.IsTransient(result.Error) || attempt == ReadBackAttempts) break;
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), CancellationToken.None).ConfigureAwait(false);
                }
                if (result.IsFailure) return planWritten.Modified is null ? null : planWritten;
                if (result.Value is { ItemType: not StorageItemType.Directory } item && item.Size == total &&
                    (!(timesSet || keepTimeInMetadata) || StorageCompare.EffectiveModified(item) is not { } now || (now - modified!.Value).Duration() <= tolerance))
                    return StorageSyncIdentity.Of(item);
                return null; // Replaced or removed right after the write: not ours to record.
            }

            StorageSyncIdentity? wrote;
            if (options.Verify)
            {
                var confirmed = await StagedWriter.ConfirmPromotedAsync(to, toPath, staged, CancellationToken.None).ConfigureAwait(false);
                if (confirmed.IsFailure && confirmed.Error!.Code == StorageErrors.ConflictCode)
                    // Committed, and then replaced: the next run sees the other writer's version.
                    // Not reported as committed: the step failed, as the content it wrote is no longer there.
                    return Result.Failure(StorageErrors.Conflict(
                        $"'{toPath}' was written but does not hold that content any more; another writer replaced it.",
                        string.Join(';', (confirmed.Error.Details ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
                            .Where(part => !part.StartsWith(StorageErrorInfo.DestinationStateKey + "=", StringComparison.Ordinal)))));
                wrote = confirmed.IsSuccess ? StorageSyncIdentity.Of(confirmed.Value) : await ReadBackAsync().ConfigureAwait(false);
            }
            else
            {
                wrote = await ReadBackAsync().ConfigureAwait(false);
            }
            if (wrote is not null)
                Synced[relative] = towardDestination ? new StorageSyncBaselineEntry(readIdentity, wrote) : new StorageSyncBaselineEntry(wrote, readIdentity);
            return promoted;
        }

        /// <summary>
        /// Deletes a file while it is exactly the planned version (no time tolerance: a same-size edit within it is
        /// still an edit), or a directory only while it is empty: anything that appeared in it after the plan keeps
        /// it, and the step is reported stale. A path whose type changed since the plan (a file where a folder was, or
        /// the other way round) is stale too.
        /// </summary>
        private static async Task<Result> DeleteAsync(IStorageService storage, string path, StorageSyncIdentity? planned, CancellationToken cancellationToken)
        {
            var read = await Current(storage, path, cancellationToken).ConfigureAwait(false);
            if (read.IsFailure) return Result.Failure(read.Error!);
            if (read.Value is not { } current) return Result.Success();
            var plannedDirectory = planned is null;
            if ((current.ItemType == StorageItemType.Directory) != plannedDirectory) return Stale(path);
            if (plannedDirectory)
            {
                var emptied = await RemoveStaleStagingAsync(storage, path, cancellationToken).ConfigureAwait(false);
                if (emptied.IsFailure) return Result.Failure(emptied.Error!);
                if (!emptied.Value) return Stale(path);
                return await storage.DeleteAsync(path, new StorageDeleteOptions { Recursive = false, IgnoreMissing = true }, cancellationToken).ConfigureAwait(false);
            }
            if (!planned!.Matches(current, TimeSpan.Zero))
                return Stale(path);
            var condition = storage.Capabilities.Supports(StorageFeature.ConditionalDelete) && (planned.ETag is not null || planned.VersionId is not null)
                ? new StorageMutationCondition { ExpectedETag = planned.ETag, ExpectedVersionId = planned.VersionId }
                : null;
            var deleted = await storage.DeleteAsync(path, new StorageDeleteOptions { Recursive = false, IgnoreMissing = true, Condition = condition }, cancellationToken).ConfigureAwait(false);
            return deleted.IsFailure && condition is not null && deleted.Error!.Code == StorageErrors.ConflictCode ? Stale(path) : deleted;
        }

        /// <summary>
        /// Whether a folder holds nothing but the library's own staging left by an interrupted transfer, removing
        /// that staging when it is older than <see cref="StaleStagingAge"/>. A folder holding anything else, a
        /// backup of a replaced file (possibly the only copy of that version), or staging a transfer may still be
        /// writing, is not empty: false.
        /// </summary>
        private static async Task<Result<bool>> RemoveStaleStagingAsync(IStorageService storage, string path, CancellationToken cancellationToken)
        {
            var staging = new List<StorageItem>();
            string? token = null;
            do
            {
                var listed = await storage.ListAsync(path, new StorageListOptions { PageSize = 100, IncludeInternal = true, IncludeHidden = true, ContinuationToken = token }, cancellationToken).ConfigureAwait(false);
                if (listed.IsFailure) return Result<bool>.Failure(listed.Error!);
                foreach (var item in listed.Value!.Items)
                {
                    if (!Providers.StorageListFilter.IsInternal(item.Name) || item.ItemType != StorageItemType.File ||
                        item.Name.Contains("backup", StringComparison.OrdinalIgnoreCase) ||
                        item.LastModified is not { } modified || DateTimeOffset.UtcNow - modified < StaleStagingAge)
                        return Result<bool>.Success(false);
                    staging.Add(item);
                }
                token = listed.Value.ContinuationToken;
            }
            while (token is not null && staging.Count < 1000);
            if (token is not null) return Result<bool>.Success(false);
            foreach (var item in staging)
            {
                var removed = await storage.DeleteAsync(item.Path, new StorageDeleteOptions { Recursive = false, IgnoreMissing = true }, cancellationToken).ConfigureAwait(false);
                if (removed.IsFailure) return Result<bool>.Failure(removed.Error!);
            }
            return Result<bool>.Success(true);
        }

        /// <summary>The item at a path, or null when it is not there; any other failure to read it is a failure.</summary>
        private static async Task<Result<StorageItem?>> Current(IStorageService storage, string path, CancellationToken cancellationToken)
        {
            var info = await storage.GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
            if (info.IsSuccess) return Result<StorageItem?>.Success(info.Value);
            return info.Error!.Code == StorageErrors.NotFoundCode
                ? Result<StorageItem?>.Success(null)
                : Result<StorageItem?>.Failure(info.Error);
        }

        private static Result Stale(string path) =>
            Result.Failure(StorageErrors.Conflict($"'{path}' changed after the plan was made; the step was not taken.", $"{StaleKey}=true"));

        private static bool IsStale(Error? error) => error is not null && StorageErrorInfo.TryGetDetail(error, StaleKey, out _);
    }
}
