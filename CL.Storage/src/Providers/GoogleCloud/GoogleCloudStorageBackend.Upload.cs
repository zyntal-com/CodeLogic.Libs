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
    public async Task<Result<StorageItem>> UploadAsync(string path, Stream source, StorageUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (StorageTransferPipeline.Applies(this, options))
            return await StorageTransferPipeline.UploadAsync(this, path, source, options, cancellationToken).ConfigureAwait(false);
        options ??= new StorageUploadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StorageItem>.Failure(validation.Error!);
        if (StorageOptionValidation.MetadataSizeBytes(options.Metadata) > 8 * 1024)
            return Result<StorageItem>.Failure(StorageErrors.TooLarge("Google Cloud metadata exceeds the 8 KiB limit."));
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        var normalizedPath = normalized.Value!;
        try
        {
            long? ifGenerationMatch = null;
            if (options.Condition is { IsEmpty: false } condition)
            {
                var expectedGeneration = ParseGeneration(condition.ExpectedVersionId);
                if (expectedGeneration.IsFailure)
                    return Result<StorageItem>.Failure(expectedGeneration.Error!);
                ifGenerationMatch = expectedGeneration.Value;
                if (condition.ExpectedETag is not null)
                {
                    var current = await _client.GetObjectAsync(
                        _bucket,
                        ToKey(normalizedPath),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    var conditionResult = ValidateCurrentCondition(
                        ToItem(normalizedPath, current),
                        condition,
                        "Google Cloud object");
                    if (conditionResult.IsFailure)
                        return Result<StorageItem>.Failure(conditionResult.Error!);
                    ifGenerationMatch ??= checked((long?)current.Generation);
                }
            }
            var item = new GcsObject
            {
                Bucket = _bucket,
                Name = ToKey(normalizedPath),
                ContentType = options.ContentType,
                Metadata = options.Metadata.Count == 0 ? null : new Dictionary<string, string>(options.Metadata, StringComparer.Ordinal)
            };
            var uploaded = await _client.UploadObjectAsync(item, source, new UploadObjectOptions
            {
                ChunkSize = _uploadChunkSize,
                IfGenerationMatch = options.Overwrite ? ifGenerationMatch : 0
            }, cancellationToken).ConfigureAwait(false);
            return Result<StorageItem>.Success(ToItem(normalizedPath, uploaded));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (GoogleApiException error) when (!options.Overwrite && error.HttpStatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            return Result<StorageItem>.Failure(StorageErrors.Conflict("The Google Cloud destination already exists."));
        }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Upload Google Cloud object")); }
    }

    /// <inheritdoc />
    public async Task<Result<StorageItem>> UploadBytesAsync(string path, byte[] content, StorageUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var source = new MemoryStream(content, writable: false);
        await using var sourceLifetime = source.ConfigureAwait(false);
        return await UploadAsync(path, source, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<Stream>> DownloadAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default) =>
        await StorageTransferPipeline.MeterAsync(this, path, await DownloadUnmeteredAsync(path, options, cancellationToken).ConfigureAwait(false), options, cancellationToken).ConfigureAwait(false);

    private async Task<Result<Stream>> DownloadUnmeteredAsync(string path, StorageDownloadOptions? options, CancellationToken cancellationToken)
    {
        options ??= new StorageDownloadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<Stream>.Failure(validation.Error!);
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<Stream>.Failure(normalized.Error!);
        var generation = ParseGeneration(options.VersionId);
        if (generation.IsFailure) return Result<Stream>.Failure(generation.Error!);
        try
        {
            var item = await _client.GetObjectAsync(
                _bucket,
                ToKey(normalized.Value!),
                new GetObjectOptions { Generation = generation.Value },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var size = item.Size.HasValue ? checked((long)item.Size.Value) : (long?)null;
            if (size.HasValue && options.Offset > size.Value)
                return Result<Stream>.Failure(StorageErrors.InvalidPath(
                    "The range offset exceeds the Google Cloud object length."));
            if (size.HasValue && options.Offset == size.Value)
                return Result<Stream>.Success(new MemoryStream([], writable: false));

            DownloadObjectOptions? downloadOptions = generation.Value.HasValue
                ? new DownloadObjectOptions { Generation = generation.Value }
                : null;
            if (options.Offset > 0 || options.Length.HasValue)
            {
                var requestedLength = options.Length;
                if (size.HasValue)
                {
                    var available = size.Value - options.Offset;
                    requestedLength = requestedLength.HasValue
                        ? Math.Min(requestedLength.Value, available)
                        : available;
                }
                if (requestedLength == 0)
                    return Result<Stream>.Success(new MemoryStream([], writable: false));
                var end = requestedLength.HasValue
                    ? options.Offset + requestedLength.Value - 1
                    : (long?)null;
                downloadOptions ??= new DownloadObjectOptions();
                downloadOptions.Range = new RangeHeaderValue(options.Offset, end);
                // The stored CRC32C/MD5 covers the whole object, so it cannot validate a byte range.
                downloadOptions.DownloadValidationMode = DownloadValidationMode.Never;
            }

            var pipe = new Pipe(new PipeOptions(
                pool: MemoryPool<byte>.Shared,
                readerScheduler: PipeScheduler.ThreadPool,
                writerScheduler: PipeScheduler.ThreadPool,
                pauseWriterThreshold: 1_048_576,
                resumeWriterThreshold: 524_288,
                minimumSegmentSize: 65_536,
                useSynchronizationContext: false));
            var downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var producer = DownloadToPipeAsync(
                _bucket,
                ToKey(normalized.Value!),
                pipe.Writer,
                downloadOptions,
                downloadCancellation.Token);
            var stream = new AsyncOwnedResourceStream(
                pipe.Reader.AsStream(),
                async () =>
                {
                    downloadCancellation.Cancel();
                    try { await producer.ConfigureAwait(false); }
                    finally { downloadCancellation.Dispose(); }
                });
            return Result<Stream>.Success(stream);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<Stream>.Failure(Map(error, "Download Google Cloud object")); }
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
                        "Google Cloud virtual directories do not have one atomic identity condition."));
                var prefix = DirectoryPrefix(normalized.Value!);
                var names = new List<string>();
                await foreach (var item in _client.ListObjectsAsync(_bucket, prefix)
                    .WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    names.Add(item.Name);
                }
                if (!options.Recursive && names.Any(name => name != prefix))
                    return Result.Failure(StorageErrors.Conflict("The Google Cloud directory is not empty."));
                foreach (var name in names)
                    await _client.DeleteObjectAsync(_bucket, name, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var condition = ValidateCurrentCondition(info.Value, options.Condition, "Google Cloud object");
                if (condition.IsFailure) return condition;
                var expectedGeneration = ParseGeneration(
                    options.Condition?.ExpectedVersionId ??
                    (options.Condition?.ExpectedETag is null ? null : info.Value.VersionId));
                if (expectedGeneration.IsFailure) return Result.Failure(expectedGeneration.Error!);
                await _client.DeleteObjectAsync(
                    _bucket,
                    ToKey(normalized.Value!),
                    new DeleteObjectOptions { IfGenerationMatch = expectedGeneration.Value },
                    cancellationToken).ConfigureAwait(false);
            }
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Delete Google Cloud object")); }
    }
}
