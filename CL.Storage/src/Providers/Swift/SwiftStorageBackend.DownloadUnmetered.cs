using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers.Swift;

public sealed partial class SwiftStorageBackend
{

    private async Task<Result<Stream>> DownloadUnmeteredAsync(string path, StorageDownloadOptions? options, CancellationToken cancellationToken)
    {
        options ??= new StorageDownloadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<Stream>.Failure(validation.Error!);
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<Stream>.Failure(normalized.Error!);
        HttpResponseMessage? response = null;
        try
        {
            response = await SendAsync(() =>
            {
                // A container with object versioning (X-Versions-Enabled) serves an older version by its id.
                var request = new HttpRequestMessage(HttpMethod.Get, ObjectUri(ToKey(normalized.Value!), options.VersionId));
                if (options.Offset > 0 || options.Length.HasValue)
                {
                    var end = options.Length.HasValue ? options.Offset + options.Length.Value - 1 : (long?)null;
                    request.Headers.Range = new RangeHeaderValue(options.Offset, end);
                }
                return request;
            }, cancellationToken, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = FromStatus(response, "Download Swift object");
                response.Dispose();
                response = null;
                return Result<Stream>.Failure(error);
            }
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var owned = new OwnedResourceStream(stream, response);
            response = null;
            return Result<Stream>.Success(owned);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<Stream>.Failure(Map(error, "Download Swift object")); }
        finally { response?.Dispose(); }
    }

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
        try
        {
            var info = await GetInfoAsync(normalized.Value!, cancellationToken).ConfigureAwait(false);
            if (info.IsFailure) return options.IgnoreMissing && info.Error?.Code == StorageErrors.NotFoundCode
                ? Result.Success()
                : Result.Failure(info.Error!);
            if (info.Value!.ItemType == StorageItemType.Directory)
            {
                if (options.Condition is { IsEmpty: false })
                    return Result.Failure(StorageErrors.Unsupported(
                        "Swift virtual directories do not have one atomic identity condition."));
                var listed = await ListAllNamesAsync(DirectoryPrefix(normalized.Value!), cancellationToken).ConfigureAwait(false);
                if (listed.IsFailure) return Result.Failure(listed.Error!);
                var names = listed.Value!;
                if (!options.Recursive && names.Any(name => name != DirectoryPrefix(normalized.Value!)))
                    return Result.Failure(StorageErrors.Conflict("The Swift directory is not empty."));
                foreach (var name in names)
                {
                    var deleted = await DeleteObjectAsync(name, cancellationToken).ConfigureAwait(false);
                    if (deleted.IsFailure) return deleted;
                }
                var markerDelete = await DeleteObjectAsync(DirectoryPrefix(normalized.Value!), cancellationToken, ignoreMissing: true).ConfigureAwait(false);
                if (markerDelete.IsFailure) return markerDelete;
            }
            else
            {
                // Swift ignores If-Match on DELETE: the condition is checked just before.
                var check = CheckCondition(info.Value, options.Condition, "delete");
                if (check.IsFailure) return check;
                return await DeleteObjectAsync(ToKey(normalized.Value!), cancellationToken).ConfigureAwait(false);
            }
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Delete Swift object")); }
    }

