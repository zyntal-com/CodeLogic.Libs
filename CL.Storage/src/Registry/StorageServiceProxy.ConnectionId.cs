using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CodeLogic.Core.Events;
using CodeLogic.Core.Results;
using System.Runtime.CompilerServices;

namespace CL.Storage.Registry;

internal sealed partial class StorageServiceProxy
{

    public string ConnectionId => Read(backend => backend.ConnectionId);
    public StorageProvider Provider => Read(backend => backend.Provider);
    public string Root => Read(backend => backend.Root);
    public StorageCapabilities Capabilities => Read(backend => backend.Capabilities);

    public Task<Result<StorageItem>> GetInfoAsync(string path, CancellationToken cancellationToken = default) =>
        InvokePathAsync(path, cancellationToken,
            (backend, normalized) => backend.GetInfoAsync(normalized, cancellationToken));

    public Task<Result<bool>> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
        InvokePathAsync(path, cancellationToken,
            (backend, normalized) => backend.ExistsAsync(normalized, cancellationToken));

    public Task<Result<StoragePage>> ListAsync(string path, StorageListOptions? options = null, CancellationToken cancellationToken = default) =>
        InvokePathAsync(path, cancellationToken, options?.Validate() ?? Result.Success(),
            (backend, normalized) => backend.ListAsync(normalized, options, cancellationToken));

    public Task<Result> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        InvokePathAsync(path, cancellationToken,
            (backend, normalized) => backend.CreateDirectoryAsync(normalized, cancellationToken));

    public Task<Result<StorageItem>> UploadAsync(string path, Stream source, StorageUploadOptions? options = null, CancellationToken cancellationToken = default) =>
        UploadWithConflictPolicyAsync(path, options, source.CanSeek ? source.Length - source.Position : null, cancellationToken,
            (backend, normalized, resolved) => backend.UploadAsync(normalized, source, resolved, cancellationToken));

    public Task<Result<StorageItem>> UploadBytesAsync(string path, byte[] content, StorageUploadOptions? options = null, CancellationToken cancellationToken = default) =>
        UploadWithConflictPolicyAsync(path, options, content.LongLength, cancellationToken,
            (backend, normalized, resolved) => backend.UploadBytesAsync(normalized, content, resolved, cancellationToken));

    /// <summary>
    /// Resolves a conditional <see cref="StorageUploadOptions.ConflictPolicy"/> before uploading. A skipped
    /// upload succeeds with the existing destination item and publishes no write event.
    /// </summary>
    private async Task<Result<StorageItem>> UploadWithConflictPolicyAsync(
        string path,
        StorageUploadOptions? options,
        long? sourceLength,
        CancellationToken cancellationToken,
        Func<IStorageBackend, string, StorageUploadOptions?, Task<Result<StorageItem>>> upload,
        [CallerMemberName] string caller = "")
    {
        var validation = options?.Validate() ?? Result.Success();
        if (options?.ConflictPolicy == StorageConflictPolicy.Resume && validation.IsSuccess)
            return await UploadPathWithEventAsync(path, validation, cancellationToken,
                (backend, normalized) => upload(backend, normalized, options), caller).ConfigureAwait(false);
        if (validation.IsFailure || options?.ConflictPolicy is null)
            return await UploadPathWithEventAsync(path, validation, cancellationToken,
                (backend, normalized) => upload(backend, normalized, options), caller).ConfigureAwait(false);

        var normalizedPath = StoragePath.Normalize(path);
        if (normalizedPath.IsFailure)
            return Result<StorageItem>.Failure(normalizedPath.Error!);
        var decision = await StorageConflictResolver.ResolveAsync(
            this,
            normalizedPath.Value!,
            options.ConflictPolicy,
            options.Overwrite,
            sourceLength,
            options.SourceLastModified,
            cancellationToken).ConfigureAwait(false);
        if (decision.IsFailure)
            return Result<StorageItem>.Failure(decision.Error!);
        if (decision.Value.Skip)
            return Result<StorageItem>.Success(decision.Value.Existing!);
        var resolved = options with { Overwrite = decision.Value.Overwrite, ConflictPolicy = null };
        return await UploadPathWithEventAsync(decision.Value.Path, Result.Success(), cancellationToken,
            (backend, normalized) => upload(backend, normalized, resolved), caller).ConfigureAwait(false);
    }

