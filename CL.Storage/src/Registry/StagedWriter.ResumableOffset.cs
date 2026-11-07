using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Registry;

internal static partial class StagedWriter
{

    /// <summary>
    /// How many bytes an earlier attempt staged. Only a part file that is not there means none; a size that
    /// cannot be read fails the attempt (and keeps the part file) rather than appending the whole source
    /// after the existing prefix.
    /// </summary>
    private static async Task<Result<long>> ResumableOffsetAsync(IStorageService destination, string staging, long? expectedLength, CancellationToken cancellationToken)
    {
        var existing = await destination.GetInfoAsync(staging, cancellationToken).ConfigureAwait(false);
        if (existing.IsFailure)
            return existing.Error!.Code == StorageErrors.NotFoundCode ? Result<long>.Success(0) : Result<long>.Failure(existing.Error);
        if (existing.Value!.ItemType != StorageItemType.File)
            return Result<long>.Failure(StorageErrors.Conflict($"The staging path '{staging}' is not a file."));
        var size = existing.Value.Size ?? 0;
        var canAppend = destination is IStorageAppendService && destination.Capabilities.Supports(StorageFeature.Append);
        if (size > 0 && canAppend && (expectedLength is null || size <= expectedLength))
            return Result<long>.Success(size);
        await DeleteAsync(destination, staging).ConfigureAwait(false);
        return Result<long>.Success(0);
    }

    /// <summary>
    /// Opens the source from the start, hashes its first <paramref name="length"/> bytes into
    /// <paramref name="hash"/>, and checks them against the staged prefix. Returns the source positioned at
    /// <paramref name="length"/>, or a null stream when the prefix does not match, in which case the caller
    /// starts over. A staged prefix that cannot be read is a failure (the part file is kept), not a mismatch.
    /// </summary>
    private static async Task<Result<Stream>> OpenVerifiedTailAsync(
        IStorageService destination,
        string staging,
        long length,
        IncrementalHash hash,
        StagedSourceOpener open,
        CancellationToken cancellationToken)
    {
        using var stagedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var download = await destination.DownloadAsync(staging, new StorageDownloadOptions { Length = length }, cancellationToken).ConfigureAwait(false);
        if (download.IsFailure) return Result<Stream>.Failure(download.Error!);
        var stagedStream = download.Value!;
        await using (stagedStream.ConfigureAwait(false))
        {
            if (await HashAsync(stagedStream, stagedHash, length, cancellationToken).ConfigureAwait(false) != length)
                return Result<Stream>.Success(null!);
        }

        var opened = await open(0, cancellationToken).ConfigureAwait(false);
        if (opened.IsFailure) return opened;
        var source = opened.Value!;
        var matched = false;
        try
        {
            matched = await HashAsync(source, hash, length, cancellationToken).ConfigureAwait(false) == length &&
                hash.GetCurrentHash().AsSpan().SequenceEqual(stagedHash.GetHashAndReset());
        }
        finally
        {
            if (!matched) await source.DisposeAsync().ConfigureAwait(false);
        }
        return Result<Stream>.Success(matched ? source : null!);
    }

