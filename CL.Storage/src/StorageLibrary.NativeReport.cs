using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;

namespace CL.Storage;

public sealed partial class StorageLibrary
{
    private sealed class NativeReportProgress(StorageTransferReport report, bool announceAsCopy)
    {
        internal StorageTransferReport Report { get; set; } = report;
        internal bool AnnounceAsCopy { get; set; } = announceAsCopy;
    }

    private static async Task CompleteNativeReportAsync(NativeReportProgress progress, StorageTransferOptions options,
        bool move, Result native, StorageItemType sourceType, IStorageBackend sourceBackend, IStorageBackend destinationBackend,
        string destinationTarget, string sourcePath, long? total, bool checkSourceBefore)
    {
        options.Progress?.Report(new StorageTransferProgress(total ?? 0, total ?? 0, true,
    ItemPath: sourcePath));
        if (move && native.IsFailure)
        {
            // The provider committed the destination but reported a failure after it: whether the
            // source is still there decides between a finished move and one to reconcile.
            var sourceLeft = await TryExistsAsync(sourceBackend, sourcePath).ConfigureAwait(false);
            if (sourceLeft != false)
            {
                progress.AnnounceAsCopy = true;
                progress.Report = progress.Report with
                {
                    Outcome = StorageTransferOutcome.NeedsReconciliation,
                    SourceDeleted = false,
                    Error = native.Error
                };
            }
        }
        if (sourceType == StorageItemType.File)
        {
            var committed = await TryGetInfoAsync(destinationBackend, destinationTarget).ConfigureAwait(false);
            if (committed is not null)
                progress.Report = progress.Report with
                {
                    Bytes = committed.Size ?? progress.Report.Bytes,
                    DestinationETag = committed.ETag,
                    DestinationVersionId = committed.VersionId
                };
        }
        else
        {
            // A server-side directory move names no files; they are counted where they landed.
            var (files, directories, bytes) = await CountTreeAsync(destinationBackend,
    destinationTarget).ConfigureAwait(false);
            progress.Report = progress.Report with { Files = files, Directories = directories + 1, Bytes = bytes };
        }
        var enforcement = StorageConditionEnforcement.None;
        if (!options.Overwrite)
            enforcement = await StorageConditionEnforcements.ForAsync(sourceBackend,
    StorageConditionKind.CreateOnly, serverSideCopy: true, CancellationToken.None).ConfigureAwait(false);
        // The source compared just before the move is the weaker guarantee, and it is what is reported.
        if (checkSourceBefore)
            enforcement = StorageConditionEnforcement.CheckedBeforeCommit;
        progress.Report = progress.Report with { ConditionEnforcement = enforcement };
    }
}
