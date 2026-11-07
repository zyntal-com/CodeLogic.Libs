using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers.Azure;

public sealed partial class AzureBlobStorageBackend
{

    /// <inheritdoc />
    public async Task<Result<byte[]>> DownloadBytesAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageDownloadOptions();
        var limit = options.MaxBufferedBytes ?? _maxBufferedDownloadBytes;
        var download = await DownloadAsync(path, options, cancellationToken).ConfigureAwait(false);
        if (download.IsFailure) return Result<byte[]>.Failure(download.Error!);
        var source = download.Value!;
        await using var sourceLifetime = source.ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[81_920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (destination.Length > Math.Min(limit, int.MaxValue) - read)
                return Result<byte[]>.Failure(StorageErrors.TooLarge($"The download exceeds the {limit} byte buffering limit."));
            destination.Write(buffer, 0, read);
        }
        return Result<byte[]>.Success(destination.ToArray());
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync(string path, StorageDeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageDeleteOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return validation;
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result.Failure(normalized.Error!);
        var normalizedPath = normalized.Value!;
        try
        {
            var info = await GetInfoAsync(normalizedPath, cancellationToken).ConfigureAwait(false);
            if (info.IsFailure) return options.IgnoreMissing && info.Error?.Code == StorageErrors.NotFoundCode
                ? Result.Success()
                : Result.Failure(info.Error!);
            if (info.Value!.ItemType == StorageItemType.Directory)
            {
                if (options.Condition is { IsEmpty: false })
                    return Result.Failure(StorageErrors.Unsupported(
                        "Azure virtual directories do not have one atomic identity condition."));
                var prefix = DirectoryPrefix(normalizedPath);
                var names = new List<string>();
                await foreach (var item in _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, cancellationToken).ConfigureAwait(false))
                    names.Add(item.Name);
                if (!options.Recursive && names.Any(name => name != prefix))
                    return Result.Failure(StorageErrors.Conflict("The Azure blob directory is not empty."));
                foreach (var name in names)
                    await _container.DeleteBlobIfExistsAsync(name, DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken).ConfigureAwait(false);
                await _container.DeleteBlobIfExistsAsync(prefix, DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var condition = ValidateCurrentCondition(info.Value, options.Condition, "Azure blob");
                if (condition.IsFailure) return condition;
                var requestConditions = options.Condition is { IsEmpty: false }
                    ? new BlobRequestConditions
                    {
                        IfMatch = options.Condition.ExpectedETag is not null
                            ? new ETag(options.Condition.ExpectedETag)
                            : info.Value.ETag is not null ? new ETag(info.Value.ETag) : null
                    }
                    : null;
                await Blob(normalizedPath).DeleteIfExistsAsync(
                    DeleteSnapshotsOption.IncludeSnapshots,
                    requestConditions,
                    cancellationToken).ConfigureAwait(false);
            }
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Delete Azure blob")); }
    }

    /// <inheritdoc />
    /// <remarks>
    /// A blob is copied on the server pinned to the version it read (source <c>If-Match</c>, and
    /// <see cref="StorageTransferOptions.SourceVersionId"/> when set); a create-only copy carries <c>If-None-Match: *</c>
    /// and a <see cref="StorageTransferOptions.DestinationCondition"/> the destination's <c>If-Match</c>.
    /// </remarks>
    public async Task<Result> CopyAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageTransferOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return validation;
        var source = NormalizeRequired(sourcePath);
        if (source.IsFailure) return Result.Failure(source.Error!);
        var destination = NormalizeRequired(destinationPath);
        if (destination.IsFailure) return Result.Failure(destination.Error!);
        var sourceValue = source.Value!;
        var destinationValue = destination.Value!;
        var relationship = StorageTransferPath.ValidateDistinct(sourceValue, destinationValue);
        if (relationship.IsFailure) return relationship;
        var info = await SourceInfoAsync(sourceValue, options.SourceVersionId, cancellationToken).ConfigureAwait(false);
        if (info.IsFailure) return Result.Failure(info.Error!);
        if (info.Value!.ItemType == StorageItemType.Directory)
        {
            relationship = StorageTransferPath.ValidateDirectoryDestination(sourceValue, destinationValue);
            if (relationship.IsFailure) return relationship;
            var relayed = await StorageTransferCoordinator.CopyAsync(
                this,
                sourceValue,
                this,
                destinationValue,
                options,
                cancellationToken).ConfigureAwait(false);
            return relayed.IsSuccess ? Result.Success() : Result.Failure(relayed.Error!);
        }
        var copied = await CopyFileAsync(sourceValue, destinationValue, info.Value, options, cancellationToken).ConfigureAwait(false);
        return copied.IsSuccess ? Result.Success() : Result.Failure(copied.Error!);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A blob is copied as <see cref="CopyAsync"/> does, then deleted only while it still has the ETag that was
    /// copied. Once the copy committed, a failure to delete the source returns <c>storage.partial_failure</c> with
    /// <c>destinationState=complete</c> and <c>leftBehind</c> naming the source, and cancellation no longer applies.
    /// A directory is copied through the relay and each copied blob deleted under the identity it was listed with.
    /// <para>
    /// Snapshots do not move: the copy carries only the blob's current content, and deleting the source deletes its
    /// snapshots with it, as <see cref="DeleteAsync"/> does (Azure deletes a blob with snapshots only when told to
    /// delete them too).
    /// Previous versions on an account with blob versioning are kept by Azure as usual. To keep snapshots, copy the
    /// blob and delete the source yourself.
    /// </para>
    /// </remarks>
    public async Task<Result> MoveAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageTransferOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return validation;
        var source = NormalizeRequired(sourcePath);
        if (source.IsFailure) return Result.Failure(source.Error!);
        var destination = NormalizeRequired(destinationPath);
        if (destination.IsFailure) return Result.Failure(destination.Error!);
        var relationship = StorageTransferPath.ValidateDistinct(source.Value!, destination.Value!);
        if (relationship.IsFailure) return relationship;
        var info = await SourceInfoAsync(source.Value!, options.SourceVersionId, cancellationToken).ConfigureAwait(false);
        if (info.IsFailure) return Result.Failure(info.Error!);
        if (info.Value!.ItemType == StorageItemType.Directory)
        {
            relationship = StorageTransferPath.ValidateDirectoryDestination(source.Value!, destination.Value!);
            if (relationship.IsFailure) return relationship;
            return await ObjectStoreMoves.MoveDirectoryAsync(this, source.Value!, destination.Value!, options, cancellationToken).ConfigureAwait(false);
        }
        var copied = await CopyFileAsync(source.Value!, destination.Value!, info.Value, options, cancellationToken).ConfigureAwait(false);
        if (copied.IsFailure) return copied;
        // Committed: the source goes only while it is the blob that was copied, and a cancel no longer applies.
        try
        {
            await Blob(source.Value!).DeleteAsync(
                DeleteSnapshotsOption.IncludeSnapshots,
                new BlobRequestConditions { IfMatch = new ETag(info.Value.ETag!) },
                CancellationToken.None).ConfigureAwait(false);
            return Result.Success();
        }
        catch (RequestFailedException error) when (error.Status == 404)
        {
            return Result.Success();
        }
        catch (RequestFailedException error) when (error.Status == 412)
        {
            return Result.Failure(ObjectStoreMoves.SourceKept("Azure Blob", source.Value!,
                StorageErrors.Conflict("The Azure blob source changed after it was copied, so it was not deleted.")));
        }
        catch (Exception error)
        {
            return Result.Failure(ObjectStoreMoves.SourceKept("Azure Blob", source.Value!, Map(error, "Delete moved Azure blob")));
        }
    }

