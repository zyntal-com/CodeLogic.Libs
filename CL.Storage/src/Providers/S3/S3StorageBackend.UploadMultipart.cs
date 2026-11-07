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

    private async Task<S3UploadCompletion> UploadMultipartAsync(
        string key,
        Stream source,
        StorageUploadOptions options,
        string? ifMatch,
        bool sendConditions,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_multipartPartSizeBytes);
        string? uploadId = null;
        try
        {
            var first = await ReadPartAsync(source, buffer, _multipartPartSizeBytes, cancellationToken).ConfigureAwait(false);
            if (first.EndOfStream)
            {
                var content = new MemoryStream(buffer, 0, first.Count, writable: false, publiclyVisible: true);
                await using var contentLifetime = content.ConfigureAwait(false);
                var request = NewPutRequest(key, content, options.ContentType, options.Metadata);
                if (!sendConditions) { /* checked before the upload */ }
                else if (!options.Overwrite)
                    request.IfNoneMatch = "*";
                else if (ifMatch is not null)
                    request.IfMatch = ifMatch;
                var response = await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
                return new S3UploadCompletion(response.ETag?.Trim('"'), response.VersionId, first.Count);
            }

            var initiate = new InitiateMultipartUploadRequest
            {
                BucketName = _bucket,
                Key = key,
                ContentType = options.ContentType
            };
            foreach (var (name, value) in options.Metadata)
                initiate.Metadata[name] = value;
            // Not cancelled half-way: an upload the server created but whose id never came back could not be aborted.
            var initiated = await _client.InitiateMultipartUploadAsync(initiate, CancellationToken.None).ConfigureAwait(false);
            uploadId = initiated.UploadId;
            cancellationToken.ThrowIfCancellationRequested();
            var parts = new List<PartETag>();
            long totalBytes = 0;
            var partNumber = 1;
            var current = first;
            while (current.Count > 0)
            {
                if (partNumber > 10_000)
                    throw new StorageMultipartLimitException();
                var partStream = new MemoryStream(
                    buffer,
                    0,
                    current.Count,
                    writable: false,
                    publiclyVisible: true);
                await using var partStreamLifetime = partStream.ConfigureAwait(false);
                var uploaded = await _client.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = _bucket,
                    Key = key,
                    UploadId = uploadId,
                    PartNumber = partNumber,
                    PartSize = current.Count,
                    InputStream = partStream,
                    IsLastPart = current.EndOfStream,
                    DisablePayloadSigning = _disablePayloadSigning,
                    DisableDefaultChecksumValidation = _disableChecksumValidation
                }, cancellationToken).ConfigureAwait(false);
                parts.Add(new PartETag(uploaded));
                totalBytes = checked(totalBytes + current.Count);
                partNumber++;
                if (current.EndOfStream)
                    break;
                current = await ReadPartAsync(source, buffer, _multipartPartSizeBytes, cancellationToken).ConfigureAwait(false);
            }

            var completeRequest = new CompleteMultipartUploadRequest
            {
                BucketName = _bucket,
                Key = key,
                UploadId = uploadId,
                PartETags = parts
            };
            if (!sendConditions) { /* checked before the upload */ }
            else if (!options.Overwrite)
                completeRequest.IfNoneMatch = "*";
            else if (ifMatch is not null)
                completeRequest.IfMatch = ifMatch;
            // Once sent, the completion is not cancelled: it may commit on the server with its reply lost to the cancel.
            cancellationToken.ThrowIfCancellationRequested();
            var completed = await CompleteMultipartAsync(completeRequest).ConfigureAwait(false);
            uploadId = null;
            return new S3UploadCompletion(completed.ETag?.Trim('"'), completed.VersionId, totalBytes);
        }
        catch (StorageMultipartLimitException)
        {
            throw;
        }
        finally
        {
            if (uploadId is not null)
            {
                try
                {
                    await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                    {
                        BucketName = _bucket,
                        Key = key,
                        UploadId = uploadId
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
            }
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<S3PartRead> ReadPartAsync(
        Stream source,
        byte[] buffer,
        int maxCount,
        CancellationToken cancellationToken)
    {
        var count = 0;
        while (count < maxCount)
        {
            var read = await source.ReadAsync(buffer.AsMemory(count, maxCount - count), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return new S3PartRead(count, EndOfStream: true);
            count += read;
        }
        return new S3PartRead(count, EndOfStream: false);
    }

    /// <summary>
    /// Reads a copy or move source: the object's headers (at <paramref name="versionId"/> when set), or no headers
    /// when the path is a directory.
    /// </summary>
    private async Task<Result<(StorageItem Item, GetObjectMetadataResponse? Head)>> SourceHeadAsync(string path, string? versionId, CancellationToken cancellationToken)
    {
        try
        {
            var head = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _bucket,
                Key = ToKey(path),
                VersionId = versionId
            }, cancellationToken).ConfigureAwait(false);
            return Result<(StorageItem, GetObjectMetadataResponse?)>.Success((FromMetadata(path, head), head));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AmazonS3Exception error) when (IsNotFound(error) && versionId is null)
        {
            var directory = await GetDirectoryInfoAsync(path, cancellationToken).ConfigureAwait(false);
            return directory.IsSuccess
                ? Result<(StorageItem, GetObjectMetadataResponse?)>.Success((directory.Value!, null))
                : Result<(StorageItem, GetObjectMetadataResponse?)>.Failure(directory.Error!);
        }
        catch (Exception error) { return Result<(StorageItem, GetObjectMetadataResponse?)>.Failure(Map(error, "Get S3 copy source")); }
    }

    /// <summary>
    /// Copies one object on the server, pinned to the version <paramref name="head"/> describes, and returns that
    /// identity so a move deletes the source under the same condition.
    /// </summary>
    private async Task<Result<S3PinnedSource>> CopyFileAsync(
        string sourcePath,
        string destinationPath,
        StorageTransferOptions options,
        GetObjectMetadataResponse head,
        CancellationToken cancellationToken)
    {
        if (options.ExpectedSourceETag is { } expected && !StagedWriter.SameETag(expected, head.ETag))
            return Result<S3PinnedSource>.Failure(StorageErrors.Conflict(
                $"The S3 source '{sourcePath}' changed since it was read.",
                $"expectedETag={expected.Trim('"')};actualETag={head.ETag?.Trim('"')}"));
        var sourceKey = ToKey(sourcePath);
        var destinationKey = ToKey(destinationPath);
        var multipart = head.ContentLength > MultipartCopyThresholdBytes;
        string? ifMatch = null;
        if (options.DestinationCondition is { IsEmpty: false } condition)
        {
            // A condition the server would ignore must not be sent as if it were kept: the caller falls back to a
            // path that checks it. A large copy commits with CompleteMultipartUpload, so that request must enforce it.
            if (await ConditionStateAsync(multipart ? S3ProbedCondition.PutMatch : S3ProbedCondition.CopyMatch, cancellationToken).ConfigureAwait(false) != S3ConditionState.Enforced)
                return Result<S3PinnedSource>.Failure(StorageErrors.Unsupported(
                    "This S3-compatible server does not enforce If-Match on the request that commits a copy, so a server-side copy cannot keep the destination condition."));
            var resolved = await ResolveUploadIfMatchAsync(destinationKey, condition, cancellationToken).ConfigureAwait(false);
            if (resolved.IsFailure) return Result<S3PinnedSource>.Failure(resolved.Error!);
            ifMatch = resolved.Value;
        }
        var createOnly = !options.Overwrite;
        var sendCreateOnly = false;
        if (createOnly)
        {
            // Where the server enforces If-None-Match on the committing request this is a courtesy; where it does not
            // (MinIO ignores it on CopyObject; some servers reject it), it is the only guard, the header is not sent,
            // and the enforcement is reported as checked before commit.
            sendCreateOnly = await ConditionStateAsync(multipart ? S3ProbedCondition.PutCreateOnly : S3ProbedCondition.CopyCreateOnly, cancellationToken).ConfigureAwait(false) == S3ConditionState.Enforced;
            var exists = await ExistsAsync(destinationPath, cancellationToken).ConfigureAwait(false);
            if (exists.IsFailure) return Result<S3PinnedSource>.Failure(exists.Error!);
            if (exists.Value) return Result<S3PinnedSource>.Failure(StorageErrors.Conflict("The S3 destination already exists."));
            if (BeforeConditionalCopy is { } hook) await hook().ConfigureAwait(false);
        }
        try
        {
            if (multipart)
                await CopyMultipartAsync(sourceKey, destinationKey, head, options.SourceVersionId, sendCreateOnly, ifMatch, cancellationToken).ConfigureAwait(false);
            else
            {
                // Once sent, a CopyObject is not cancelled: it may commit on the server with its reply lost to the
                // cancel, and a move would then keep a source whose copy exists.
                cancellationToken.ThrowIfCancellationRequested();
                await _client.CopyObjectAsync(NewCopyRequest(sourceKey, destinationKey, head, options.SourceVersionId, sendCreateOnly, ifMatch), CancellationToken.None).ConfigureAwait(false);
            }
            return Result<S3PinnedSource>.Success(new S3PinnedSource(head.ETag, options.SourceVersionId));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return Result<S3PinnedSource>.Failure(StorageErrors.Conflict(
                createOnly
                    ? "The S3 copy was refused: the destination now exists, or the source changed since it was read."
                    : "The S3 copy was refused: the source changed since it was read, or the destination no longer matches its condition.",
                ProviderErrorMapper.Details(StorageErrorInfo.HttpStatusKey, "412", error.ErrorCode)));
        }
        catch (Exception error) { return Result<S3PinnedSource>.Failure(Map(error, "Copy S3 object")); }
    }
}
