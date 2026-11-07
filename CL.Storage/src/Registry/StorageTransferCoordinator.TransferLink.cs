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
    /// Applies <see cref="StorageTransferOptions.LinkHandling"/> to one link. <c>treeRoot</c> is the source
    /// directory being transferred, used to remap link targets inside it, or null for a single link.
    /// </summary>
    private static async Task<Result<StorageTransferSummary>> TransferLinkAsync(
        IStorageBackend source,
        StorageItem link,
        string? treeRoot,
        IStorageBackend destination,
        string destinationPath,
        StorageTransferOptions options,
        TransferCleanupTracker cleanup,
        CancellationToken cancellationToken)
    {
        switch (options.LinkHandling)
        {
            case StorageLinkHandling.Skip:
                // Counted as skipped, so a move leaves it where it is.
                return Result<StorageTransferSummary>.Success(new StorageTransferSummary(StorageItemType.Link, 0, 0, 0, SkippedFiles: 1, TransferredSources: [])
                {
                    SkipReason = StorageSkipReason.Link,
                    SkippedSources = [link.Path]
                });

            case StorageLinkHandling.Follow:
            {
                // Some providers stat through the link and report the target's type.
                var target = await source.GetInfoAsync(link.Path, cancellationToken).ConfigureAwait(false);
                if (target.IsSuccess && target.Value!.ItemType == StorageItemType.Directory)
                    return Result<StorageTransferSummary>.Failure(StorageErrors.Unsupported(
                        $"Link '{link.Path}' points to a directory; following directory links is not supported."));
                var file = target.IsSuccess && target.Value!.ItemType == StorageItemType.File
                    ? target.Value with { Path = link.Path }
                    : link with { ItemType = StorageItemType.File, Size = null };
                return await CopyFileAsync(source, file, destination, destinationPath, options, cleanup, cancellationToken).ConfigureAwait(false);
            }

            case StorageLinkHandling.Recreate:
            {
                if (source is not IStorageAttributeService reader || !source.Capabilities.Supports(StorageFeature.ReadLinks))
                    return Result<StorageTransferSummary>.Failure(StorageErrors.Unsupported(
                        "The source connection cannot read link targets, so links cannot be recreated."));
                if (destination is not IStorageAttributeService writer || !destination.Capabilities.Supports(StorageFeature.CreateLinks))
                    return Result<StorageTransferSummary>.Failure(StorageErrors.Unsupported(
                        "The destination connection cannot create links."));
                var target = await reader.ReadLinkAsync(link.Path, cancellationToken).ConfigureAwait(false);
                if (target.IsFailure)
                    return Result<StorageTransferSummary>.Failure(target.Error!);
                if (target.Value!.StoragePath is not { } targetPath)
                    return Result<StorageTransferSummary>.Failure(StorageErrors.Unsupported(
                        $"Link '{link.Path}' points outside the source root and cannot be recreated."));
                var mapped = targetPath;
                if (treeRoot is not null)
                {
                    var relative = GetRelativePath(treeRoot, targetPath);
                    if (relative is null)
                        return Result<StorageTransferSummary>.Failure(StorageErrors.Unsupported(
                            $"Link '{link.Path}' points outside the transferred directory and cannot be recreated."));
                    mapped = Combine(DestinationTreeRoot(destinationPath, link.Path, treeRoot), relative);
                }
                var created = await writer.CreateLinkAsync(destinationPath, mapped, cancellationToken).ConfigureAwait(false);
                if (created.IsFailure)
                    return Result<StorageTransferSummary>.Failure(created.Error!);
                Result<StorageItem> createdLink;
                try { createdLink = await destination.GetInfoAsync(destinationPath, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) { createdLink = Result<StorageItem>.Failure(StorageErrors.FromException(error, "Read created link")); }
                cleanup.TrackFile(destinationPath, createdLink.IsSuccess ? createdLink.Value : null);
                return Result<StorageTransferSummary>.Success(new StorageTransferSummary(StorageItemType.Link, 0, 0, 0));
            }

            default:
                return Result<StorageTransferSummary>.Failure(StorageErrors.Unsupported(
                    $"Link '{link.Path}' was not transferred: link targets are provider-specific. Set LinkHandling to skip, follow, or recreate links."));
        }
    }

    /// <summary>Returns the destination directory that corresponds to <paramref name="treeRoot"/>.</summary>
    private static string DestinationTreeRoot(string destinationItemPath, string sourceItemPath, string treeRoot)
    {
        var relative = GetRelativePath(treeRoot, sourceItemPath) ?? string.Empty;
        var depth = relative.Length == 0 ? 0 : relative.Split('/').Length;
        var segments = destinationItemPath.Split('/');
        return string.Join('/', segments.Take(Math.Max(0, segments.Length - depth)));
    }
}