    /// <summary>Runs right after a server-side copy started, so a test can cancel while it is pending.</summary>
    internal Func<Task>? AfterCopyStarted { get; init; }

    /// <summary>A copy or move source: the blob (at <paramref name="versionId"/> when set), or a directory.</summary>
    private async Task<Result<StorageItem>> SourceInfoAsync(string path, string? versionId, CancellationToken cancellationToken)
    {
        if (versionId is null)
            return await GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            var properties = await Blob(path).WithVersion(versionId).GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return Result<StorageItem>.Success(ToItem(path, properties.Value));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Get Azure blob version")); }
    }

    /// <summary>Copies one blob on the server, pinned to the version <paramref name="source"/> describes.</summary>
    private async Task<Result> CopyFileAsync(string sourcePath, string destinationPath, StorageItem source, StorageTransferOptions options, CancellationToken cancellationToken)
    {
        if (options.ExpectedSourceETag is { } expected && !StagedWriter.SameETag(expected, source.ETag))
            return Result.Failure(StorageErrors.Conflict(
                $"The Azure blob source '{sourcePath}' changed since it was read.",
                $"expectedETag={expected.Trim('"')};actualETag={source.ETag}"));
        try
        {
            BlobRequestConditions? destinationConditions = null;
            if (!options.Overwrite)
            {
                destinationConditions = new BlobRequestConditions { IfNoneMatch = ETag.All };
            }
            else if (options.DestinationCondition is { IsEmpty: false } condition)
            {
                var expectedETag = condition.ExpectedETag;
                if (condition.ExpectedVersionId is not null)
                {
                    var current = await GetInfoAsync(destinationPath, cancellationToken).ConfigureAwait(false);
                    if (current.IsFailure)
                        return Result.Failure(current.Error!.Code == StorageErrors.NotFoundCode
                            ? StorageErrors.Conflict("The Azure blob destination no longer exists for the requested condition.")
                            : current.Error!);
                    var check = ValidateCurrentCondition(current.Value!, condition, "Azure blob");
                    if (check.IsFailure) return check;
                    expectedETag ??= current.Value!.ETag;
                }
                destinationConditions = new BlobRequestConditions { IfMatch = new ETag(expectedETag!) };
            }
            var sourceBlob = options.SourceVersionId is { } version ? Blob(sourcePath).WithVersion(version) : Blob(sourcePath);
            var operation = await Blob(destinationPath).StartCopyFromUriAsync(sourceBlob.Uri, new BlobCopyFromUriOptions
            {
                SourceConditions = source.ETag is null ? null : new BlobRequestConditions { IfMatch = new ETag(source.ETag) },
                DestinationConditions = destinationConditions
            }, cancellationToken).ConfigureAwait(false);
            if (AfterCopyStarted is { } started) await started().ConfigureAwait(false);
            // Once started, the copy has already replaced the destination (a pending copy has no old content to go
            // back to), so it is waited for whatever the caller's token says: abandoning it would leave a failed copy
            // over the old content, and the caller would report nothing committed. (The started operation keeps the
            // caller's token for its polls, so a fresh one follows the copy by its id.)
            await new CopyFromUriOperation(operation.Id, Blob(destinationPath)).WaitForCompletionAsync(CancellationToken.None).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (RequestFailedException error) when (error.Status is 409 or 412)
        {
            return Result.Failure(StorageErrors.Conflict(
                !options.Overwrite
                    ? "The Azure blob destination already exists, or the source changed since it was read."
                    : "The Azure blob source changed since it was read, or the destination no longer matches its condition.",
                ProviderErrorMapper.Details(StorageErrorInfo.HttpStatusKey, error.Status.ToString(CultureInfo.InvariantCulture), error.ErrorCode)));
        }
        catch (Exception error) { return Result.Failure(Map(error, "Copy Azure blob")); }
    }
}
