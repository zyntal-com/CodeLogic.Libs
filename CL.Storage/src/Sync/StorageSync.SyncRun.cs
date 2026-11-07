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

    // ---------------------------------------------------------------- applying

    /// <summary>Runs a plan's steps and records their outcomes, and what each applied step left on both sides.</summary>
    private sealed partial class SyncRun(IStorageService source, IStorageService destination, StorageSyncPlan plan, StorageSyncOptions options)
    {
        private readonly StorageSyncActionResult[] _results = [.. plan.Actions.Select(action => new StorageSyncActionResult(action,
            action.WithheldReason is not null ? StorageSyncActionOutcome.Withheld : StorageSyncActionOutcome.NotRun))];
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _renamed = new(StringComparer.Ordinal);

        public IReadOnlyList<StorageSyncActionResult> Results => _results;

        /// <summary>Paths an applied step brought into sync, with the versions it wrote or created.</summary>
        public System.Collections.Concurrent.ConcurrentDictionary<string, StorageSyncBaselineEntry> Synced { get; } = new(StringComparer.Ordinal);

        /// <summary>Paths an applied step deleted.</summary>
        public System.Collections.Concurrent.ConcurrentDictionary<string, bool> Removed { get; } = new(StringComparer.Ordinal);

        public async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var indexes = Enumerable.Range(0, plan.Actions.Count).ToList();
            // Only for recognising the version a source had; whatever a step deletes, overwrites, or renames must
            // be exactly the version planned (see DeleteAsync, CopyAsync, RenameAsync).
            var tolerance = options.Compare.TimeTolerance;

            // Directories and renames run in order; a rename must land before the copies that follow it.
            foreach (var i in indexes.Where(i => plan.Actions[i].Kind is StorageSyncActionKind.CreateDirectory or StorageSyncActionKind.CreateDirectoryAtSource or StorageSyncActionKind.RenameAtDestination))
            {
                if (stop.IsCancellationRequested) break;
                await RunAsync(i, stop, tolerance).ConfigureAwait(false);
            }
            await Parallel.ForEachAsync(
                indexes.Where(i => plan.Actions[i].Kind is StorageSyncActionKind.CopyToDestination or StorageSyncActionKind.CopyToSource),
                new ParallelOptions { MaxDegreeOfParallelism = options.MaxConcurrency },
                async (i, _) =>
                {
                    if (!stop.IsCancellationRequested) await RunAsync(i, stop, tolerance).ConfigureAwait(false);
                }).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // A failed step means the two sides do not match yet, so deleting could remove the only good copy.
            var failed = _results.Count(result => result.Outcome is StorageSyncActionOutcome.Failed or StorageSyncActionOutcome.Stale);
            foreach (var i in indexes.Where(i => plan.Actions[i].Kind is StorageSyncActionKind.DeleteFromDestination or StorageSyncActionKind.DeleteFromSource))
            {
                if (stop.IsCancellationRequested) break;
                if (_results[i].Outcome == StorageSyncActionOutcome.Withheld) continue;
                if (failed > 0)
                {
                    _results[i] = _results[i] with
                    {
                        Outcome = StorageSyncActionOutcome.Withheld,
                        Action = plan.Actions[i] with { WithheldReason = $"{failed} other step(s) failed or found their item changed in this run" }
                    };
                    continue;
                }
                await RunAsync(i, stop, tolerance).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        private async Task RunAsync(int index, CancellationTokenSource stop, TimeSpan tolerance)
        {
            var action = plan.Actions[index];
            var attempts = 0;
            Result result;
            try
            {
                while (true)
                {
                    attempts++;
                    result = await StepAsync(action, tolerance, stop.Token).ConfigureAwait(false);
                    if (result.IsSuccess || StorageErrorInfo.DestinationCommitted(result.Error) || !StorageErrorInfo.IsTransient(result.Error) ||
                        attempts > options.ItemRetries || stop.IsCancellationRequested)
                        break;
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempts - 1)), stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Stopped by the caller, or by another step failing with ContinueOnError off: this step stays NotRun.
                return;
            }
            catch (Exception error)
            {
                // A provider or store that throws fails this step only; the run, its report, and the baseline go on.
                result = Result.Failure(StorageErrors.FromException(error, $"Sync '{action.RelativePath}'"));
            }
            var committed = StorageErrorInfo.DestinationCommitted(result.Error);
            // A step that ended because the run was stopping did not fail on its own: it stays NotRun. One that
            // failed for another reason meanwhile is reported as it ended.
            if (result.IsFailure && !committed && stop.IsCancellationRequested && result.Error!.Code == StorageErrors.CancelledCode) return;
            var outcome = result.IsSuccess || committed
                ? StorageSyncActionOutcome.Applied
                : IsStale(result.Error) ? StorageSyncActionOutcome.Stale : StorageSyncActionOutcome.Failed;
            _results[index] = new StorageSyncActionResult(action, outcome, result.Error, attempts);
            if (outcome != StorageSyncActionOutcome.Applied && !options.ContinueOnError)
                await stop.CancelAsync().ConfigureAwait(false);
        }

        private string SourcePath(string relative) => Join(plan.SourceRoot, relative);
        private static string? ReadPath(StorageSyncAction action) => action.ReadPath is { } read ? StoragePath.Normalize(read).Value : null;
        private string DestinationPath(StorageSyncAction action) => Join(plan.DestinationRoot, action.DestinationRelativePath ?? action.RelativePath);
        private string DestinationPath(string relative) => Join(plan.DestinationRoot, relative);

        private async Task<Result> StepAsync(StorageSyncAction action, TimeSpan tolerance, CancellationToken cancellationToken)
        {
            switch (action.Kind)
            {
                case StorageSyncActionKind.CreateDirectory:
                {
                    var created = await destination.CreateDirectoryAsync(DestinationPath(action), cancellationToken).ConfigureAwait(false);
                    if (created.IsSuccess && action.RelativePath.Length > 0) Synced[action.RelativePath] = new StorageSyncBaselineEntry(null, null, IsDirectory: true);
                    return created;
                }
                case StorageSyncActionKind.CreateDirectoryAtSource:
                {
                    var created = await source.CreateDirectoryAsync(SourcePath(action.RelativePath), cancellationToken).ConfigureAwait(false);
                    if (created.IsSuccess) Synced[action.RelativePath] = new StorageSyncBaselineEntry(null, null, IsDirectory: true);
                    return created;
                }
                case StorageSyncActionKind.RenameAtDestination:
                    return await RenameAsync(action, cancellationToken).ConfigureAwait(false);
                case StorageSyncActionKind.CopyToDestination:
                    return await CopyAsync(source, ReadPath(action) ?? SourcePath(action.RelativePath), action.Source, destination, DestinationPath(action), action.Destination,
                        checkFrom: true, towardDestination: true, action.RelativePath, tolerance, cancellationToken).ConfigureAwait(false);
                case StorageSyncActionKind.CopyToSource:
                {
                    // A kept conflict copy was renamed in this run (possibly to a numbered name), which may also give
                    // it a new identity on object stores.
                    var chained = action.Conflict is not null && action.TargetPath is not null;
                    var relative = chained ? _renamed.GetValueOrDefault(action.RelativePath, action.RelativePath) : action.RelativePath;
                    var from = chained ? DestinationPath(Sibling(action.DestinationRelativePath ?? action.RelativePath, relative)) : ReadPath(action) ?? DestinationPath(action);
                    return await CopyAsync(destination, from, action.Destination, source, SourcePath(relative), chained ? null : action.Source,
                        checkFrom: !chained, towardDestination: false, relative, tolerance, cancellationToken).ConfigureAwait(false);
                }
                case StorageSyncActionKind.DeleteFromDestination:
                {
                    var deleted = await DeleteAsync(destination, DestinationPath(action), action.Destination, cancellationToken).ConfigureAwait(false);
                    if (deleted.IsSuccess) Removed[action.RelativePath] = true;
                    return deleted;
                }
                case StorageSyncActionKind.DeleteFromSource:
                {
                    var deleted = await DeleteAsync(source, SourcePath(action.RelativePath), action.Source, cancellationToken).ConfigureAwait(false);
                    if (deleted.IsSuccess) Removed[action.RelativePath] = true;
                    return deleted;
                }
                default:
                    return Result.Success();
            }
        }

        /// <summary>
        /// Moves the destination's version aside as a kept conflict copy. The planned name is taken when it is free
        /// on both sides; otherwise a numbered one, so an old conflict copy never blocks the run. Only the version
        /// that was planned is moved: the move is pinned to its ETag and version, and where the connection cannot
        /// pin a move, the version is copied (pinned) and then deleted under the same condition.
        /// </summary>
        private async Task<Result> RenameAsync(StorageSyncAction action, CancellationToken cancellationToken)
        {
            var current = await Current(destination, DestinationPath(action), cancellationToken).ConfigureAwait(false);
            if (current.IsFailure) return Result.Failure(current.Error!);
            if (!action.Destination!.Matches(current.Value, TimeSpan.Zero)) return Stale(action.RelativePath);
            var there = action.DestinationRelativePath ?? action.RelativePath;
            for (var attempt = 1; attempt <= 20; attempt++)
            {
                var target = attempt == 1 ? action.TargetPath! : ConflictPath(action.RelativePath, action.Destination, attempt);
                var atDestination = await Current(destination, DestinationPath(Sibling(there, target)), cancellationToken).ConfigureAwait(false);
                if (atDestination.IsFailure) return Result.Failure(atDestination.Error!);
                var atSource = await Current(source, SourcePath(target), cancellationToken).ConfigureAwait(false);
                if (atSource.IsFailure) return Result.Failure(atSource.Error!);
                if (atDestination.Value is not null || atSource.Value is not null) continue;
                var moved = await MoveVersionAsync(DestinationPath(action), DestinationPath(Sibling(there, target)), current.Value!, cancellationToken).ConfigureAwait(false);
                if (moved.IsSuccess || StorageErrorInfo.DestinationCommitted(moved.Error)) _renamed[action.TargetPath!] = target;
                return moved;
            }
            return Result.Failure(StorageErrors.Conflict($"No free name was found for the conflict copy of '{action.RelativePath}'."));
        }

        private async Task<Result> MoveVersionAsync(string fromPath, string toPath, StorageItem version, CancellationToken cancellationToken)
        {
            var pinned = version.ETag is not null || version.VersionId is not null;
            var moved = await destination.MoveAsync(fromPath, toPath, new StorageTransferOptions
            {
                Overwrite = false,
                ExpectedSourceETag = version.ETag,
                SourceVersionId = version.VersionId
            }, cancellationToken).ConfigureAwait(false);
            if (moved.IsFailure && moved.Error!.Code == StorageErrors.UnsupportedCode && pinned)
                return await CopyThenDeleteAsync(fromPath, toPath, version, cancellationToken).ConfigureAwait(false);
            if (moved.IsFailure && moved.Error!.Code == StorageErrors.ConflictCode) return Stale(fromPath);
            return moved;
        }

        /// <summary>
        /// A move for connections that cannot pin one: the planned version is copied through staging (read pinned,
        /// and checked unchanged after it streamed), created only where nothing is, and the original is then
        /// deleted only while it is still that version.
        /// </summary>
        private async Task<Result> CopyThenDeleteAsync(string fromPath, string toPath, StorageItem version, CancellationToken cancellationToken)
        {
            var written = await StagedWriter.WriteAsync(
                destination,
                new StagedWriteRequest
                {
                    Path = toPath,
                    Upload = new StorageUploadOptions { CreateParents = true, ContentType = version.ContentType, Metadata = version.Metadata },
                    ExpectedLength = version.Size
                },
                (offset, token) => destination.DownloadAsync(fromPath, new StorageDownloadOptions { Offset = offset, VersionId = version.VersionId }, token),
                cancellationToken).ConfigureAwait(false);
            if (written.Cancelled) cancellationToken.ThrowIfCancellationRequested();
            if (!written.IsSuccess) return Result.Failure(written.Error!);
            var identity = StorageSyncIdentity.Of(version)!;
            var after = await Current(destination, fromPath, cancellationToken).ConfigureAwait(false);
            if (after.IsFailure || !identity.Matches(after.Value, TimeSpan.Zero))
            {
                await StagedWriter.DeleteAsync(destination, written.Content!.StagingPath).ConfigureAwait(false);
                return after.IsFailure ? Result.Failure(after.Error!) : Stale(fromPath);
            }
            var (promoted, _) = await StagedWriter.PromoteAsync(destination, written.Content!.StagingPath, toPath, overwrite: false, condition: null, createParents: true, CancellationToken.None).ConfigureAwait(false);
            if (promoted.IsFailure && !StorageErrorInfo.DestinationCommitted(promoted.Error))
            {
                await StagedWriter.DeleteAsync(destination, written.Content.StagingPath).ConfigureAwait(false);
                return promoted.Error!.Code == StorageErrors.ConflictCode ? Stale(toPath) : promoted;
            }
            var removed = await DeleteAsync(destination, fromPath, identity, CancellationToken.None).ConfigureAwait(false);
            if (removed.IsFailure)
                return Result.Failure(StorageErrors.Conflict(
                    $"The conflict copy '{toPath}' was made, but '{fromPath}' changed or could not be removed, so it was kept: {removed.Error!.Message}",
                    $"{StaleKey}=true"));
            return promoted;
        }

    }
}
