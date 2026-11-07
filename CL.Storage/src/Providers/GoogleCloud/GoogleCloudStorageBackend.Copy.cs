using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;
using Google;
using Google.Cloud.Storage.V1;
using GcsObject = Google.Apis.Storage.v1.Data.Object;

namespace CL.Storage.Providers.GoogleCloud;

public sealed partial class GoogleCloudStorageBackend
{

    /// <inheritdoc />
    /// <remarks>
    /// An object is copied on the server pinned to the generation it read (<c>ifSourceGenerationMatch</c>, and
    /// <see cref="StorageTransferOptions.SourceVersionId"/> as the source generation when set); a create-only copy
    /// carries <c>ifGenerationMatch=0</c> and a <see cref="StorageTransferOptions.DestinationCondition"/> the
    /// destination's generation.
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

    /// <inheritdoc />
    /// <remarks>
    /// An object is copied as <see cref="CopyAsync"/> does, then deleted only while it is still the generation that
    /// was copied. Once the copy committed, a failure to delete the source returns <c>storage.partial_failure</c> with
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
        // Committed: the source goes only while it is the generation that was copied, and a cancel no longer applies.
        try
        {
            await _client.DeleteObjectAsync(
                _bucket,
                ToKey(source.Value!),
                new DeleteObjectOptions { IfGenerationMatch = ParseGeneration(info.Value.VersionId).Value },
                CancellationToken.None).ConfigureAwait(false);
            return Result.Success();
        }
        catch (GoogleApiException error) when (error.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return Result.Success();
        }
        catch (GoogleApiException error) when (error.HttpStatusCode == HttpStatusCode.PreconditionFailed)
        {
            return Result.Failure(ObjectStoreMoves.SourceKept("Google Cloud", source.Value!,
                StorageErrors.Conflict("The Google Cloud source changed after it was copied, so it was not deleted.")));
        }
        catch (Exception error)
        {
            return Result.Failure(ObjectStoreMoves.SourceKept("Google Cloud", source.Value!, Map(error, "Delete moved Google Cloud object")));
        }
    }

    /// <summary>A copy or move source: the object (at generation <paramref name="versionId"/> when set), or a directory.</summary>
    private async Task<Result<StorageItem>> SourceInfoAsync(string path, string? versionId, CancellationToken cancellationToken)
    {
        if (versionId is null)
            return await GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
        var generation = ParseGeneration(versionId);
        if (generation.IsFailure) return Result<StorageItem>.Failure(generation.Error!);
        try
        {
            var item = await _client.GetObjectAsync(_bucket, ToKey(path), new GetObjectOptions { Generation = generation.Value }, cancellationToken).ConfigureAwait(false);
            return Result<StorageItem>.Success(ToItem(path, item));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Get Google Cloud object generation")); }
    }

    /// <summary>Copies one object on the server, pinned to the generation <paramref name="source"/> describes.</summary>
    private async Task<Result> CopyFileAsync(string sourcePath, string destinationPath, StorageItem source, StorageTransferOptions options, CancellationToken cancellationToken)
    {
        if (options.ExpectedSourceETag is { } expected && !StagedWriter.SameETag(expected, source.ETag))
            return Result.Failure(StorageErrors.Conflict(
                $"The Google Cloud source '{sourcePath}' changed since it was read.",
                $"expectedETag={expected.Trim('"')};actualETag={source.ETag}"));
        var sourceGeneration = ParseGeneration(source.VersionId);
        if (sourceGeneration.IsFailure) return Result.Failure(sourceGeneration.Error!);
        try
        {
            long? destinationGeneration = null;
            if (!options.Overwrite)
            {
                destinationGeneration = 0;
            }
            else if (options.DestinationCondition is { IsEmpty: false } condition)
            {
                var current = await GetInfoAsync(destinationPath, cancellationToken).ConfigureAwait(false);
                if (current.IsFailure)
                    return Result.Failure(current.Error!.Code == StorageErrors.NotFoundCode
                        ? StorageErrors.Conflict("The Google Cloud destination no longer exists for the requested condition.")
                        : current.Error!);
                var check = ValidateCurrentCondition(current.Value!, condition, "Google Cloud object");
                if (check.IsFailure) return check;
                var generation = ParseGeneration(condition.ExpectedVersionId ?? current.Value!.VersionId);
                if (generation.IsFailure) return Result.Failure(generation.Error!);
                destinationGeneration = generation.Value;
            }
            await _client.CopyObjectAsync(
                _bucket,
                ToKey(sourcePath),
                _bucket,
                ToKey(destinationPath),
                new CopyObjectOptions
                {
                    SourceGeneration = options.SourceVersionId is null ? null : sourceGeneration.Value,
                    IfSourceGenerationMatch = sourceGeneration.Value,
                    IfGenerationMatch = destinationGeneration
                },
                cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (GoogleApiException error) when (error.HttpStatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            return Result.Failure(StorageErrors.Conflict(
                !options.Overwrite
                    ? "The Google Cloud destination already exists, or the source changed since it was read."
                    : "The Google Cloud source changed since it was read, or the destination no longer matches its condition.",
                ProviderErrorMapper.Details(StorageErrorInfo.HttpStatusKey, ((int)error.HttpStatusCode).ToString(CultureInfo.InvariantCulture))));
        }
        catch (Exception error) { return Result.Failure(Map(error, "Copy Google Cloud object")); }
    }

    /// <inheritdoc />
    public async Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.GetBucketAsync(_bucket, cancellationToken: cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Check Google Cloud Storage health")); }
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
            return Result<StorageItem>.Failure(StorageErrors.TooLarge("Google Cloud metadata exceeds the 8 KiB limit."));
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        var expectedGeneration = ParseGeneration(options.ExpectedVersionId);
        if (expectedGeneration.IsFailure) return Result<StorageItem>.Failure(expectedGeneration.Error!);
        var snapshot = StorageMetadataSnapshot.Create(metadata);
        try
        {
            var key = ToKey(normalized.Value!);
            var current = await _client.GetObjectAsync(
                _bucket,
                key,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (options.ExpectedETag is not null &&
                !string.Equals(options.ExpectedETag, current.ETag, StringComparison.Ordinal))
            {
                return Result<StorageItem>.Failure(StorageErrors.Conflict(
                    "The Google Cloud object ETag no longer matches the metadata update condition."));
            }
            if (expectedGeneration.Value.HasValue && current.Generation != expectedGeneration.Value)
            {
                return Result<StorageItem>.Failure(StorageErrors.Conflict(
                    "The Google Cloud object generation no longer matches the metadata update condition."));
            }

            var values = options.Mode == StorageMetadataUpdateMode.Merge && current.Metadata is not null
                ? new Dictionary<string, string>(current.Metadata, StringComparer.Ordinal)
                : [with(StringComparer.Ordinal)];
            foreach (var (name, value) in snapshot)
                values[name] = value;
            var patched = await _client.PatchObjectAsync(new GcsObject
            {
                Bucket = _bucket,
                Name = key,
                Metadata = values
            }, new PatchObjectOptions
            {
                IfGenerationMatch = expectedGeneration.Value,
                IfMetagenerationMatch = current.Metageneration
            }, cancellationToken).ConfigureAwait(false);
            return Result<StorageItem>.Success(ToItem(normalized.Value!, patched));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (GoogleApiException error) when (error.HttpStatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            return Result<StorageItem>.Failure(StorageErrors.Conflict(
                "The Google Cloud object changed before its metadata could be updated."));
        }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Update Google Cloud metadata")); }
    }
}
