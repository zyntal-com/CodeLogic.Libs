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

    /// <summary>A single <c>CopyObject</c>: content type, headers, user metadata, and tags come along by default.</summary>
    private CopyObjectRequest NewCopyRequest(
        string sourceKey,
        string destinationKey,
        GetObjectMetadataResponse head,
        string? sourceVersionId,
        bool sendCreateOnly,
        string? ifMatch)
    {
        var request = new CopyObjectRequest
        {
            SourceBucket = _bucket,
            SourceKey = sourceKey,
            SourceVersionId = sourceVersionId,
            DestinationBucket = _bucket,
            DestinationKey = destinationKey,
            ETagToMatch = head.ETag
        };
        // Only conditions the server was found to enforce are sent (see ConditionStateAsync).
        if (sendCreateOnly) request.IfNoneMatch = "*";
        else if (ifMatch is not null) request.IfMatch = ifMatch;
        // Without these the copy would take the bucket's defaults: STANDARD storage and the default encryption.
        if (IsNonStandard(head.StorageClass)) request.StorageClass = head.StorageClass;
        if (IsEncrypted(head.ServerSideEncryptionMethod))
        {
            request.ServerSideEncryptionMethod = head.ServerSideEncryptionMethod;
            request.ServerSideEncryptionKeyManagementServiceKeyId = head.ServerSideEncryptionKeyManagementServiceKeyId;
            request.BucketKeyEnabled = head.BucketKeyEnabled;
        }
        return request;
    }

    /// <summary>
    /// Copies an object too large for one <c>CopyObject</c> (over 5 GiB) part by part, every part pinned to the
    /// source version, several parts at a time. The object's headers, user metadata, tags, storage class, and
    /// SSE-S3/SSE-KMS settings are set when the upload starts; a failed part aborts the upload.
    /// </summary>
    private async Task CopyMultipartAsync(
        string sourceKey,
        string destinationKey,
        GetObjectMetadataResponse head,
        string? sourceVersionId,
        bool sendCreateOnly,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        var size = head.ContentLength;
        var initiate = new InitiateMultipartUploadRequest
        {
            BucketName = _bucket,
            Key = destinationKey,
            ContentType = head.Headers.ContentType,
            WebsiteRedirectLocation = head.WebsiteRedirectLocation
        };
        initiate.Headers.CacheControl = head.Headers.CacheControl;
        initiate.Headers.ContentDisposition = head.Headers.ContentDisposition;
        initiate.Headers.ContentEncoding = head.Headers.ContentEncoding;
        initiate.Headers.ContentLanguage = head.Headers.ContentLanguage;
        if (!string.IsNullOrEmpty(head.ExpiresString)) initiate.Headers["Expires"] = head.ExpiresString;
        foreach (var (name, value) in UserMetadata(head.Metadata))
            initiate.Metadata[name] = value;
        if (IsNonStandard(head.StorageClass)) initiate.StorageClass = head.StorageClass;
        if (IsEncrypted(head.ServerSideEncryptionMethod))
        {
            initiate.ServerSideEncryptionMethod = head.ServerSideEncryptionMethod;
            initiate.ServerSideEncryptionKeyManagementServiceKeyId = head.ServerSideEncryptionKeyManagementServiceKeyId;
            initiate.BucketKeyEnabled = head.BucketKeyEnabled;
        }
        if (head.TagsCount is > 0)
        {
            var tags = await _client.GetObjectTaggingAsync(new GetObjectTaggingRequest
            {
                BucketName = _bucket,
                Key = sourceKey,
                VersionId = sourceVersionId
            }, cancellationToken).ConfigureAwait(false);
            initiate.TagSet = tags.Tagging;
        }
        // Not cancelled half-way: an upload the server created but whose id never came back could not be aborted.
        var initiated = await _client.InitiateMultipartUploadAsync(initiate, CancellationToken.None).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var partSize = MultipartCopyPartSize(size, _multipartPartSizeBytes, Math.Min(MultipartCopyMinPartBytes, MultipartCopyThresholdBytes));
            var count = checked((int)((size + partSize - 1) / partSize));
            var parts = new PartETag[count];
            Exception? failure = null;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var slots = new SemaphoreSlim(MultipartCopyConcurrency);
            var running = new List<Task>(count);
            async Task CopyPartAsync(int number, long first, long last)
            {
                try
                {
                    var copied = await _client.CopyPartAsync(new CopyPartRequest
                    {
                        SourceBucket = _bucket,
                        SourceKey = sourceKey,
                        SourceVersionId = sourceVersionId,
                        ETagToMatch = [head.ETag],
                        DestinationBucket = _bucket,
                        DestinationKey = destinationKey,
                        UploadId = initiated.UploadId,
                        PartNumber = number,
                        FirstByte = first,
                        LastByte = last
                    }, stop.Token).ConfigureAwait(false);
                    parts[number - 1] = new PartETag(number, copied.ETag);
                }
                catch (Exception error) when (!(error is OperationCanceledException && stop.IsCancellationRequested))
                {
                    Interlocked.CompareExchange(ref failure, error, null);
                    await stop.CancelAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Another part failed, or the caller cancelled.
                }
                finally { slots.Release(); }
            }
            try
            {
                for (var index = 0; index < count; index++)
                {
                    await slots.WaitAsync(stop.Token).ConfigureAwait(false);
                    var first = index * partSize;
                    running.Add(CopyPartAsync(index + 1, first, Math.Min(first + partSize, size) - 1));
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // A part failed or the caller cancelled; the parts already running finish first.
            }
            await Task.WhenAll(running).ConfigureAwait(false);
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            cancellationToken.ThrowIfCancellationRequested();

            var complete = new CompleteMultipartUploadRequest
            {
                BucketName = _bucket,
                Key = destinationKey,
                UploadId = initiated.UploadId,
                PartETags = [.. parts]
            };
            if (sendCreateOnly) complete.IfNoneMatch = "*";
            else if (ifMatch is not null) complete.IfMatch = ifMatch;
            // Once sent, the completion is not cancelled: it may commit with its reply lost to the cancel.
            await CompleteMultipartAsync(complete).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = _bucket, Key = destinationKey, UploadId = initiated.UploadId }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception) { /* The bucket's lifecycle rules remove abandoned uploads. */ }
            throw;
        }
    }

    /// <summary>
    /// The part size for copying <paramref name="size"/> bytes: the larger of the preferred size and
    /// <paramref name="minimum"/>, larger still when needed to stay within 10,000 parts, and never above S3's 5 GiB part limit.
    /// </summary>
    internal static long MultipartCopyPartSize(long size, long preferred, long minimum = 128L * 1024 * 1024) =>
        Math.Min(5L * 1024 * 1024 * 1024, Math.Max(Math.Max(preferred, minimum), (size + 9_999) / 10_000));

    /// <summary>
    /// Completes a multipart upload, never cancelled once sent. When the reply to a completion that did commit is
    /// lost, the SDK's retry is refused (the upload is gone, or a condition no longer holds against our own object);
    /// the object is then read back, and if it carries the multipart ETag these parts produce, the upload is ours and
    /// succeeded. In a versioned bucket another writer may already have put a newer version on top; the key's versions
    /// are then searched for ours, so its version is reported rather than a failure.
    /// </summary>
    private async Task<CompleteMultipartUploadResponse> CompleteMultipartAsync(CompleteMultipartUploadRequest request)
    {
        try
        {
            return await _client.CompleteMultipartUploadAsync(request, CancellationToken.None).ConfigureAwait(false);
        }
        catch (AmazonS3Exception error) when (error.ErrorCode == "NoSuchUpload" || error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
        {
            var expected = MultipartETag(request.PartETags.Select(part => part.ETag));
            if (expected is null) throw;
            var current = await HeadAsync(request.Key, CancellationToken.None).ConfigureAwait(false);
            if (current is not null && string.Equals(current.ETag?.Trim('"'), expected, StringComparison.OrdinalIgnoreCase))
                return new CompleteMultipartUploadResponse { BucketName = request.BucketName, Key = request.Key, ETag = current.ETag, VersionId = current.VersionId };
            var version = await FindVersionAsync(request.Key, expected).ConfigureAwait(false);
            if (version is null) throw;
            return new CompleteMultipartUploadResponse { BucketName = request.BucketName, Key = request.Key, ETag = version.ETag, VersionId = version.VersionId };
        }
    }

    /// <summary>
    /// A recent version of <paramref name="key"/> with the given ETag, or null when there is none or the bucket
    /// cannot list versions (unversioned buckets list only the current object, which was already compared).
    /// </summary>
    private async Task<S3ObjectVersion?> FindVersionAsync(string key, string etag)
    {
        try
        {
            var versions = await _client.ListVersionsAsync(new ListVersionsRequest { BucketName = _bucket, Prefix = key, MaxKeys = 100 }, CancellationToken.None).ConfigureAwait(false);
            return (versions.Versions ?? []).FirstOrDefault(version =>
                version.Key == key && version.IsDeleteMarker != true && !string.IsNullOrEmpty(version.VersionId) && version.VersionId != "null" &&
                string.Equals(version.ETag?.Trim('"'), etag, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception) { return null; }
    }

    /// <summary>The ETag S3 gives a multipart upload of these parts (MD5 of the part MD5s, then the part count), or null when a part ETag is not an MD5.</summary>
    internal static string? MultipartETag(IEnumerable<string?> partETags)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var count = 0;
        foreach (var etag in partETags)
        {
            var hex = etag?.Trim().Trim('"');
            if (hex is not { Length: 32 } || !hex.All(Uri.IsHexDigit))
                return null;
            md5.AppendData(Convert.FromHexString(hex));
            count++;
        }
        return count == 0 ? null : $"{Convert.ToHexStringLower(md5.GetHashAndReset())}-{count.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Reads an object's headers, or null when it does not exist.</summary>
    private async Task<GetObjectMetadataResponse?> HeadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucket, Key = key }, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception error) when (IsNotFound(error))
        {
            return null;
        }
    }
}
