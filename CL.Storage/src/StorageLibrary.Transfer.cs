using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;
namespace CL.Storage;

public sealed partial class StorageLibrary
{
    private async Task<StorageTransferReport> TransferAsync(string sourceConnectionId, string sourcePath,
                        string destinationConnectionId, string destinationPath, StorageTransferOptions? options,
                        bool move, CancellationToken cancellationToken)
    {
        ValidateConnectionId(sourceConnectionId);
        ValidateConnectionId(destinationConnectionId);
        options ??= new StorageTransferOptions();
        var report = new StorageTransferReport
        {
            Outcome = StorageTransferOutcome.Failed,
            SourcePath = sourcePath,
            DestinationPath = destinationPath
        };
        StorageTransferReport Fail(Error error) => report with { Outcome = StorageTransferOutcome.Failed, Error = error };
        // A transfer reports what happened rather than throwing; a cancel before it starts is reported too.
        if (cancellationToken.IsCancellationRequested)
            return report with
            {
                Outcome = StorageTransferOutcome.Cancelled,
                Error = StorageErrors.Cancelled("The transfer was cancelled before it started."),
                SourceDeleted = move ? false : null
            };
        var prepared = PrepareTransferPaths(sourcePath, destinationPath, options);
        if (prepared.IsFailure) return Fail(prepared.Error!);
        report = report with { SourcePath = prepared.Value.Source, DestinationPath = prepared.Value.Destination };
        var destinationTarget = prepared.Value.Destination;
        BackendEntry.BackendOperationLease? sourceLease = null;
        BackendEntry.BackendOperationLease? destinationLease = null;
        StorageEventPublisher? publisher = null;
        StorageTransferSummary? summary = null;
        bool sameBackend = false;
        // A move that committed its destination but kept (part of) its source is announced as a copy, so
        // watchers and caches learn about the new destination.
        var announceAsCopy = false;
        string? effectiveSourceId = null;
        string? effectiveDestinationId = null;
        StorageProvider sourceProvider = default;
        StorageProvider destinationProvider = default;
        IStorageBackend? movedSource = null;
        TransferState? state = null;
        try
        {
            try
            {
                sourceLease = AcquireOperation(sourceConnectionId);
                destinationLease = AcquireOperation(destinationConnectionId);
            }
            catch (KeyNotFoundException error)
            {
                return Fail(StorageErrors.NotFound(error.Message));
            }
            var sourceBackend = sourceLease.Backend;
            var destinationBackend = destinationLease.Backend;
            sameBackend = ReferenceEquals(sourceBackend, destinationBackend);
            effectiveSourceId = sourceBackend.ConnectionId;
            effectiveDestinationId = destinationBackend.ConnectionId;
            sourceProvider = sourceBackend.Provider;
            destinationProvider = destinationBackend.Provider;
            var usedNativeOperation = false;
            var perFileDecisions = false;
            // Conditional conflict policies decide per file. A single file is decided here, so the
            // native operation can still run; a directory must relay so every file is decided on its own.
            // Resume is decided by the coordinator, which owns the resumable staging.
            // Rename picks the free name here only for a file the server can copy or move itself (a same-server
            // rename stays a rename); the native call then retries the next free name when the chosen one is taken
            // at commit. Anything that relays leaves Rename to the coordinator, which does the same for its promote.
            var nativeCandidate = sameBackend && !options.RequiresGuarantees &&
                options.MetadataPreservation != StorageMetadataPreservation.Discard;
            string? renamedFrom = null;
            var requestedOptions = options;
            if (options.ConflictPolicy == StorageConflictPolicy.Rename && !nativeCandidate)
            {
                perFileDecisions = true;
            }
            else if (StorageConflictResolver.IsConditional(options.ConflictPolicy) && options.ConflictPolicy != StorageConflictPolicy.Resume)
            {
                // A pinned version is judged by its own size and time, not the latest one's.
                var info = await StorageTransferCoordinator.ResolveSourceAsync(sourceBackend, prepared.Value.Source,
                        options.SourceVersionId, cancellationToken).ConfigureAwait(false);
                if (info.IsFailure)
                    return Fail(info.Error!);
                if (info.Value!.ItemType == StorageItemType.File)
                {
                    var decision = await StorageConflictResolver.ResolveAsync(destinationBackend, destinationTarget,
                        options.ConflictPolicy, options.Overwrite, info.Value.Size, info.Value.LastModified,
                        cancellationToken).ConfigureAwait(false);
                    if (decision.IsFailure)
                        return Fail(decision.Error!);
                    if (decision.Value.Skip)
                    {
                        return report with
                        {
                            Outcome = StorageTransferOutcome.Skipped,
                            SourceType = StorageItemType.File,
                            SkippedFiles = 1,
                            SkipReason = StorageConflictResolver.SkipReasonFor(options.ConflictPolicy!.Value),
                            DestinationETag = decision.Value.Existing?.ETag,
                            DestinationVersionId = decision.Value.Existing?.VersionId,
                            SourceDeleted = move ? false : null
                        };
                    }
                    if (options.ConflictPolicy == StorageConflictPolicy.Rename)
                        renamedFrom = destinationTarget;
                    destinationTarget = decision.Value.Path;
                    options = options with { Overwrite = decision.Value.Overwrite, ConflictPolicy = null };
                }
                else
                {
                    perFileDecisions = true;
                }
            }
            // Guarantees (conditions, pinning, verification, resume) need the staged relay, not a native call, and
            // so does discarding metadata, which a server-side copy would carry.
            if (sameBackend && !perFileDecisions && !options.RequiresGuarantees &&
                options.MetadataPreservation != StorageMetadataPreservation.Discard)
            {
                var relationship = StorageTransferPath.ValidateDistinct(prepared.Value.Source, destinationTarget);
                if (relationship.IsFailure)
                    return Fail(relationship.Error!);
                var sourceInfo = await sourceBackend.GetInfoAsync(prepared.Value.Source, cancellationToken).ConfigureAwait(false);
                if (sourceInfo.IsFailure)
                    return Fail(sourceInfo.Error!);
                if (sourceInfo.Value!.ItemType == StorageItemType.Directory)
                {
                    relationship = StorageTransferPath.ValidateDirectoryDestination(prepared.Value.Source,
                        destinationTarget);
                    if (relationship.IsFailure)
                        return Fail(relationship.Error!);
                }
                var requiredFeatures = (move, sourceInfo.Value.ItemType) switch
                {
                    (false, StorageItemType.File) => StorageFeature.FileCopy | StorageFeature.ServerSideCopy,
                    // Directory copy implementations commonly loop over provider objects and can
                    // leave a partial tree. The coordinator tracks and rolls back every new item.
                    (false, StorageItemType.Directory) => StorageFeature.None,
                    (true, StorageItemType.File) => StorageFeature.FileMove | StorageFeature.ServerSideMove,
                    (true, StorageItemType.Directory) => StorageFeature.DirectoryMove |
                        StorageFeature.ServerSideMove |
                        StorageFeature.AtomicMove,
                    _ => StorageFeature.None
                };
                var supportsNativeOperation = requiredFeatures != StorageFeature.None &&
                    sourceBackend.Capabilities.Supports(requiredFeatures);
                var nativeOptions = options;
                if (supportsNativeOperation && move && sourceInfo.Value.ItemType == StorageItemType.Directory)
                {
                    // A native directory move replaces or refuses an existing destination depending on the server;
                    // onto an existing directory the relay runs instead, which merges.
                    var destinationExists = await destinationBackend.ExistsAsync(destinationTarget, cancellationToken).ConfigureAwait(false);
                    if (destinationExists.IsFailure)
                        return Fail(destinationExists.Error!);
                    supportsNativeOperation = !destinationExists.Value;
                }
                // A move that is not an atomic rename (a copy and a delete) is pinned to the version read here, so a
                // write made in between is neither copied half-way nor deleted. Where the source has no identity to
                // pin, or the connection refuses the pin (WebDAV), the source is compared immediately before the
                // server's own move instead of relaying the bytes through the client, and the report says so.
                var checkSourceBefore = false;
                if (supportsNativeOperation && move && sourceInfo.Value.ItemType == StorageItemType.File &&
                    !sourceBackend.Capabilities.Supports(StorageFeature.AtomicMove))
                {
                    if (sourceInfo.Value.ETag is { } eTag)
                        nativeOptions = options with { ExpectedSourceETag = eTag };
                    else if (sourceInfo.Value.VersionId is { } version)
                        nativeOptions = options with { SourceVersionId = version };
                    else
                        checkSourceBefore = true;
                }
                if (!supportsNativeOperation && renamedFrom is not null)
                {
                    // The server cannot do it: the relay decides the name, and retries the next one at its commit.
                    options = requestedOptions;
                    destinationTarget = renamedFrom;
                    renamedFrom = null;
                }
                if (supportsNativeOperation)
                {
                    if (options.PhaseChanged is { } committing)
                        await committing(Queue.StorageTransferPhase.Committing, cancellationToken).ConfigureAwait(false);
                    // A server-side operation moves no bytes through the client: report its start and end.
                    var total = sourceInfo.Value.ItemType == StorageItemType.File ? sourceInfo.Value.Size : null;
                    options.Progress?.Report(new StorageTransferProgress(0, total, false,
                        ItemPath: prepared.Value.Source));
                    var native = await NativeAsync().ConfigureAwait(false);
                    // Rename: a name taken between the choice and the server's rename is not a failure; the next free
                    // one is used. Only a destination that is really there now counts as taken.
                    for (var retry = 0; renamedFrom is not null && retry < RenameRetries && native.IsFailure &&
                        native.Error!.Code == StorageErrors.ConflictCode && !StorageErrorInfo.DestinationCommitted(native.Error); retry++)
                    {
                        var taken = await destinationBackend.ExistsAsync(destinationTarget, cancellationToken).ConfigureAwait(false);
                        if (taken.IsFailure || !taken.Value)
                            break;
                        var next = await StorageConflictResolver.ResolveAsync(destinationBackend, renamedFrom,
                        StorageConflictPolicy.Rename, false, sourceInfo.Value.Size, sourceInfo.Value.LastModified,
                        cancellationToken).ConfigureAwait(false);
                        if (next.IsFailure || next.Value.Path == destinationTarget)
                            break;
                        destinationTarget = next.Value.Path;
                        native = await NativeAsync().ConfigureAwait(false);
                    }
                    async Task<Result> NativeAsync()
                    {
                        if (checkSourceBefore)
                        {
                            var unchanged = await SourceUnchangedAsync(sourceBackend, prepared.Value.Source,
                        sourceInfo.Value!, cancellationToken).ConfigureAwait(false);
                            if (unchanged.IsFailure)
                                return unchanged;
                        }
                        var result = move
                            ? await sourceBackend.MoveAsync(prepared.Value.Source, destinationTarget, nativeOptions,
                        cancellationToken).ConfigureAwait(false)
                            : await sourceBackend.CopyAsync(prepared.Value.Source, destinationTarget, nativeOptions,
                        cancellationToken).ConfigureAwait(false);
                        if (result.IsFailure && result.Error!.Code == StorageErrors.UnsupportedCode && !ReferenceEquals(nativeOptions,
                        options))
                        {
                            // The connection cannot pin its move to a version: the source is compared immediately
                            // before the server's move instead.
                            checkSourceBefore = true;
                            nativeOptions = options;
                            return await NativeAsync().ConfigureAwait(false);
                        }
                        return result;
                    }
                    var nativeCommitted = native.IsSuccess || StorageErrorInfo.DestinationCommitted(native.Error);
                    if (!nativeCommitted)
                    {
                        // A provider that stopped part-way (a WebDAV 207 multi-status) left a mixed state to reconcile.
                        return native.Error!.Code == StorageErrors.PartialFailureCode
                            ? report with
                            {
                                Outcome = StorageTransferOutcome.NeedsReconciliation,
                                Error = native.Error,
                                SourceDeleted = move ? false : null
                            }
                            : Fail(native.Error!);
                    }
                    else
                    {
                        // Committed. The report is built first and only then completed, with reads that ignore the
                        // caller's cancel, so a cancel now cannot turn a finished copy or move into "Cancelled".
                        usedNativeOperation = true;
                        if (move)
                            movedSource = sourceBackend;
                        // A move whose source delete failed names the source itself; that is not an internal leftover.
                        var leftBehind = StagedWriter.LeftBehind(native.Error)
                            .Where(path => !string.Equals(path, prepared.Value.Source, StringComparison.Ordinal)).ToList();
                        report = report with
                        {
                            Outcome = StorageTransferOutcome.Completed,
                            SourceType = sourceInfo.Value.ItemType,
                            WrittenPath = destinationTarget,
                            Files = sourceInfo.Value.ItemType == StorageItemType.File ? 1 : 0,
                            Directories = sourceInfo.Value.ItemType == StorageItemType.Directory ? 1 : 0,
                            Bytes = sourceInfo.Value.Size ?? 0,
                            DestinationCommitted = true,
                            SourceDeleted = move ? true : null,
                            BackupLeftBehind = leftBehind.FirstOrDefault(IsBackupName),
                            StagingLeftBehind = leftBehind.FirstOrDefault(path => !IsBackupName(path))
                        };
                        publisher = CaptureEventPublisher();
                        var progress = new NativeReportProgress(report, announceAsCopy);
                        try
                        {
                            await CompleteNativeReportAsync(progress, options, move, native, sourceInfo.Value.ItemType,
                                sourceBackend, destinationBackend, destinationTarget, prepared.Value.Source, total, checkSourceBefore).ConfigureAwait(false);
                        }
                        finally { report = progress.Report; announceAsCopy = progress.AnnounceAsCopy; }
                    }
                }
            }
            if (!usedNativeOperation)
            {
                // The relay reads the source and writes the destination at the same time. On one connection
                // limited to a single session that cannot work; it fails now instead of waiting out the
                // session timeout.
                if (sameBackend && StorageTransferPipeline.SessionLimitFor(sourceBackend) is < 2)
                    return Fail(StorageErrors.Unsupported("This transfer relays through the client on one connection, which needs two sessions (one to read, one to write), but the connection allows one. Raise Session.MaxSessions to at least 2.",
                        "requiredSessions=2;maxSessions=1"));
                state = new TransferState();
                var copied = await StorageTransferCoordinator.CopyAsync(sourceBackend, prepared.Value.Source,
                        destinationBackend, destinationTarget, options, cancellationToken, state).ConfigureAwait(false);
                if (copied.IsFailure)
                {
                    report = FailedTransferReport(report, copied.Error!, state, move);
                    if (!report.DestinationCommitted)
                        return report;
                    // Committed but not as it should be: still announced, so watchers see the new destination.
                    announceAsCopy = true;
                    publisher = CaptureEventPublisher();
                    summary = new StorageTransferSummary(report.SourceType ?? StorageItemType.File, state.FilesCommitted,
                        state.DirectoriesCreated, state.BytesCommitted);
                }
                else
                {
                    summary = copied.Value!;
                    report = SummaryReport(report, summary, move);
                    // A resumed move whose destination already holds the content (for example after a crash between
                    // commit and source deletion) still has to delete its source; any other skip leaves it.
                    var alreadyMoved = move && summary.SkipReason == StorageSkipReason.AlreadyComplete;
                    if (summary.SkippedFiles > 0 && summary.Files == 0 && summary.SourceType is StorageItemType.File or StorageItemType.Link && !alreadyMoved)
                        return report with
                        {
                            Outcome = StorageTransferOutcome.Skipped,
                            SourceDeleted = move ? false : null
                        };
                    report = report with
                    {
                        Outcome = StorageTransferOutcome.Completed,
                        SourceDeleted = move ? false : null
                    };
                    // Announced even if deleting the source fails below: the destination is committed.
                    publisher = CaptureEventPublisher();
                    if (move)
                        movedSource = sourceBackend;
                    if (move && options.PhaseChanged is { } deleting)
                        await deleting(Queue.StorageTransferPhase.DeletingSource, cancellationToken).ConfigureAwait(false);
                    if (move && summary.SourceType == StorageItemType.Directory)
                    {
                        var removed = await DeleteMovedDirectoryAsync(sourceBackend, prepared.Value.Source, summary,
                        state, cancellationToken).ConfigureAwait(false);
                        report = report with { SourceDeleted = removed.SourceDeleted };
                        if (removed.Error is not null)
                        {
                            announceAsCopy = true;
                            report = report with
                            {
                                Outcome = StorageTransferOutcome.NeedsReconciliation,
                                Error = removed.Error
                            };
                        }
                    }
                    else if (move)
                    {
                        var deleted = await DeleteIfUnchangedAsync(sourceBackend, prepared.Value.Source, state.Source,
                        ignoreMissing: false, cancellationToken).ConfigureAwait(false);
                        if (deleted.IsFailure)
                        {
                            announceAsCopy = true;
                            report = report with
                            {
                                Outcome = StorageTransferOutcome.NeedsReconciliation,
                                SourceDeleted = false,
                                Error = StorageErrors.PartialFailure("The destination completed, but the source could not be deleted.",
                        $"sourceDeleteError={deleted.Error!.Code};{StorageErrorInfo.DestinationStateKey}=complete")
                            };
                        }
                        else
                        {
                            report = report with { SourceDeleted = true };
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Reported rather than thrown, so the caller learns what the transfer left behind; a destination that
            // was already committed is still announced below.
            (report, publisher, summary, announceAsCopy) = await StoppedTransferReportAsync(report, state, move, movedSource, publisher, summary, announceAsCopy, StorageErrors.Cancelled("The transfer was cancelled.")).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A provider that throws is reported like one that fails.
            (report, publisher, summary, announceAsCopy) = await StoppedTransferReportAsync(report, state, move, movedSource, publisher, summary, announceAsCopy, StorageErrors.FromException(error, "Transfer")).ConfigureAwait(false);
        }
        finally
        {
            destinationLease?.Dispose();
            sourceLease?.Dispose();
        }
        await PublishTransferResultAsync(
            Result.Success(),
            publisher,
            sameBackend,
            move && !announceAsCopy,
            effectiveSourceId!,
            sourceProvider,
            report.SourcePath,
            effectiveDestinationId!,
            destinationProvider,
            report.WrittenPath ?? destinationTarget,
            summary ?? new StorageTransferSummary(report.SourceType ?? StorageItemType.File, report.Files,
                        report.Directories, report.Bytes)).ConfigureAwait(false);
        return report;
    }
}
