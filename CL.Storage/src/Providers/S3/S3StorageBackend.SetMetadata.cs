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
    public async Task<Result<StorageItem>> SetMetadataAsync(
        string path,
        IReadOnlyDictionary<string, string> metadata,
        StorageMetadataUpdateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        options ??= new StorageMetadataUpdateOptions();
        var validation = options.Validate(metadata);
        if (validation.IsFailure) return Result<StorageItem>.Failure(validation.Error!);
        if (StorageOptionValidation.MetadataSizeBytes(metadata) > 2 * 1024)
            return Result<StorageItem>.Failure(StorageErrors.TooLarge("S3 user metadata exceeds the 2 KiB limit."));
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        var snapshot = StorageMetadataSnapshot.Create(metadata);
        try
        {
            var key = ToKey(normalized.Value!);
            var current = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _bucket,
                Key = key
            }, cancellationToken).ConfigureAwait(false);
            if (options.ExpectedVersionId is not null &&
                !string.Equals(options.ExpectedVersionId, current.VersionId, StringComparison.Ordinal))
            {
                return Result<StorageItem>.Failure(StorageErrors.Conflict(
                    "The S3 object version no longer matches the metadata update condition."));
            }

            var values = options.Mode == StorageMetadataUpdateMode.Merge
                ? UserMetadata(current.Metadata)
                : [with(StringComparer.Ordinal)];
            foreach (var (name, value) in snapshot)
                values[name] = value;

            var request = new CopyObjectRequest
            {
                SourceBucket = _bucket,
                SourceKey = key,
                DestinationBucket = _bucket,
                DestinationKey = key,
                MetadataDirective = S3MetadataDirective.REPLACE,
                ContentType = current.Headers.ContentType,
                CacheControl = current.Headers.CacheControl,
                ContentDisposition = current.Headers.ContentDisposition,
                ContentEncoding = current.Headers.ContentEncoding,
                ContentLanguage = current.Headers.ContentLanguage,
                // The object is copied onto itself, so pinning the source (x-amz-copy-source-if-match, which every
                // S3-compatible server honours) pins the destination too; If-Match, which some servers ignore or
                // reject on CopyObject, is not needed.
                ETagToMatch = options.ExpectedETag ?? (options.ExpectedVersionId is null ? null : current.ETag)
            };
            foreach (var (name, value) in values)
                request.Metadata[name] = value;
            await _client.CopyObjectAsync(request, cancellationToken).ConfigureAwait(false);
            return await GetInfoAsync(normalized.Value!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AmazonS3Exception error) when (error.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            return Result<StorageItem>.Failure(StorageErrors.Conflict(
                "The S3 object changed before its metadata could be updated."));
        }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Update S3 metadata")); }
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyDictionary<string, string>>> GetTagsAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure)
            return Result<IReadOnlyDictionary<string, string>>.Failure(normalized.Error!);
        try
        {
            var response = await _client.GetObjectTaggingAsync(new GetObjectTaggingRequest
            {
                BucketName = _bucket,
                Key = ToKey(normalized.Value!)
            }, cancellationToken).ConfigureAwait(false);
            return Result<IReadOnlyDictionary<string, string>>.Success(
                StorageMetadataSnapshot.Create(
                    (response.Tagging ?? []).Select(tag =>
                        new KeyValuePair<string, string>(tag.Key, tag.Value))));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            return Result<IReadOnlyDictionary<string, string>>.Failure(Map(error, "Read S3 object tags"));
        }
    }

    /// <inheritdoc />
    public async Task<Result<StorageItem>> SetTagsAsync(
        string path,
        IReadOnlyDictionary<string, string> tags,
        StorageTagUpdateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        options ??= new StorageTagUpdateOptions();
        var validation = options.Validate(tags);
        if (validation.IsFailure) return Result<StorageItem>.Failure(validation.Error!);
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var key = ToKey(normalized.Value!);
            if (options.Mode == StorageTagUpdateMode.Merge)
            {
                var current = await _client.GetObjectTaggingAsync(new GetObjectTaggingRequest
                {
                    BucketName = _bucket,
                    Key = key
                }, cancellationToken).ConfigureAwait(false);
                foreach (var tag in current.Tagging ?? [])
                    values[tag.Key] = tag.Value;
            }
            foreach (var (name, value) in tags)
                values[name] = value;
            validation = options.Validate(values);
            if (validation.IsFailure) return Result<StorageItem>.Failure(validation.Error!);

            await _client.PutObjectTaggingAsync(new PutObjectTaggingRequest
            {
                BucketName = _bucket,
                Key = key,
                Tagging = new Tagging
                {
                    TagSet = values.Select(pair => new Amazon.S3.Model.Tag
                    {
                        Key = pair.Key,
                        Value = pair.Value
                    }).ToList()
                }
            }, cancellationToken).ConfigureAwait(false);
            return await GetInfoAsync(normalized.Value!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Update S3 object tags")); }
    }

    /// <inheritdoc />
    public async Task<Result<StorageVersionPage>> ListVersionsAsync(
        string path,
        StorageVersionListOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageVersionListOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StorageVersionPage>.Failure(validation.Error!);
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageVersionPage>.Failure(normalized.Error!);
        var continuation = DecodeVersionContinuation(options.ContinuationToken);
        if (continuation.IsFailure) return Result<StorageVersionPage>.Failure(continuation.Error!);
        var key = ToKey(normalized.Value!);

        try
        {
            var response = await _client.ListVersionsAsync(new ListVersionsRequest
            {
                BucketName = _bucket,
                Prefix = key,
                MaxKeys = Math.Min(options.PageSize, 1_000),
                KeyMarker = continuation.Value!.KeyMarker,
                VersionIdMarker = continuation.Value.VersionIdMarker
            }, cancellationToken).ConfigureAwait(false);
            var versions = new List<StorageVersion>();
            foreach (var version in response.Versions ?? [])
            {
                if (!string.Equals(version.Key, key, StringComparison.Ordinal) ||
                    (!options.IncludeDeleteMarkers && version.IsDeleteMarker == true))
                {
                    continue;
                }
                if (string.IsNullOrWhiteSpace(version.VersionId))
                    return Result<StorageVersionPage>.Failure(StorageErrors.ProviderError(
                        "S3 returned a version without a version identifier."));
                versions.Add(new StorageVersion
                {
                    Path = normalized.Value!,
                    VersionId = version.VersionId,
                    ETag = version.ETag?.Trim('"'),
                    Size = version.Size,
                    LastModified = version.LastModified.HasValue
                        ? new DateTimeOffset(version.LastModified.Value.ToUniversalTime())
                        : null,
                    IsLatest = version.IsLatest == true,
                    IsDeleteMarker = version.IsDeleteMarker == true
                });
            }

            string? next = null;
            if (response.IsTruncated == true)
            {
                if (string.IsNullOrEmpty(response.NextKeyMarker))
                    return Result<StorageVersionPage>.Failure(StorageErrors.ProviderError(
                        "S3 truncated a version page without returning a continuation marker."));
                next = EncodeVersionContinuation(new S3VersionContinuation(
                    response.NextKeyMarker,
                    response.NextVersionIdMarker));
            }
            return Result<StorageVersionPage>.Success(new StorageVersionPage(versions.AsReadOnly(), next));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageVersionPage>.Failure(Map(error, "List S3 object versions")); }
    }

    /// <inheritdoc />
    public async Task<Result> DeleteVersionAsync(
        string path,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var versionValidation = StorageOptionValidation.OptionalToken(versionId, nameof(versionId));
        if (versionValidation.IsFailure) return versionValidation;
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result.Failure(normalized.Error!);
        try
        {
            await _client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = _bucket,
                Key = ToKey(normalized.Value!),
                VersionId = versionId
            }, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Delete S3 object version")); }
    }
}
