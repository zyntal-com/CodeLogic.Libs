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

    private static async Task<Result<StorageTransferSummary>> CopyDirectoryAsync(
        IStorageBackend source,
        StorageItem sourceDirectory,
        IStorageBackend destination,
        string destinationPath,
        StorageTransferOptions options,
        TransferCleanupTracker cleanup,
        CancellationToken cancellationToken,
        TransferState state)
    {
        // A directory changes the destination as soon as its first folder or file lands.
        if (options.PhaseChanged is { } committing)
            await committing(Queue.StorageTransferPhase.Committing, cancellationToken).ConfigureAwait(false);
        var destinationDirectory = await EnsureDirectoryAsync(
            destination,
            destinationPath,
            options.CreateParents,
            cleanup,
            cancellationToken).ConfigureAwait(false);
        if (destinationDirectory.IsFailure)
            return Result<StorageTransferSummary>.Failure(destinationDirectory.Error!);

        long files = 0;
        long directories = 1;
        long bytes = 0;
        long skipped = 0;
        var transferred = new List<string>();
        var transferredItems = new List<StorageItem>();
        var skippedSources = new List<string>();
        string? stagingLeft = null;
        string? backupLeft = null;
        var enforcement = StorageConditionEnforcement.None;
        string? continuationToken = null;
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        var seenItems = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var page = await source.ListAsync(
                sourceDirectory.Path,
                new StorageListOptions
                {
                    Recursive = true,
                    PageSize = PageSize,
                    ContinuationToken = continuationToken
                },
                cancellationToken).ConfigureAwait(false);
            if (page.IsFailure)
                return Result<StorageTransferSummary>.Failure(page.Error!);

            foreach (var item in page.Value!.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var itemPath = StoragePath.Normalize(item.Path);
                if (itemPath.IsFailure)
                    return Result<StorageTransferSummary>.Failure(StorageErrors.ProviderError(
                        "The source provider returned an invalid path while listing a directory."));
                if (!seenItems.Add(itemPath.Value!))
                    continue;

                var relative = GetRelativePath(sourceDirectory.Path, itemPath.Value!);
                if (relative is null)
                    return Result<StorageTransferSummary>.Failure(StorageErrors.ProviderError(
                        "The source provider returned an item outside the requested directory."));
                if (relative.Length == 0)
                    continue;
                var mappedPath = Combine(destinationPath, relative);

                switch (item.ItemType)
                {
                    case StorageItemType.Directory:
                        {
                            var directory = await EnsureDirectoryAsync(
                                destination,
                                mappedPath,
                                options.CreateParents,
                                cleanup,
                                cancellationToken).ConfigureAwait(false);
                            if (directory.IsFailure)
                                return Result<StorageTransferSummary>.Failure(directory.Error!);
                            directories++;
                            break;
                        }
                    case StorageItemType.File:
                        {
                            var parent = Parent(mappedPath);
                            if (parent.Length > 0)
                            {
                                var directory = await EnsureDirectoryAsync(
                                    destination,
                                    parent,
                                    options.CreateParents,
                                    cleanup,
                                    cancellationToken).ConfigureAwait(false);
                                if (directory.IsFailure)
                                    return Result<StorageTransferSummary>.Failure(directory.Error!);
                            }
                            var fileState = new TransferState();
                            var copied = await CopyFileAsync(
                                source,
                                item with { Path = itemPath.Value! },
                                destination,
                                mappedPath,
                                options,
                                cleanup,
                                cancellationToken,
                                fileState).ConfigureAwait(false);
                            // What each file committed, left behind, and how its condition held is the directory's too.
                            state.FilesCommitted += fileState.FilesCommitted;
                            state.BytesCommitted += fileState.BytesCommitted;
                            state.ConditionEnforcement = Weakest(state.ConditionEnforcement, fileState.ConditionEnforcement);
                            state.BackupLeftBehind ??= fileState.BackupLeftBehind;
                            if (!fileState.StagingResumable)
                                state.StagingLeftBehind ??= fileState.StagingLeftBehind;
                            if (copied.IsFailure)
                                return Result<StorageTransferSummary>.Failure(copied.Error!);
                            var file = copied.Value!;
                            stagingLeft ??= file.StagingLeftBehind;
                            backupLeft ??= file.BackupLeftBehind;
                            enforcement = Weakest(enforcement, file.ConditionEnforcement);
                            files += copied.Value!.Files;
                            bytes += copied.Value.Bytes;
                            skipped += copied.Value.SkippedFiles;
                            transferred.AddRange(copied.Value.TransferredSources ?? []);
                            transferredItems.AddRange(copied.Value.TransferredItems ?? []);
                            skippedSources.AddRange(copied.Value.SkippedSources ?? []);
                            break;
                        }
                    default:
                        {
                            var parent = Parent(mappedPath);
                            if (parent.Length > 0 && options.LinkHandling is StorageLinkHandling.Follow or StorageLinkHandling.Recreate)
                            {
                                var directory = await EnsureDirectoryAsync(
                                    destination,
                                    parent,
                                    options.CreateParents,
                                    cleanup,
                                    cancellationToken).ConfigureAwait(false);
                                if (directory.IsFailure)
                                    return Result<StorageTransferSummary>.Failure(directory.Error!);
                            }
                            var linked = await TransferLinkAsync(
                                source,
                                item with { Path = itemPath.Value! },
                                sourceDirectory.Path,
                                destination,
                                mappedPath,
                                options,
                                cleanup,
                                cancellationToken).ConfigureAwait(false);
                            if (linked.IsFailure)
                                return Result<StorageTransferSummary>.Failure(linked.Error!);
                            files += linked.Value!.Files;
                            bytes += linked.Value.Bytes;
                            skipped += linked.Value.SkippedFiles;
                            skippedSources.AddRange(linked.Value.SkippedSources ?? []);
                            // A recreated link was moved (an equivalent link exists at the destination), and so was
                            // a followed link's content; a skipped link stays behind on a move.
                            if (options.LinkHandling == StorageLinkHandling.Recreate)
                            {
                                transferred.Add(itemPath.Value!);
                                transferredItems.Add(item with { Path = itemPath.Value! });
                            }
                            else
                            {
                                transferred.AddRange(linked.Value.TransferredSources ?? []);
                                transferredItems.AddRange(linked.Value.TransferredItems ?? []);
                            }
                            break;
                        }
                }
            }

            var next = page.Value.ContinuationToken;
            if (string.IsNullOrEmpty(next))
            {
                continuationToken = null;
            }
            else if (!seenTokens.Add(next))
            {
                return Result<StorageTransferSummary>.Failure(StorageErrors.ProviderError(
                    "The source provider repeated a continuation token while listing a directory."));
            }
            else
            {
                continuationToken = next;
            }
        }
        while (continuationToken is not null);

        return Result<StorageTransferSummary>.Success(new StorageTransferSummary(
            sourceDirectory.ItemType,
            files,
            directories,
            bytes,
            skipped,
            transferred)
        {
            TransferredItems = transferredItems,
            SkippedSources = skippedSources,
            StagingLeftBehind = stagingLeft,
            BackupLeftBehind = backupLeft,
            ConditionEnforcement = enforcement
        });
    }

    /// <summary>The weaker of two enforcements: a check just before the commit outranks an atomic one, which outranks none.</summary>
    private static StorageConditionEnforcement Weakest(StorageConditionEnforcement a, StorageConditionEnforcement b) =>
        a == StorageConditionEnforcement.CheckedBeforeCommit || b == StorageConditionEnforcement.CheckedBeforeCommit
            ? StorageConditionEnforcement.CheckedBeforeCommit
            : a == StorageConditionEnforcement.Atomic || b == StorageConditionEnforcement.Atomic ? StorageConditionEnforcement.Atomic : StorageConditionEnforcement.None;
}
