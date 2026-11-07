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
    /// Keeps a resumable part file (and records it for a token) or removes a staging object. A part file that
    /// is already gone is simply not reported.
    /// </summary>
    private static async Task<Result> SettleStagingAsync(IStorageBackend destination, StagedContent staged, bool resumable, TransferState state)
    {
        if (resumable)
        {
            Result<StorageItem> kept;
            try { kept = await destination.GetInfoAsync(staged.StagingPath, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) { kept = Result<StorageItem>.Failure(StorageErrors.FromException(error, "Read transfer staging object")); }
            if (kept.IsSuccess)
            {
                state.StagingLeftBehind = staged.StagingPath;
                state.StagingResumable = true;
                state.BytesStaged = kept.Value!.Size ?? staged.Bytes + staged.BytesResumed;
                return Result.Success();
            }
            if (kept.Error!.Code == StorageErrors.NotFoundCode)
                return Result.Success();
        }
        var deleted = await DeleteStagingAsync(destination, staged.StagingPath).ConfigureAwait(false);
        if (deleted.IsFailure) state.StagingLeftBehind = staged.StagingPath;
        return deleted;
    }

    /// <summary>
    /// Whether a token's staging path is the part file its own fields derive for <paramref name="destinationPath"/>:
    /// only then may the library delete it as stale. Any other path in a token is not the library's to touch.
    /// </summary>
    private static bool IsOwnPartFile(StorageResumeToken token, string destinationPath) =>
        token.DestinationPath == destinationPath &&
        token.StagingPath == StagedWriter.ResumableStagingPath(
            token.DestinationPath,
            StagedWriter.SourceKey(token.SourcePath, token.SourceLength, token.SourceLastModified, token.SourceETag, token.SourceVersionId));

    /// <summary>Whether an item carries anything that tells one version of its content from another.</summary>
    private static bool HasIdentity(StorageItem item) =>
        item.ETag is not null || item.LastModified is not null || item.VersionId is not null;

    /// <summary>
    /// Whether a resume token's staged bytes came from the current source: every identity field must match
    /// exactly, and a field missing on one side only is a mismatch, not a wildcard.
    /// </summary>
    private static bool IsSameSource(StorageResumeToken token, StorageItem source) =>
        HasIdentity(source) &&
        token.SourcePath == source.Path &&
        token.SourceLength == source.Size &&
        token.SourceVersionId == source.VersionId &&
        (token.SourceETag is null ? source.ETag is null : StagedWriter.SameETag(token.SourceETag, source.ETag)) &&
        token.SourceLastModified == source.LastModified;

    /// <summary>
    /// Whether the destination provably holds the source's content: both sides report the same digest for
    /// a common algorithm. Without that evidence the content is written again rather than assumed complete.
    /// </summary>
    internal static async Task<bool> SameContentAsync(
        IStorageService source,
        StorageItem sourceFile,
        IStorageService destination,
        StorageItem destinationItem,
        bool pinnedVersion,
        CancellationToken cancellationToken)
    {
        // A server digest describes the latest version, so it proves nothing about a pinned older one.
        if (pinnedVersion)
            return false;
        foreach (var algorithm in new[] { StorageChecksumAlgorithm.Sha256, StorageChecksumAlgorithm.Md5 })
        {
            var ours = await destination.GetServerChecksumAsync(destinationItem.Path, algorithm, cancellationToken).ConfigureAwait(false);
            if (ours.IsFailure) continue;
            var theirs = await source.GetServerChecksumAsync(sourceFile.Path, algorithm, cancellationToken).ConfigureAwait(false);
            if (theirs.IsFailure) continue;
            return string.Equals(ours.Value!.HexValue, theirs.Value!.HexValue, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static string Name(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? path : path[(index + 1)..];
    }

    private static async Task<Result> EnsureDirectoryAsync(
        IStorageBackend destination,
        string path,
        bool createParents,
        TransferCleanupTracker cleanup,
        CancellationToken cancellationToken)
    {
        if (path.Length == 0)
            return Result.Success();
        var exists = await destination.ExistsAsync(path, cancellationToken).ConfigureAwait(false);
        if (exists.IsFailure)
            return Result.Failure(exists.Error!);
        if (exists.Value)
        {
            var info = await destination.GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
            if (info.IsFailure)
                return Result.Failure(info.Error!);
            return info.Value!.ItemType == StorageItemType.Directory
                ? Result.Success()
                : Result.Failure(StorageErrors.Conflict(
                    $"The transfer path '{path}' is not a directory."));
        }

        var parent = Parent(path);
        if (parent.Length > 0)
        {
            if (!createParents)
            {
                var parentExists = await destination.ExistsAsync(parent, cancellationToken).ConfigureAwait(false);
                if (parentExists.IsFailure)
                    return Result.Failure(parentExists.Error!);
                if (!parentExists.Value)
                    return Result.Failure(StorageErrors.NotFound(
                        $"The transfer destination parent '{parent}' was not found."));
            }
            else
            {
                var ensuredParent = await EnsureDirectoryAsync(
                    destination,
                    parent,
                    createParents: true,
                    cleanup,
                    cancellationToken).ConfigureAwait(false);
                if (ensuredParent.IsFailure)
                    return ensuredParent;
            }
        }

        var created = await destination.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        if (created.IsFailure)
            return created;
        cleanup.TrackDirectory(path);
        return Result.Success();
    }

    private static Task<Result<string>> AllocateStagingPathAsync(
        IStorageBackend destination,
        string parent,
        CancellationToken cancellationToken,
        string namePrefix = ".cl-storage-transfer-") =>
        StagedWriter.AllocateStagingPathAsync(destination, parent, cancellationToken, namePrefix);

    private static Task<Result> DeleteStagingAsync(IStorageBackend destination, string stagingPath) =>
        StagedWriter.DeleteAsync(destination, stagingPath);

    private static Result<StorageTransferSummary> FailureAfterCleanup(
        Error primary,
        string message,
        params Result[] cleanupResults)
    {
        var cleanupErrors = cleanupResults
            .Where(result => result.IsFailure)
            .Select(result => result.Error!.Code)
            .ToArray();
        return cleanupErrors.Length == 0
            ? Result<StorageTransferSummary>.Failure(primary)
            : Result<StorageTransferSummary>.Failure(StorageErrors.PartialFailure(
                $"{message}, and cleanup was incomplete.",
                $"primaryError={primary.Code};cleanupErrors={string.Join(',', cleanupErrors)}"));
    }

    /// <summary>
    /// Settles the backup after a promote that did not complete (or may not have). Someone else may have
    /// written the destination since it was backed up, so the backup is put back only when the destination is
    /// gone — a copied backup is copied back create-only, a renamed one renamed back; a destination that is
    /// still the backed-up version, or someone else's newer write, is left alone and the backup removed.
    /// </summary>
    private static async Task<Result> RecoverReplacementAsync(
        IStorageBackend destination,
        string destinationPath,
        string backupPath,
        bool renamed)
    {
        try
        {
            var current = await destination.GetInfoAsync(destinationPath, CancellationToken.None).ConfigureAwait(false);
            if (current.IsFailure && current.Error!.Code != StorageErrors.NotFoundCode)
                return Result.Failure(current.Error);
            if (current.IsFailure)
            {
                var putBack = new StorageTransferOptions { Overwrite = false, CreateParents = false };
                var restored = renamed
                    ? await destination.MoveAsync(backupPath, destinationPath, putBack, CancellationToken.None).ConfigureAwait(false)
                    : await destination.CopyAsync(backupPath, destinationPath, putBack, CancellationToken.None).ConfigureAwait(false);
                if (restored.IsFailure && !StorageErrorInfo.DestinationCommitted(restored.Error))
                    return restored;
                if (renamed)
                    return Result.Success();
            }
            return await destination.DeleteAsync(
                backupPath,
                new StorageDeleteOptions { IgnoreMissing = true },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return Result.Failure(StorageErrors.FromException(error, "Restore transfer destination"));
        }
    }

    /// <summary>
    /// Whether <paramref name="current"/> is still the version <paramref name="committed"/> describes: the same
    /// ETag or version where both have one, otherwise the same size and modification time.
    /// </summary>
    internal static bool SameVersion(StorageItem committed, StorageItem current)
    {
        if (committed.ETag is not null && current.ETag is not null)
        {
            // A strong match proves the version; any mismatch proves a change. Weak validators that match prove
            // nothing more than size and time do, so they fall through to that comparison.
            if (!StorageETags.WeakEquals(committed.ETag, current.ETag)) return false;
            if (!StorageETags.IsWeak(committed.ETag) && !StorageETags.IsWeak(current.ETag)) return true;
        }
        if (committed.VersionId is not null && current.VersionId is not null)
            return string.Equals(committed.VersionId, current.VersionId, StringComparison.Ordinal);
        return committed.Size == current.Size && committed.LastModified == current.LastModified;
    }

    private static string? GetRelativePath(string root, string candidate)
    {
        if (root.Length == 0)
            return candidate.TrimStart('/');
        if (string.Equals(root, candidate, StringComparison.Ordinal))
            return string.Empty;
        var prefix = root + "/";
        return candidate.StartsWith(prefix, StringComparison.Ordinal)
            ? candidate[prefix.Length..]
            : null;
    }

    private static string Parent(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? string.Empty : path[..index];
    }

    private static string Combine(string parent, string child) =>
        parent.Length == 0 ? child.TrimStart('/') : $"{parent.TrimEnd('/')}/{child.TrimStart('/')}";
}
