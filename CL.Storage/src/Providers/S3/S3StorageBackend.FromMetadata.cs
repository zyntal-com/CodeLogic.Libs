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

    private static StorageItem FromMetadata(string path, GetObjectMetadataResponse response) => new()
    {
        Path = path,
        Name = NameOf(path),
        ItemType = StorageItemType.File,
        Size = response.ContentLength,
        LastModified = response.LastModified.HasValue ? new DateTimeOffset(response.LastModified.Value) : null,
        ContentType = response.Headers.ContentType,
        ETag = response.ETag?.Trim('"'),
        VersionId = response.VersionId,
        Metadata = UserMetadata(response.Metadata)
    };

    private static StorageItem FromListedObject(string path, S3Object item) => new()
    {
        Path = path,
        Name = NameOf(path),
        ItemType = StorageItemType.File,
        Size = item.Size,
        LastModified = item.LastModified.HasValue ? new DateTimeOffset(item.LastModified.Value) : null,
        ETag = item.ETag?.Trim('"')
    };

    /// <inheritdoc />
    /// <remarks>
    /// SHA-256 comes from a stored <c>x-amz-checksum-sha256</c>. MD5 comes from the ETag, which equals
    /// the content MD5 only for single-part uploads without SSE-KMS, so other ETags are not reported.
    /// </remarks>
    public async Task<Result<StorageChecksum>> GetServerChecksumAsync(string path, StorageChecksumAlgorithm algorithm, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(path);
        if (normalized.IsFailure) return Result<StorageChecksum>.Failure(normalized.Error!);
        if (algorithm is not (StorageChecksumAlgorithm.Md5 or StorageChecksumAlgorithm.Sha256))
            return ProviderChecksums.Unavailable(algorithm, $"S3 does not store {algorithm} checksums.");
        try
        {
            var response = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _bucket,
                Key = ToKey(normalized.Value!),
                ChecksumMode = ChecksumMode.ENABLED
            }, cancellationToken).ConfigureAwait(false);
            if (algorithm == StorageChecksumAlgorithm.Sha256)
                return ProviderChecksums.FromBase64(algorithm, response.ChecksumSHA256);
            var encryption = response.ServerSideEncryptionMethod?.Value ?? string.Empty;
            if (encryption.StartsWith("aws:kms", StringComparison.Ordinal))
                return ProviderChecksums.Unavailable(algorithm, "An SSE-KMS object's ETag is not its MD5.");
            // SSE-C: the ETag is derived from the encrypted content, not the plaintext. (Servers that answer a HEAD
            // without the customer key say so here; AWS refuses that HEAD with 400, handled below.)
            if (response.ServerSideEncryptionCustomerMethod is { Value.Length: > 0 } customer && customer != ServerSideEncryptionCustomerMethod.None)
                return ProviderChecksums.Unavailable(algorithm, "An SSE-C object's ETag is not its MD5.");
            return ProviderChecksums.FromHex(algorithm, response.ETag);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.BadRequest)
        {
            // AWS answers a HEAD of an SSE-C object without the customer key with 400 (a HEAD has no error body to
            // say more). Such an object's ETag is not its MD5 anyway, and this connection cannot send the key.
            return ProviderChecksums.Unavailable(algorithm,
                "S3 refused to read the object's headers (400): it is probably encrypted with a customer key (SSE-C), whose ETag is not its MD5.");
        }
        catch (Exception error) { return Result<StorageChecksum>.Failure(Map(error, "Get S3 checksum")); }
    }

    private static bool IsNotFound(AmazonS3Exception error) => error.StatusCode == HttpStatusCode.NotFound ||
        error.ErrorCode is "NoSuchKey" or "NoSuchBucket" or "NotFound";

    /// <summary>Maps an S3 SDK or transport exception to a storage error (internal for tests).</summary>
    internal static Error Map(Exception exception, string operation)
    {
        if (exception is StorageMultipartLimitException)
            return StorageErrors.TooLarge($"{operation}: the multipart upload exceeds 10,000 parts.");
        if (exception is AmazonS3Exception s3)
        {
            var details = ProviderErrorMapper.Details(
                StorageErrorInfo.HttpStatusKey, ((int)s3.StatusCode).ToString(CultureInfo.InvariantCulture), s3.ErrorCode);
            if (!string.IsNullOrWhiteSpace(s3.RequestId))
                details += ";requestId=" + ProviderErrorMapper.SafeDetailValue(s3.RequestId);
            if (IsNotFound(s3)) return StorageErrors.NotFound($"{operation}: item was not found.", details);
            if (s3.ErrorCode is "InvalidAccessKeyId" or "SignatureDoesNotMatch" or "ExpiredToken" or "InvalidToken")
                return StorageErrors.AuthenticationFailed($"{operation}: S3 rejected the credentials.", details);
            if (s3.ErrorCode is "AccessDenied" or "AllAccessDisabled")
                return StorageErrors.PermissionDenied($"{operation}: access was denied.", details);
            if (s3.ErrorCode is "SlowDown" or "RequestLimitExceeded" or "ServiceUnavailable")
                return StorageErrors.ServerBusy($"{operation}: S3 is throttling requests.", details);
            if (s3.ErrorCode is "QuotaExceeded")
                return StorageErrors.QuotaExceeded($"{operation}: the S3 storage quota was exceeded.", details);
            if (s3.ErrorCode is "EntityTooLarge")
                return StorageErrors.TooLarge($"{operation}: S3 rejected the object size.", details);
            if ((int)s3.StatusCode > 0)
                return ProviderErrorMapper.FromHttpStatus((int)s3.StatusCode, operation, "S3", providerCode: s3.ErrorCode);
            return StorageErrors.ProviderError($"{operation}: S3 request failed.", details);
        }
        if (ProviderErrorMapper.FromTransport(exception, operation, "S3") is { } transport)
            return transport;
        // The SDK refused the request before sending it (configuration, signing, checksums). Its messages are
        // written by the SDK, not the server, and say what to fix; keep a sanitized copy.
        if (exception is AmazonClientException client && ProviderErrorMapper.SafeProviderMessage(client.Message) is { Length: > 0 } reason)
            return StorageErrors.ProviderError($"{operation}: the S3 client refused the request: {reason}", ProviderErrorMapper.ExceptionDetails(exception));
        return StorageErrors.ProviderError($"{operation}: S3 provider failed.", ProviderErrorMapper.ExceptionDetails(exception));
    }

    private static string EncodeVersionContinuation(S3VersionContinuation continuation) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(continuation)));

    private static Result<S3VersionContinuation> DecodeVersionContinuation(string? continuationToken)
    {
        if (string.IsNullOrEmpty(continuationToken))
            return Result<S3VersionContinuation>.Success(new S3VersionContinuation(null, null));
        try
        {
            var value = JsonSerializer.Deserialize<S3VersionContinuation>(
                Encoding.UTF8.GetString(Convert.FromBase64String(continuationToken)));
            return value is null
                ? Result<S3VersionContinuation>.Failure(StorageErrors.InvalidPath(
                    "The S3 version continuation token is invalid."))
                : Result<S3VersionContinuation>.Success(value);
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            return Result<S3VersionContinuation>.Failure(StorageErrors.InvalidPath(
                "The S3 version continuation token is invalid."));
        }
    }

    private sealed record S3UploadCompletion(string? ETag, string? VersionId, long? Bytes);
    /// <summary>The source version a copy was pinned to.</summary>
    private sealed record S3PinnedSource(string? ETag, string? VersionId);
    /// <summary>What a connection's server does with upload, copy, and delete conditions.</summary>
    private sealed record S3ConditionProbe(
        S3ConditionState PutCreateOnly,
        S3ConditionState PutMatch,
        S3ConditionState CopyCreateOnly,
        S3ConditionState CopyMatch,
        S3ConditionState DeleteMatch,
        bool Conclusive)
    {
        /// <summary>For a probe that could not finish: when the next may run.</summary>
        public DateTimeOffset RetryAt { get; init; }

        public S3ConditionState StateOf(S3ProbedCondition condition) => condition switch
        {
            S3ProbedCondition.PutCreateOnly => PutCreateOnly,
            S3ProbedCondition.PutMatch => PutMatch,
            S3ProbedCondition.CopyCreateOnly => CopyCreateOnly,
            S3ProbedCondition.CopyMatch => CopyMatch,
            _ => DeleteMatch
        };

        /// <summary>The capabilities with only the conditional flags this server enforces on every request that commits.</summary>
        public StorageCapabilities Capabilities(StorageCapabilities all)
        {
            var features = all.Features;
            if (PutCreateOnly != S3ConditionState.Enforced || CopyCreateOnly != S3ConditionState.Enforced) features &= ~StorageFeature.ConditionalCreate;
            if (PutMatch != S3ConditionState.Enforced || CopyMatch != S3ConditionState.Enforced) features &= ~StorageFeature.ConditionalUpdate;
            if (DeleteMatch != S3ConditionState.Enforced) features &= ~StorageFeature.ConditionalDelete;
            return features == all.Features ? all : new StorageCapabilities(features, all.Limits);
        }
    }
    private enum S3ProbedCondition { PutCreateOnly, PutMatch, CopyCreateOnly, CopyMatch, DeleteMatch }
    private enum S3ConditionState { Enforced, Ignored, Rejected, Unknown }
    private sealed record S3VersionContinuation(string? KeyMarker, string? VersionIdMarker);
    private readonly record struct S3PartRead(int Count, bool EndOfStream);
    private sealed class StorageMultipartLimitException : Exception { }
}
