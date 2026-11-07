using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers.S3;

public sealed partial class S3StorageBackend
{

    private async Task<Result<Stream>> DownloadUnmeteredAsync(string path, StorageDownloadOptions? options, CancellationToken cancellationToken)
    {
        options ??= new StorageDownloadOptions();
        var valid = options.Validate();
        if (valid.IsFailure) return Result<Stream>.Failure(valid.Error!);
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<Stream>.Failure(normalized.Error!);
        try
        {
            var request = new GetObjectRequest { BucketName = _bucket, Key = ToKey(normalized.Value!) };
            if (options.VersionId is not null)
                request.VersionId = options.VersionId;
            if (options.Offset > 0 || options.Length.HasValue)
            {
                var end = options.Length.HasValue ? options.Offset + options.Length.Value - 1 : long.MaxValue;
                request.ByteRange = new ByteRange(options.Offset, end);
            }
            var response = await _client.GetObjectAsync(request, cancellationToken).ConfigureAwait(false);
            return Result<Stream>.Success(new OwnedResourceStream(response.ResponseStream, response));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<Stream>.Failure(Map(error, "Download S3 object")); }
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
        using var target = new MemoryStream();
        var buffer = new byte[81_920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (target.Length > limit - read || target.Length > int.MaxValue - read)
                return Result<byte[]>.Failure(StorageErrors.TooLarge($"The download exceeds the {limit} byte buffering limit."));
            target.Write(buffer, 0, read);
        }
        return Result<byte[]>.Success(target.ToArray());
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
            if (info.IsFailure)
                return options.IgnoreMissing && info.Error?.Code == StorageErrors.NotFoundCode ? Result.Success() : Result.Failure(info.Error!);

            if (info.Value!.ItemType == StorageItemType.Directory)
            {
                if (options.Condition is { IsEmpty: false })
                    return Result.Failure(StorageErrors.Unsupported(
                        "S3 virtual directories do not have one atomic identity condition."));
                var prefix = ToDirectoryPrefix(normalized.Value!);
                string? token = null;
                do
                {
                    var page = await _client.ListObjectsV2Async(new ListObjectsV2Request
                    {
                        BucketName = _bucket,
                        Prefix = prefix,
                        ContinuationToken = token,
                        MaxKeys = 1000
                    }, cancellationToken).ConfigureAwait(false);
                    if (!options.Recursive && (page.S3Objects?.Any(item => item.Key != prefix) ?? false))
                        return Result.Failure(StorageErrors.Conflict("The S3 directory is not empty."));
                    foreach (var item in page.S3Objects ?? [])
                        await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = item.Key }, cancellationToken).ConfigureAwait(false);
                    token = page.NextContinuationToken;
                } while (!string.IsNullOrEmpty(token));
                await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = prefix }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var condition = ValidateCurrentCondition(info.Value, options.Condition, "S3 object");
                if (condition.IsFailure) return condition;
                // Checked just above; sent as If-Match too only where the server enforces it on deletes.
                var sendIfMatch = options.Condition is { IsEmpty: false } &&
                    await ConditionStateAsync(S3ProbedCondition.DeleteMatch, cancellationToken).ConfigureAwait(false) == S3ConditionState.Enforced;
                await _client.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = _bucket,
                    Key = ToKey(normalized.Value!),
                    IfMatch = !sendIfMatch ? null : options.Condition?.ExpectedETag ??
                        (options.Condition?.ExpectedVersionId is null ? null : info.Value.ETag)
                }, cancellationToken).ConfigureAwait(false);
            }
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Delete S3 object")); }
    }

    /// <inheritdoc />
    /// <remarks>
    /// A file is copied on the server in one <c>CopyObject</c> up to 5 GiB and part by part above, always pinned to
    /// the source version it read (<c>x-amz-copy-source-if-match</c>, and <see cref="StorageTransferOptions.SourceVersionId"/>
    /// when set), keeping the content type and headers, user metadata, tags, storage class, and SSE-S3/SSE-KMS
    /// settings. A create-only copy sends <c>If-None-Match: *</c>; whether the server enforces it is reported by
    /// <see cref="IStorageConditionEnforcementSource"/> (MinIO ignores it on <c>CopyObject</c>, so there the
    /// existence check made just before is the only guard). A <see cref="StorageTransferOptions.DestinationCondition"/>
    /// is sent as <c>If-Match</c> and refused as unsupported where the server would ignore it.
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
        var sourceInfo = await SourceHeadAsync(source.Value!, options.SourceVersionId, cancellationToken).ConfigureAwait(false);
        if (sourceInfo.IsFailure) return Result.Failure(sourceInfo.Error!);
        if (sourceInfo.Value.Head is null)
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
        var copied = await CopyFileAsync(source.Value!, destination.Value!, options, sourceInfo.Value.Head, cancellationToken).ConfigureAwait(false);
        return copied.IsSuccess ? Result.Success() : Result.Failure(copied.Error!);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A file is copied as <see cref="CopyAsync"/> does, pinned to the version read, and the source is then deleted
    /// only while it is still that version (<c>If-Match</c>, checked just before as well). Once the copy has
    /// committed, a failure to delete the source (it changed, or the request failed) returns
    /// <c>storage.partial_failure</c> with <c>destinationState=complete</c> and <c>leftBehind</c> naming the source;
    /// cancellation no longer applies then. A directory is copied through the relay and each copied file deleted
    /// under the identity it was listed with; files that changed or appeared meanwhile are kept.
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
        var sourceInfo = await SourceHeadAsync(source.Value!, options.SourceVersionId, cancellationToken).ConfigureAwait(false);
        if (sourceInfo.IsFailure) return Result.Failure(sourceInfo.Error!);
        if (sourceInfo.Value.Head is null)
        {
            relationship = StorageTransferPath.ValidateDirectoryDestination(source.Value!, destination.Value!);
            if (relationship.IsFailure) return relationship;
            return await ObjectStoreMoves.MoveDirectoryAsync(this, source.Value!, destination.Value!, options, cancellationToken).ConfigureAwait(false);
        }

        var copied = await CopyFileAsync(source.Value!, destination.Value!, options, sourceInfo.Value.Head, cancellationToken).ConfigureAwait(false);
        if (copied.IsFailure) return Result.Failure(copied.Error!);
        // The destination is committed: the source is deleted under the identity that was copied, and a cancel
        // can no longer leave the move half-reported.
        var deleted = await DeleteMovedSourceAsync(ToKey(source.Value!), copied.Value!).ConfigureAwait(false);
        return deleted.IsSuccess
            ? Result.Success()
            : Result.Failure(ObjectStoreMoves.SourceKept("S3", source.Value!, deleted.Error!));
    }

    /// <inheritdoc />
    public async Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = _keyPrefix,
                MaxKeys = 1
            }, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Check S3 health")); }
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyDictionary<string, string>>> GetMetadataAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var info = await GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
        return info.IsSuccess
            ? Result<IReadOnlyDictionary<string, string>>.Success(info.Value!.Metadata)
            : Result<IReadOnlyDictionary<string, string>>.Failure(info.Error!);
    }
}