    /// <summary>Hashes up to <paramref name="length"/> bytes of a stream and returns how many there were.</summary>
    private static async Task<long> HashAsync(Stream stream, IncrementalHash hash, long length, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(SegmentSize);
        try
        {
            long read = 0;
            while (read < length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(SegmentSize, length - read)), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                hash.AppendData(buffer, 0, count);
                read += count;
            }
            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Confirms the staged object by the server's SHA-256 where it keeps one, otherwise by reading it back.</summary>
    private static async Task<Result<string>> ConfirmAsync(IStorageService destination, string staging, string digest, long length, CancellationToken cancellationToken)
    {
        var server = await destination.GetServerChecksumAsync(staging, StorageChecksumAlgorithm.Sha256, cancellationToken).ConfigureAwait(false);
        if (server.IsSuccess)
        {
            return string.Equals(server.Value!.HexValue, digest, StringComparison.OrdinalIgnoreCase)
                ? Result<string>.Success("server")
                : Result<string>.Failure(StorageErrors.Conflict("The destination's SHA-256 does not match the content that was sent.",
                    $"expectedSha256={digest};actualSha256={server.Value.HexValue.ToLowerInvariant()}"));
        }
        var reread = await destination.ComputeChecksumAsync(staging, StorageChecksumAlgorithm.Sha256, mode: StorageChecksumMode.ComputeOnly, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (reread.IsFailure)
            return Result<string>.Failure(reread.Error!);
        if (reread.Value!.BytesProcessed != length || !string.Equals(reread.Value.HexValue, digest, StringComparison.OrdinalIgnoreCase))
            return Result<string>.Failure(StorageErrors.Conflict("The destination does not hold the content that was sent.",
                $"expectedSha256={digest};actualSha256={reread.Value.HexValue.ToLowerInvariant()};expectedLength={length};actualLength={reread.Value.BytesProcessed}"));
        return Result<string>.Success("reread");
    }

    private static async Task<Result<long>> AppendAsync(
        IStorageService destination,
        string staging,
        GuardedReadStream source,
        StorageUploadOptions upload,
        long? total,
        string path,
        CancellationToken cancellationToken)
    {
        // An append bypasses the provider's upload entry, so the destination's speed limits apply here (unless
        // the caller's pipeline already applied them to the source).
        var progress = upload.Progress;
        var limits = upload.PipelineApplied ? TransferLimits.None : StorageTransferPipeline.LimitsFor(destination);
        Stream input = progress is null && !limits.LimitsUploads
            ? source
            : new Providers.MeteredStream(source, progress, total, path, leaveOpen: true, limits.Upload, limits.TotalUpload);
        Result<StorageItem> appended;
        try
        {
            appended = await destination.AppendAsync(staging, input, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!ReferenceEquals(input, source)) await input.DisposeAsync().ConfigureAwait(false);
        }
        if (appended.IsFailure) return Result<long>.Failure(appended.Error!);
        // A provider that stopped reading early must not look like the source ended.
        if (!source.Exceeded && await source.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) > 0)
            return Result<long>.Failure(StorageErrors.ProviderError("The destination reported the append complete before consuming the whole source."));
        return Result<long>.Success(source.BytesRead);
    }

    /// <summary>
    /// Streams <paramref name="source"/> into an upload through a bounded pipe (at most 1 MiB read ahead) and
    /// checks that the provider consumed everything it was given.
    /// </summary>
    internal static async Task<Result<long>> RelayAsync(
        Stream source,
        IStorageService destination,
        string stagingPath,
        StorageUploadOptions uploadOptions,
        CancellationToken cancellationToken)
    {
        var pipe = new Pipe(new PipeOptions(
            pool: MemoryPool<byte>.Shared,
            readerScheduler: PipeScheduler.ThreadPool,
            writerScheduler: PipeScheduler.ThreadPool,
            pauseWriterThreshold: PauseWriterThreshold,
            resumeWriterThreshold: ResumeWriterThreshold,
            minimumSegmentSize: SegmentSize,
            useSynchronizationContext: false));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = ProduceAsync(source, pipe.Writer, linked.Token, cancellationToken);
        Result<StorageItem>? upload = null;
        Error? thrownUploadError = null;
        try
        {
            var relayStream = new CountingReadStream(pipe.Reader.AsStream(leaveOpen: true));
            await using var relayStreamLifetime = relayStream.ConfigureAwait(false);
            try
            {
                upload = await destination.UploadAsync(stagingPath, relayStream, uploadOptions, linked.Token).ConfigureAwait(false);
                if (upload.Value.IsFailure || !producer.IsCompleted)
                    linked.Cancel();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                linked.Cancel();
                await ObserveProducerAsync(producer).ConfigureAwait(false);
                throw;
            }
            catch (Exception error)
            {
                thrownUploadError = StorageErrors.FromException(error, "Upload transfer staging object");
                linked.Cancel();
            }

            var produced = await producer.ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                cancellationToken.ThrowIfCancellationRequested();
            // A source that failed makes the upload fail too; the source's error is the one that explains it.
            // (The producer reports "unavailable" only when the upload stopped reading first.)
            if (produced.IsFailure && produced.Error!.Code != StorageErrors.UnavailableCode)
                return Result<long>.Failure(produced.Error);
            if (thrownUploadError is not null)
                return Result<long>.Failure(thrownUploadError);
            if (upload?.IsFailure == true)
                return Result<long>.Failure(upload.Value.Error!);
            if (produced.IsFailure)
                return Result<long>.Failure(produced.Error!);
            if (relayStream.BytesRead != produced.Value)
                return Result<long>.Failure(StorageErrors.ProviderError(
                    "The destination provider reported upload success before consuming the complete transfer stream."));
            return Result<long>.Success(produced.Value);
        }
        finally
        {
            linked.Cancel();
            await pipe.Reader.CompleteAsync().ConfigureAwait(false);
            await ObserveProducerAsync(producer).ConfigureAwait(false);
        }
    }
}
