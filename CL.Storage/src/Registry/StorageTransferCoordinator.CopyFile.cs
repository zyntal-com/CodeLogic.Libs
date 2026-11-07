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

    private static async Task<Result<StorageTransferSummary>> CopyFileAsync(IStorageBackend source,
                        StorageItem sourceFile, IStorageBackend destination, string destinationPath,
                        StorageTransferOptions options, TransferCleanupTracker cleanup,
                        CancellationToken cancellationToken, TransferState? state = null)
    {
        state ??= new TransferState();
        var aggregate = options.Progress as AggregateProgress;
        var resumable = options.ConflictPolicy == StorageConflictPolicy.Resume || options.ResumeToken is not null;

        // The source must still be the version the caller planned with.
        if (options.ExpectedSourceETag is { } plannedETag && !StagedWriter.SameETag(plannedETag, sourceFile.ETag))
            return Result<StorageTransferSummary>.Failure(StorageErrors.Conflict($"The source '{sourceFile.Path}' changed: its ETag is no longer the expected one.",
                        $"expectedETag={plannedETag};actualETag={sourceFile.ETag}"));
        if (options.ExpectedSourceLength is { } plannedLength && sourceFile.Size is { } actualLength && actualLength != plannedLength)
            return Result<StorageTransferSummary>.Failure(StorageErrors.Conflict($"The source '{sourceFile.Path}' is {actualLength} bytes; {plannedLength} were expected.",
                        $"expectedLength={plannedLength};actualLength={actualLength}"));

        string? renamedFrom = null;
        if (options.ConflictPolicy is { } policy && policy != StorageConflictPolicy.Resume)
        {
            var decision = await StorageConflictResolver.ResolveAsync(destination, destinationPath, policy,
                        options.Overwrite, sourceFile.Size, sourceFile.LastModified, cancellationToken).ConfigureAwait(false);
            if (decision.IsFailure)
                return Result<StorageTransferSummary>.Failure(decision.Error!);
            if (decision.Value.Skip)
            {
                aggregate?.FileDone(sourceFile.Path, 0);
                return Result<StorageTransferSummary>.Success(new StorageTransferSummary(StorageItemType.File, 0, 0, 0,
                        SkippedFiles: 1, TransferredSources: [])
        {
            SkipReason = StorageConflictResolver.SkipReasonFor(policy),
            Destination = decision.Value.Existing,
            SkippedSources = [sourceFile.Path]
        });
    }
            if (policy == StorageConflictPolicy.Rename)
                renamedFrom = destinationPath;
            destinationPath = decision.Value.Path;
            options = options with { Overwrite = decision.Value.Overwrite, ConflictPolicy = null };
        }

        var inspected = await InspectTransferDestinationAsync(destination, destinationPath, cancellationToken).ConfigureAwait(false);
        if (inspected.IsFailure) return Result<StorageTransferSummary>.Failure(inspected.Error!);
        var destinationItem = inspected.Value;
var destinationExists = destinationItem is not null;
        if (resumable && destinationItem is not null && options.ResumeToken is null &&
            destinationItem.Size is { } present && present == sourceFile.Size &&
            await SameContentAsync(source, sourceFile, destination, destinationItem, options.SourceVersionId is not null,
                        cancellationToken).ConfigureAwait(false))
        {
    // Resume: the destination already holds this content (equal digests, not merely an equal size).
    aggregate?.FileDone(sourceFile.Path, 0);
    return Result<StorageTransferSummary>.Success(new StorageTransferSummary(StorageItemType.File, 0, 0, 0,
                SkippedFiles: 1, TransferredSources: [])
    {
        SkipReason = StorageSkipReason.AlreadyComplete,
        Destination = destinationItem,
        TransferredItems = [sourceFile]
    });
}
// Only the Resume policy replaces an existing destination by itself. A resume token continues staged
// bytes but never turns overwriting on: Validate refuses a token with Overwrite off, and a destination
// someone else created meanwhile is then a conflict, not something to replace.
var overwrite = options.Overwrite || options.ConflictPolicy == StorageConflictPolicy.Resume;
if (destinationExists && !overwrite)
    return Result<StorageTransferSummary>.Failure(StorageErrors.Conflict(
        $"The transfer destination '{destinationPath}' already exists."));
if (options.DestinationCondition is { IsEmpty: false } condition)
{
    var check = await StagedWriter.CheckConditionAsync(destination, destinationPath, condition,
                cancellationToken).ConfigureAwait(false);
    if (check.IsFailure)
    {
        state.ConditionEnforcement = StorageConditionEnforcement.CheckedBeforeCommit;
        return Result<StorageTransferSummary>.Failure(check.Error!);
    }
}

var parent = Parent(destinationPath);
if (parent.Length > 0)
{
    var parentResult = await EnsureDirectoryAsync(destination, parent, options.CreateParents, cleanup,
                cancellationToken).ConfigureAwait(false);
    if (parentResult.IsFailure)
        return Result<StorageTransferSummary>.Failure(parentResult.Error!);
}

var metadata = TransferMetadata(sourceFile, destination, options.MetadataPreservation);
if (metadata.IsFailure) return Result<StorageTransferSummary>.Failure(metadata.Error!);
var transferredMetadata = metadata.Value!;

if (options.PhaseChanged is { } transferring)
    await transferring(Queue.StorageTransferPhase.Transferring, cancellationToken).ConfigureAwait(false);

StagedContent staged;
var stagedResumable = false;
PartLease? lease = null;
var canUseNativeStagingCopy = ReferenceEquals(source, destination) &&
    source.Capabilities.Supports(StorageFeature.FileCopy | StorageFeature.ServerSideCopy) &&
    options.MetadataPreservation != StorageMetadataPreservation.Discard &&
    !resumable && !options.Verify && options.ExpectedSha256 is null &&
    options.ExpectedSourceLength is null && options.SourceVersionId is null;
if (canUseNativeStagingCopy)
{
    var allocated = await AllocateStagingPathAsync(destination, parent, cancellationToken).ConfigureAwait(false);
    if (allocated.IsFailure)
        return Result<StorageTransferSummary>.Failure(allocated.Error!);
    var stagingPath = allocated.Value!;
    Result copied;
    try
    {
        copied = await destination.CopyAsync(
            sourceFile.Path,
            stagingPath,
            new StorageTransferOptions
            {
                Overwrite = false,
                CreateParents = options.CreateParents,
                MetadataPreservation = options.MetadataPreservation
            },
            cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        await DeleteStagingAsync(destination, stagingPath).ConfigureAwait(false);
        throw;
    }
    catch (Exception error)
    {
        copied = Result.Failure(StorageErrors.FromException(error, "Copy transfer staging object"));
    }
    if (copied.IsFailure && StorageErrorInfo.DestinationCommitted(copied.Error))
        copied = Result.Success(); // the staging copy exists; only an internal leftover remains
    if (copied.IsFailure)
    {
        var stagingCleanup = await DeleteStagingAsync(destination, stagingPath).ConfigureAwait(false);
        if (stagingCleanup.IsFailure) state.StagingLeftBehind = stagingPath;
        return FailureAfterCleanup(copied.Error!, "The transfer upload failed", stagingCleanup);
    }
    // A server-side copy moves no bytes through the client; report it as one step.
    aggregate?.FileDone(sourceFile.Path, sourceFile.Size ?? 0);
    staged = new StagedContent(stagingPath, sourceFile.Size ?? 0, 0, null, null);
}
else
{
    string? resumeKey = null;
    string? tokenStaging = null;
    // Staged bytes are only reused for a source that can be identified (an ETag, a time, or a version);
    // otherwise a different source of the same length would be appended onto an old prefix. A weak ETag
    // (the local provider's write time, creation time and length) cannot prove the content is unchanged
    // on a coarse file-system clock, so such a source resumes only when Verify re-reads the staged prefix.
    if (resumable && HasIdentity(sourceFile) && (!StorageETags.IsWeak(sourceFile.ETag) || options.Verify))
    {
        resumeKey = StagedWriter.SourceKey(sourceFile.Path, sourceFile.Size, sourceFile.LastModified,
                sourceFile.ETag, sourceFile.VersionId);
        var expectedStaging = StagedWriter.ResumableStagingPath(destinationPath, resumeKey);
        if (options.ResumeToken is { } token)
        {
            if (token.DestinationPath == destinationPath && IsSameSource(token, sourceFile) && token.StagingPath == expectedStaging)
                tokenStaging = token.StagingPath;
            else if (token.StagingPath != expectedStaging && IsOwnPartFile(token, destinationPath) && !await StagedWriter.IsPartInUseAsync(destination,
                token.StagingPath).ConfigureAwait(false))
                // The token's own part file for this destination, staged from a source version that is gone.
                // A token naming any other file is ignored, never followed: it may be another job's.
                await DeleteStagingAsync(destination, token.StagingPath).ConfigureAwait(false);
        }
    }

    var request = new StagedWriteRequest
    {
        Path = destinationPath,
        Upload = new StorageUploadOptions
        {
            CreateParents = options.CreateParents,
            ContentType = sourceFile.ContentType,
            Metadata = transferredMetadata,
            Progress = options.Progress is { } progress ? new PathProgress(progress, sourceFile.Path) : null
        },
        // A resumable write knows the source's length, so a fully staged part file is recognised as
        // complete and a source that grew is not appended onto it.
        ExpectedLength = options.ExpectedSourceLength ?? (resumable ? sourceFile.Size : null),
        Verify = options.Verify,
        ExpectedSha256 = options.ExpectedSha256,
        ResumeKey = tokenStaging is null ? resumeKey : null,
        StagingPath = tokenStaging
    };
    var versionId = options.SourceVersionId;
    var written = await StagedWriter.WriteAsync(
        destination,
        request,
        (offset, token) => source.DownloadAsync(sourceFile.Path,
                new StorageDownloadOptions { Offset = offset, VersionId = versionId }, token),
        cancellationToken).ConfigureAwait(false);
    if (!written.IsSuccess)
    {
        state.StagingLeftBehind = written.StagingLeft;
        // Kept staged bytes are resumable only when they are a part file (never a private staging object).
        state.StagingResumable = written.Resumable && written.StagingLeft is not null && written.CleanupError is null;
        state.BytesStaged = written.BytesStaged;
        return written.CleanupError is { } cleanupError
            ? FailureAfterCleanup(written.Error!, "The transfer upload failed", Result.Failure(cleanupError))
            : Result<StorageTransferSummary>.Failure(written.Error!);
    }
    staged = written.Content!;
    stagedResumable = written.Resumable;
    lease = written.Lease;
}

var committed = false;
var promoteStarted = false;
string? backupPath = null;
var renamedBackup = false;
try
{
    // A source without a pinned version is read again: it must not have changed while it streamed.
    if (options.ExpectedSourceETag is not null && options.SourceVersionId is null)
    {
        var after = await source.GetInfoAsync(sourceFile.Path, cancellationToken).ConfigureAwait(false);
        if (after.IsFailure)
        {
            // Not evidence of a change: the staged bytes stay for a resume.
            var kept = await SettleStagingAsync(destination, staged, stagedResumable, state).ConfigureAwait(false);
            return FailureAfterCleanup(after.Error!, "The transfer could not re-read its source", kept);
        }
        if (!StagedWriter.SameETag(options.ExpectedSourceETag, after.Value!.ETag))
        {
            await DeleteStagingAsync(destination, staged.StagingPath).ConfigureAwait(false);
            return Result<StorageTransferSummary>.Failure(StorageErrors.Conflict($"The source '{sourceFile.Path}' changed while it was being copied.",
                $"expectedETag={options.ExpectedSourceETag};actualETag={after.Value.ETag}"));
        }
    }

    if (lease is not null)
    {
        // The part file is promoted only while this transfer still holds it: one taken over by another
        // transfer (its owner was judged gone) is theirs, and is neither promoted nor removed.
        var owned = await lease.StillOwnedAsync(cancellationToken).ConfigureAwait(false);
        if (owned.IsFailure)
        {
            var kept = await SettleStagingAsync(destination, staged, stagedResumable, state).ConfigureAwait(false);
            return FailureAfterCleanup(owned.Error!,
                "The transfer could not confirm its hold on the staged data", kept);
        }
        if (!owned.Value)
            return Result<StorageTransferSummary>.Failure(StorageErrors.Conflict($"The staged data for '{destinationPath}' was taken over by another transfer, so it was not committed.",
                $"stagingPath={staged.StagingPath}"));
    }

    if (options.PhaseChanged is { } committing)
        await committing(Queue.StorageTransferPhase.Committing, cancellationToken).ConfigureAwait(false);

    if (destinationExists)
    {
        // The previous destination is kept until the new one is committed. Where the server copies, the
        // backup costs no transfer; on FTP and SFTP (no server-side copy, but a replace that keeps the old
        // file until the new one is in place) a single file needs none, and a directory transfer (which may
        // roll back) renames it aside instead of copying it through the client. Any other connection
        // without a server-side copy renames it aside too: its replace is not known to restore anything.
        var serverCopy = destination.Capabilities.Supports(StorageFeature.FileCopy | StorageFeature.ServerSideCopy);
        var restoringReplace = destination is Providers.IStorageRestoringReplace;
        var renameAside = !serverCopy && (cleanup.ForDirectory || !restoringReplace) && destination.Capabilities.Supports(StorageFeature.FileMove);
        if (serverCopy || renameAside)
        {
            var allocatedBackup = await AllocateStagingPathAsync(destination, parent, cancellationToken,
                ".cl-storage-transfer-backup-").ConfigureAwait(false);
            if (allocatedBackup.IsFailure)
            {
                var stagingCleanup = await SettleStagingAsync(destination, staged, stagedResumable, state).ConfigureAwait(false);
                return FailureAfterCleanup(allocatedBackup.Error!,
                "The transfer could not allocate a replacement backup", stagingCleanup);
            }
            backupPath = allocatedBackup.Value!;
            renamedBackup = renameAside;
            var backupOptions = new StorageTransferOptions { Overwrite = false, CreateParents = false };
            var backedUp = renameAside
                ? await destination.MoveAsync(destinationPath, backupPath, backupOptions, cancellationToken).ConfigureAwait(false)
                : await destination.CopyAsync(destinationPath, backupPath, backupOptions, cancellationToken).ConfigureAwait(false);
            if (backedUp.IsFailure && StorageErrorInfo.DestinationCommitted(backedUp.Error))
                backedUp = Result.Success();
            if (backedUp.IsFailure)
            {
                var stagingCleanup = await SettleStagingAsync(destination, staged, stagedResumable, state).ConfigureAwait(false);
                var backupCleanup = renameAside
                    ? await RecoverReplacementAsync(destination, destinationPath, backupPath, renamed: true).ConfigureAwait(false)
                    : await DeleteStagingAsync(destination, backupPath).ConfigureAwait(false);
                if (backupCleanup.IsFailure) state.BackupLeftBehind = backupPath;
                return FailureAfterCleanup(backedUp.Error!,
                "The transfer could not back up the previous destination", stagingCleanup, backupCleanup);
            }
        }
    }

    promoteStarted = true;
    var outcome = await StagedWriter.PromoteCoreAsync(destination, staged.StagingPath, destinationPath,
                overwrite && !renamedBackup, options.DestinationCondition, options.CreateParents,
                cancellationToken).ConfigureAwait(false);
    // Rename: a name taken between the choice and the commit is not a failure; the next free one is used.
    for (var retry = 0; renamedFrom is not null && retry < RenameRetries &&
        outcome.Result.IsFailure && outcome.Result.Error!.Code == StorageErrors.ConflictCode; retry++)
    {
        var next = await StorageConflictResolver.ResolveAsync(destination, renamedFrom,
                StorageConflictPolicy.Rename, false, sourceFile.Size, sourceFile.LastModified, cancellationToken).ConfigureAwait(false);
        if (next.IsFailure || next.Value.Path == destinationPath)
            break;
        destinationPath = next.Value.Path;
        outcome = await StagedWriter.PromoteCoreAsync(destination, staged.StagingPath, destinationPath, false,
                null, options.CreateParents, cancellationToken).ConfigureAwait(false);
    }
    state.ConditionEnforcement = outcome.Enforcement;
    if (outcome.Result.IsFailure)
    {
        var stagingCleanup = await SettleStagingAsync(destination, staged, stagedResumable, state).ConfigureAwait(false);
        if (backupPath is not null)
        {
            if (!outcome.Touched && !renamedBackup)
            {
                // Refused before anything was moved (a failed condition): the destination is exactly as the
                // refusal found it, even if someone else deleted it, so the backup is only dropped.
                var dropped = await DeleteStagingAsync(destination, backupPath).ConfigureAwait(false);
                if (dropped.IsFailure) state.BackupLeftBehind = backupPath;
                return FailureAfterCleanup(outcome.Result.Error!, "The transfer commit failed", stagingCleanup,
                dropped);
            }
            var restored = await RecoverReplacementAsync(destination, destinationPath, backupPath, renamedBackup).ConfigureAwait(false);
            state.BackupRestored = restored.IsSuccess;
            if (restored.IsFailure) state.BackupLeftBehind = backupPath;
            return FailureAfterCleanup(outcome.Result.Error!, "The transfer commit failed", stagingCleanup,
                restored);
        }
        return FailureAfterCleanup(outcome.Result.Error!, "The transfer commit failed", stagingCleanup);
    }

    committed = true;
    state.DestinationCommitted = true;
    state.FilesCommitted = 1;
    state.BytesCommitted = staged.Bytes;
    // What the provider could not remove after it committed: our staging object or its own backup.
    var stagingLeft = outcome.LeftBehind.FirstOrDefault(path => path == staged.StagingPath);
    var providerLeft = outcome.LeftBehind.FirstOrDefault(path => path != staged.StagingPath);
    // Committed: the result is reported even if the caller cancels now, and nothing after this point rolls
    // the destination back. A verified copy also confirms the promoted destination, not only the staging
    // object; a destination that does not hold what was committed always fails (it may be another
    // writer's content, so it is reported and left as it is).
    var confirmed = await StagedWriter.ConfirmPromotedAsync(destination, destinationPath, staged,
                CancellationToken.None).ConfigureAwait(false);
    if (confirmed.IsFailure && confirmed.Error!.Code == StorageErrors.ConflictCode)
    {
        // The mismatch may be a concurrent writer or a broken promote: either way the previous version is
        // kept, never deleted and never put back, and the report names it.
        if (backupPath is not null)
            state.BackupLeftBehind = backupPath;
        state.BackupLeftBehind ??= providerLeft;
        state.StagingLeftBehind = stagingLeft;
        return Result<StorageTransferSummary>.Failure(confirmed.Error);
    }
    if (backupPath is not null)
        cleanup.TrackReplacement(destinationPath, backupPath, renamedBackup,
                confirmed.IsSuccess ? confirmed.Value : null);
    else if (!destinationExists)
        cleanup.TrackFile(destinationPath, confirmed.IsSuccess ? confirmed.Value : null);
    return CompletedFileSummary(sourceFile, staged, destinationPath, confirmed, outcome.Enforcement, stagingLeft, providerLeft);
            }
        catch (Exception error) when(!committed)
        {
                // Nothing was committed: the staging object goes (or stays, to resume from), and a replaced
                // destination keeps its content. A backup is copied back only when the promote may have removed
                // the destination; before that, it is only dropped.
                await SettleStagingAsync(destination, staged, stagedResumable, state).ConfigureAwait(false);
                if (backupPath is not null)
                {
                    var settled = promoteStarted || renamedBackup
                        ? await RecoverReplacementAsync(destination, destinationPath, backupPath, renamedBackup).ConfigureAwait(false)
                        : await DeleteStagingAsync(destination, backupPath).ConfigureAwait(false);
                    if (settled.IsFailure) state.BackupLeftBehind = backupPath;
                }
                return Result<StorageTransferSummary>.Failure(error is OperationCanceledException && cancellationToken.IsCancellationRequested
                    ? StorageErrors.Cancelled("The transfer was cancelled.")
                    : StorageErrors.FromException(error, "Commit transfer"));
            }
        finally
        {
                // Held until the part file was promoted or settled, so nobody appended to it in between.
                if (lease is not null)
                    await lease.ReleaseAsync().ConfigureAwait(false);
            }
        }
    }
