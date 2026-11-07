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

    /// <inheritdoc />
    public async Task<Result<StorageSignedUrl>> CreateSignedUrlAsync(
        string path,
        StorageSignedUrlOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageSignedUrlOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StorageSignedUrl>.Failure(validation.Error!);
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageSignedUrl>.Failure(normalized.Error!);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var expiresAt = DateTimeOffset.UtcNow.Add(options.ExpiresIn);
            var request = new GetPreSignedUrlRequest
            {
                BucketName = _bucket,
                Key = ToKey(normalized.Value!),
                Expires = expiresAt.UtcDateTime,
                Verb = options.Method == StorageSignedUrlMethod.Read ? HttpVerb.GET : HttpVerb.PUT,
                ContentType = options.ContentType,
                VersionId = options.VersionId
            };
            var value = await _client.GetPreSignedURLAsync(request).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!Uri.TryCreate(value, UriKind.Absolute, out var url))
                return Result<StorageSignedUrl>.Failure(StorageErrors.ProviderError(
                    "The S3 provider returned an invalid signed URL."));
            return Result<StorageSignedUrl>.Success(new StorageSignedUrl(url, options.Method, expiresAt));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageSignedUrl>.Failure(Map(error, "Create S3 signed URL")); }
    }

    /// <inheritdoc />
    public bool TryGetNativeClient<TClient>([NotNullWhen(true)] out TClient? client) where TClient : class
    {
        client = _client as TClient;
        return client is not null;
    }

    /// <inheritdoc />
    public Task<Result<NativeConnectionLease<TClient>>> OpenNativeConnectionAsync<TClient>(CancellationToken cancellationToken = default) where TClient : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_client is not TClient typed)
            return Task.FromResult(Result<NativeConnectionLease<TClient>>.Failure(StorageErrors.Unsupported($"S3 does not expose native type '{typeof(TClient).FullName}'.")));
        return Task.FromResult(Result<NativeConnectionLease<TClient>>.Success(new NativeConnectionLease<TClient>(typed, _ => ValueTask.CompletedTask)));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _probeGate.Dispose();
            if (_ownsClient) _client.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private async Task<Result<StorageItem>> GetDirectoryInfoAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = ToDirectoryPrefix(path),
                MaxKeys = 1
            }, cancellationToken).ConfigureAwait(false);
            return (response.KeyCount ?? 0) > 0
                ? Result<StorageItem>.Success(DirectoryItem(path))
                : Result<StorageItem>.Failure(StorageErrors.NotFound($"S3 item '{path}' was not found."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Get S3 directory info")); }
    }

    private PutObjectRequest NewPutRequest(string key, Stream source, string? contentType, IReadOnlyDictionary<string, string>? metadata)
    {
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = source,
            AutoCloseStream = false,
            AutoResetStreamPosition = false,
            DisablePayloadSigning = _disablePayloadSigning,
            DisableDefaultChecksumValidation = _disableChecksumValidation
        };
        if (!string.IsNullOrWhiteSpace(contentType)) request.ContentType = contentType;
        if (metadata is not null)
            foreach (var (name, value) in metadata) request.Metadata[name] = value;
        return request;
    }

    private async Task<Result<string?>> ResolveUploadIfMatchAsync(
        string key,
        StorageMutationCondition? condition,
        CancellationToken cancellationToken)
    {
        if (condition is null or { IsEmpty: true })
            return Result<string?>.Success(null);
        if (condition.ExpectedVersionId is null)
            return Result<string?>.Success(condition.ExpectedETag);
        try
        {
            var current = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _bucket,
                Key = key
            }, cancellationToken).ConfigureAwait(false);
            var item = FromMetadata(FromKey(key) ?? string.Empty, current);
            var validation = ValidateCurrentCondition(item, condition, "S3 object");
            return validation.IsFailure
                ? Result<string?>.Failure(validation.Error!)
                : Result<string?>.Success(condition.ExpectedETag ?? current.ETag);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AmazonS3Exception error) when (IsNotFound(error))
        {
            return Result<string?>.Failure(StorageErrors.Conflict(
                "The S3 object no longer exists for the requested upload condition."));
        }
        catch (Exception error)
        {
            return Result<string?>.Failure(Map(error, "Resolve S3 upload condition"));
        }
    }

    /// <summary>
    /// Checks a write's condition just before it, for a server that does not enforce conditional headers:
    /// a create-only write needs the key absent, a conditional replace needs its ETag or version.
    /// </summary>
    private async Task<Result> CheckBeforeWriteAsync(string key, StorageMutationCondition? condition, bool createOnly, CancellationToken cancellationToken)
    {
        if (!createOnly && condition is null or { IsEmpty: true })
            return Result.Success();
        try
        {
            var current = await HeadAsync(key, cancellationToken).ConfigureAwait(false);
            if (createOnly)
                return current is null ? Result.Success() : Result.Failure(StorageErrors.Conflict("The S3 destination already exists."));
            if (current is null)
                return Result.Failure(StorageErrors.Conflict("The S3 object no longer exists for the requested upload condition."));
            return ValidateCurrentCondition(FromMetadata(FromKey(key) ?? string.Empty, current), condition, "S3 object");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Check S3 write condition")); }
    }

    private static Result ValidateCurrentCondition(
        StorageItem current,
        StorageMutationCondition? condition,
        string itemName)
    {
        if (condition is null or { IsEmpty: true })
            return Result.Success();
        if (condition.ExpectedETag is not null &&
            !string.Equals(
                condition.ExpectedETag.Trim('"'),
                current.ETag?.Trim('"'),
                StringComparison.Ordinal))
        {
            return Result.Failure(StorageErrors.Conflict(
                $"The {itemName} ETag no longer matches the requested condition."));
        }
        return condition.ExpectedVersionId is not null &&
               !string.Equals(condition.ExpectedVersionId, current.VersionId, StringComparison.Ordinal)
            ? Result.Failure(StorageErrors.Conflict(
                $"The {itemName} version no longer matches the requested condition."))
            : Result.Success();
    }
}
