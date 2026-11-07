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
    public async Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var exists = await _container.ExistsAsync(cancellationToken).ConfigureAwait(false);
            return exists.Value ? Result.Success() : Result.Failure(StorageErrors.NotFound("The Azure blob container was not found."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Check Azure blob health")); }
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
        if (StorageOptionValidation.MetadataSizeBytes(metadata) > 8 * 1024)
            return Result<StorageItem>.Failure(StorageErrors.TooLarge("Azure Blob metadata exceeds the 8 KiB limit."));
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        var snapshot = StorageMetadataSnapshot.Create(metadata);
        try
        {
            var blob = Blob(normalized.Value!);
            var current = (await blob.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
            if (options.ExpectedVersionId is not null &&
                !string.Equals(options.ExpectedVersionId, current.VersionId, StringComparison.Ordinal))
            {
                return Result<StorageItem>.Failure(StorageErrors.Conflict(
                    "The Azure blob version no longer matches the metadata update condition."));
            }
            var values = options.Mode == StorageMetadataUpdateMode.Merge
                ? new Dictionary<string, string>(current.Metadata, StringComparer.Ordinal)
                : [with(StringComparer.Ordinal)];
            foreach (var (name, value) in snapshot)
                values[name] = value;
            var expected = options.ExpectedETag is not null
                ? new ETag(options.ExpectedETag)
                : options.ExpectedVersionId is not null ? current.ETag : (ETag?)null;
            await blob.SetMetadataAsync(
                values,
                expected.HasValue ? new BlobRequestConditions { IfMatch = expected.Value } : null,
                cancellationToken).ConfigureAwait(false);
            return await GetInfoAsync(normalized.Value!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (RequestFailedException error) when (error.Status is 409 or 412)
        {
            return Result<StorageItem>.Failure(StorageErrors.Conflict(
                "The Azure blob changed before its metadata could be updated."));
        }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Update Azure blob metadata")); }
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
            var response = await Blob(normalized.Value!).GetTagsAsync(
                conditions: null,
                cancellationToken).ConfigureAwait(false);
            return Result<IReadOnlyDictionary<string, string>>.Success(
                StorageMetadataSnapshot.Create(response.Value.Tags));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            return Result<IReadOnlyDictionary<string, string>>.Failure(Map(error, "Read Azure blob tags"));
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
            var blob = Blob(normalized.Value!);
            if (options.Mode == StorageTagUpdateMode.Merge)
            {
                var current = await blob.GetTagsAsync(
                    conditions: null,
                    cancellationToken).ConfigureAwait(false);
                foreach (var (name, value) in current.Value.Tags)
                    values[name] = value;
            }
            foreach (var (name, value) in tags)
                values[name] = value;
            validation = options.Validate(values);
            if (validation.IsFailure) return Result<StorageItem>.Failure(validation.Error!);

            await blob.SetTagsAsync(values, conditions: null, cancellationToken).ConfigureAwait(false);
            return await GetInfoAsync(normalized.Value!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Update Azure blob tags")); }
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
        var key = ToKey(normalized.Value!);
        var states = options.IncludeDeleteMarkers
            ? BlobStates.Version | BlobStates.DeletedWithVersions
            : BlobStates.Version;
        try
        {
            await foreach (var page in _container.GetBlobsAsync(
                BlobTraits.None,
                states,
                key,
                cancellationToken).AsPages(
                    options.ContinuationToken,
                    Math.Min(options.PageSize, 5_000)).ConfigureAwait(false))
            {
                var versions = new List<StorageVersion>();
                foreach (var version in page.Values)
                {
                    if (!string.Equals(version.Name, key, StringComparison.Ordinal))
                        continue;
                    if (string.IsNullOrWhiteSpace(version.VersionId))
                        return Result<StorageVersionPage>.Failure(StorageErrors.ProviderError(
                            "Azure Blob returned a version without a version identifier."));
                    versions.Add(new StorageVersion
                    {
                        Path = normalized.Value!,
                        VersionId = version.VersionId,
                        ETag = version.Properties.ETag?.ToString(),
                        Size = version.Properties.ContentLength,
                        LastModified = version.Properties.LastModified,
                        IsLatest = version.IsLatestVersion == true,
                        IsDeleteMarker = version.Deleted == true
                    });
                }
                return Result<StorageVersionPage>.Success(new StorageVersionPage(
                    versions.AsReadOnly(),
                    page.ContinuationToken));
            }
            return Result<StorageVersionPage>.Success(new StorageVersionPage([], null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageVersionPage>.Failure(Map(error, "List Azure blob versions")); }
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
            var deleted = await Blob(normalized.Value!).WithVersion(versionId).DeleteIfExistsAsync(
                DeleteSnapshotsOption.IncludeSnapshots,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return deleted.Value
                ? Result.Success()
                : Result.Failure(StorageErrors.NotFound("The Azure blob version was not found."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Delete Azure blob version")); }
    }

    /// <inheritdoc />
    public Task<Result<StorageSignedUrl>> CreateSignedUrlAsync(
        string path,
        StorageSignedUrlOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageSignedUrlOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
            return Task.FromResult(Result<StorageSignedUrl>.Failure(validation.Error!));
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure)
            return Task.FromResult(Result<StorageSignedUrl>.Failure(normalized.Error!));
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var blob = Blob(normalized.Value!);
            if (options.VersionId is not null)
                blob = blob.WithVersion(options.VersionId);
            if (!blob.CanGenerateSasUri)
                return Task.FromResult(Result<StorageSignedUrl>.Failure(StorageErrors.Unsupported(
                    "The Azure credential cannot generate SAS URLs; use a shared-key credential or native user-delegation SAS.")));
            var expiresAt = DateTimeOffset.UtcNow.Add(options.ExpiresIn);
            var permissions = options.Method == StorageSignedUrlMethod.Read
                ? BlobSasPermissions.Read
                : BlobSasPermissions.Create | BlobSasPermissions.Write;
            var url = blob.GenerateSasUri(permissions, expiresAt);
            return Task.FromResult(Result<StorageSignedUrl>.Success(
                new StorageSignedUrl(url, options.Method, expiresAt)));
        }
        catch (Exception error)
        {
            return Task.FromResult(Result<StorageSignedUrl>.Failure(Map(error, "Create Azure blob signed URL")));
        }
    }
}