    /// <inheritdoc />
    /// <remarks>
    /// An object is copied on the server with <c>COPY</c>, pinned to the version it read (<c>If-Match</c>, which
    /// Swift applies to the source, and <c>?version-id=</c> for <see cref="StorageTransferOptions.SourceVersionId"/>).
    /// A create-only copy is checked just before: Swift has no destination condition on <c>COPY</c>, so a
    /// <see cref="StorageTransferOptions.DestinationCondition"/> is unsupported.
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
        var relationship = StorageTransferPath.ValidateDistinct(source.Value!, destination.Value!);
        if (relationship.IsFailure) return relationship;
        try
        {
            var info = await SourceInfoAsync(source.Value!, options.SourceVersionId, cancellationToken).ConfigureAwait(false);
            if (info.IsFailure) return Result.Failure(info.Error!);
            if (info.Value!.ItemType == StorageItemType.Directory)
            {
                relationship = StorageTransferPath.ValidateDirectoryDestination(source.Value!, destination.Value!);
                if (relationship.IsFailure) return relationship;
                var relayed = await StorageTransferCoordinator.CopyAsync(
                    this,
                    source.Value!,
                    this,
                    destination.Value!,
                    options,
                    cancellationToken).ConfigureAwait(false);
                return relayed.IsSuccess ? Result.Success() : Result.Failure(relayed.Error!);
            }
            return await CopyFileAsync(source.Value!, destination.Value!, info.Value, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Copy Swift object")); }
    }

    /// <inheritdoc />
    /// <remarks>
    /// An object is copied as <see cref="CopyAsync"/> does, then deleted only while it is still the version that was
    /// copied, compared just before the delete (Swift ignores <c>If-Match</c> on <c>DELETE</c>). Once the copy
    /// committed, a failure to delete the source returns <c>storage.partial_failure</c> with
    /// <c>destinationState=complete</c> and <c>leftBehind</c> naming the source, and cancellation no longer applies.
    /// A directory is copied through the relay and each copied object deleted under the identity it was listed with.
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
        StorageItem copied;
        try
        {
            var info = await SourceInfoAsync(source.Value!, options.SourceVersionId, cancellationToken).ConfigureAwait(false);
            if (info.IsFailure) return Result.Failure(info.Error!);
            if (info.Value!.ItemType == StorageItemType.Directory)
            {
                relationship = StorageTransferPath.ValidateDirectoryDestination(source.Value!, destination.Value!);
                if (relationship.IsFailure) return relationship;
                return await ObjectStoreMoves.MoveDirectoryAsync(this, source.Value!, destination.Value!, options, cancellationToken).ConfigureAwait(false);
            }
            var result = await CopyFileAsync(source.Value!, destination.Value!, info.Value, options, cancellationToken).ConfigureAwait(false);
            if (result.IsFailure) return result;
            copied = info.Value;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Move Swift object")); }

        // Committed: the source goes only while it is the version that was copied, and a cancel no longer applies.
        try
        {
            var current = await GetInfoAsync(source.Value!, CancellationToken.None).ConfigureAwait(false);
            if (current.IsFailure)
                return current.Error!.Code == StorageErrors.NotFoundCode
                    ? Result.Success()
                    : Result.Failure(ObjectStoreMoves.SourceKept("Swift", source.Value!, current.Error!));
            if (!StagedWriter.SameETag(copied.ETag, current.Value!.ETag) ||
                (options.SourceVersionId is not null && !string.Equals(copied.VersionId, current.Value.VersionId, StringComparison.Ordinal)))
                return Result.Failure(ObjectStoreMoves.SourceKept("Swift", source.Value!,
                    StorageErrors.Conflict("The Swift source changed after it was copied, so it was not deleted.")));
            var deleted = await DeleteObjectAsync(ToKey(source.Value!), CancellationToken.None, ignoreMissing: true).ConfigureAwait(false);
            return deleted.IsSuccess ? deleted : Result.Failure(ObjectStoreMoves.SourceKept("Swift", source.Value!, deleted.Error!));
        }
        catch (Exception error)
        {
            return Result.Failure(ObjectStoreMoves.SourceKept("Swift", source.Value!, Map(error, "Delete moved Swift object")));
        }
    }

    /// <inheritdoc />
    ValueTask<StorageConditionEnforcement> IStorageConditionEnforcementSource.GetEnforcementAsync(StorageConditionKind kind, bool serverSideCopy, CancellationToken cancellationToken) =>
        ValueTask.FromResult(kind == StorageConditionKind.CreateOnly && !serverSideCopy
            ? StorageConditionEnforcement.Atomic
            : StorageConditionEnforcement.CheckedBeforeCommit);

    /// <summary>A copy or move source: the object (at <paramref name="versionId"/> when set), or a directory.</summary>
    private async Task<Result<StorageItem>> SourceInfoAsync(string path, string? versionId, CancellationToken cancellationToken)
    {
        if (versionId is null)
            return await GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Head, ObjectUri(ToKey(path), versionId)),
            cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? Result<StorageItem>.Success(ToItem(path, response))
            : Result<StorageItem>.Failure(FromStatus(response, "Get Swift object version"));
    }
}