    public Task<Result<Stream>> DownloadAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default) =>
        DownloadWithLeaseAsync(path, options, cancellationToken);

    public Task<Result<byte[]>> DownloadBytesAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default) =>
        InvokePathAsync(path, cancellationToken, options?.Validate() ?? Result.Success(),
            (backend, normalized) => backend.DownloadBytesAsync(normalized, options, cancellationToken));

    public Task<Result> DeleteAsync(string path, StorageDeleteOptions? options = null, CancellationToken cancellationToken = default) =>
        MutatePathWithEventAsync(
            path,
            options?.Validate() ?? Result.Success(),
            cancellationToken,
            (backend, normalized) => backend.DeleteAsync(normalized, options, cancellationToken),
            (connectionId, provider, normalized) => new StorageItemDeletedEvent(
                connectionId, provider, normalized, DateTimeOffset.UtcNow));

    public async Task<Result> CopyAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default) =>
        AsResult(await _library.CopyAsync(
            _connectionId,
            sourcePath,
            _connectionId,
            destinationPath,
            options,
            cancellationToken).ConfigureAwait(false), cancellationToken);

    public async Task<Result> MoveAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default) =>
        AsResult(await _library.MoveAsync(
            _connectionId,
            sourcePath,
            _connectionId,
            destinationPath,
            options,
            cancellationToken).ConfigureAwait(false), cancellationToken);

    /// <summary>
    /// A connection's copy and move behave like every other connection call: a clean cancel throws, as it does
    /// on a backend. A report that needs reconciliation stays a failure, since something was committed.
    /// </summary>
    private static Result AsResult(StorageTransferReport report, CancellationToken cancellationToken)
    {
        if (report.Outcome == StorageTransferOutcome.Cancelled)
            throw new OperationCanceledException(report.Error?.Message ?? "The transfer was cancelled.", cancellationToken);
        return report.ToResult();
    }

    public Task<Result<IReadOnlyDictionary<string, string>>> GetMetadataAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        InvokePathAsync(
            path,
            cancellationToken,
            (backend, normalized) => backend is IStorageMetadataService metadata
                ? metadata.GetMetadataAsync(normalized, cancellationToken)
                : Task.FromResult(Result<IReadOnlyDictionary<string, string>>.Failure(
                    StorageErrors.Unsupported("This storage connection does not support metadata operations."))));

    public Task<Result<StorageItem>> SetMetadataAsync(
        string path,
        IReadOnlyDictionary<string, string> metadata,
        StorageMetadataUpdateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        options ??= new StorageMetadataUpdateOptions();
        var validation = options.Validate(metadata);
        if (validation.IsFailure)
            return Task.FromResult(Result<StorageItem>.Failure(validation.Error!));
        var snapshot = StorageMetadataSnapshot.Create(metadata);
        return InvokePathAsync(
            path,
            cancellationToken,
            validation,
            (backend, normalized) => backend is IStorageMetadataService metadataService
                ? metadataService.SetMetadataAsync(normalized, snapshot, options, cancellationToken)
                : Task.FromResult(Result<StorageItem>.Failure(
                    StorageErrors.Unsupported("This storage connection does not support metadata updates."))));
    }

    public Task<Result<IReadOnlyDictionary<string, string>>> GetTagsAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        InvokePathAsync(
            path,
            cancellationToken,
            (backend, normalized) => backend is IStorageTagService tags
                ? tags.GetTagsAsync(normalized, cancellationToken)
                : Task.FromResult(Result<IReadOnlyDictionary<string, string>>.Failure(
                    StorageErrors.Unsupported("This storage connection does not support object tags."))));

    public Task<Result<StorageItem>> SetTagsAsync(
        string path,
        IReadOnlyDictionary<string, string> tags,
        StorageTagUpdateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        options ??= new StorageTagUpdateOptions();
        var validation = options.Validate(tags);
        if (validation.IsFailure)
            return Task.FromResult(Result<StorageItem>.Failure(validation.Error!));
        var snapshot = StorageMetadataSnapshot.Create(tags);
        return InvokePathAsync(
            path,
            cancellationToken,
            validation,
            (backend, normalized) => backend is IStorageTagService tagService
                ? tagService.SetTagsAsync(normalized, snapshot, options, cancellationToken)
                : Task.FromResult(Result<StorageItem>.Failure(
                    StorageErrors.Unsupported("This storage connection does not support object tag updates."))));
    }

    public Task<Result<StorageSignedUrl>> CreateSignedUrlAsync(
        string path,
        StorageSignedUrlOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new StorageSignedUrlOptions();
        var validation = options.Validate();
        return InvokePathAsync(
            path,
            cancellationToken,
            validation,
            (backend, normalized) => backend is IStorageSignedUrlService signedUrls
                ? signedUrls.CreateSignedUrlAsync(normalized, options, cancellationToken)
                : Task.FromResult(Result<StorageSignedUrl>.Failure(
                    StorageErrors.Unsupported("This storage connection does not support signed URLs."))));
    }

    public Task<Result<StorageVersionPage>> ListVersionsAsync(
        string path,
        StorageVersionListOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new StorageVersionListOptions();
        var validation = options.Validate();
        return InvokePathAsync(
            path,
            cancellationToken,
            validation,
            (backend, normalized) => backend is IStorageVersionService versions
                ? versions.ListVersionsAsync(normalized, options, cancellationToken)
                : Task.FromResult(Result<StorageVersionPage>.Failure(
                    StorageErrors.Unsupported("This storage connection does not support object versions."))));
    }

    public Task<Result> DeleteVersionAsync(
        string path,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var versionValidation = StorageOptionValidation.OptionalToken(versionId, nameof(versionId));
        if (versionValidation.IsFailure)
            return Task.FromResult(versionValidation);
        var normalized = StoragePath.Normalize(path);
        if (normalized.IsFailure)
            return Task.FromResult(Result.Failure(normalized.Error!));
        if (normalized.Value!.Length == 0)
            return Task.FromResult(Result.Failure(StorageErrors.InvalidPath(
                "A non-root version path is required.")));
        return InvokeAsync(backend => backend is IStorageVersionService versions
            ? versions.DeleteVersionAsync(normalized.Value!, versionId, cancellationToken)
            : Task.FromResult(Result.Failure(
                StorageErrors.Unsupported("This storage connection does not support object versions."))),
            normalized.Value!);
    }
}
