using System.Runtime.CompilerServices;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers;
using CodeLogic.Core.Results;

namespace CL.Storage.Registry;

/// <summary>Bandwidth budgets attached to one backend: its own and the library-wide ones.</summary>
internal sealed record TransferLimits(TokenBucket? Upload, TokenBucket? Download, TokenBucket? TotalUpload, TokenBucket? TotalDownload)
{
    public static TransferLimits None { get; } = new(null, null, null, null);
    public bool LimitsUploads => Upload is not null || TotalUpload is not null;
    public bool LimitsDownloads => Download is not null || TotalDownload is not null;
}

/// <summary>
/// The shared entry every backend's upload and download passes through: progress reporting, speed limits,
/// and conflict policies. Backends call it themselves, so the behaviour is the same whether a backend is
/// used through the library or on its own, and for relayed transfers between connections.
/// </summary>
internal static class StorageTransferPipeline
{
    private static readonly ConditionalWeakTable<object, TransferLimits> Limits = new();

    /// <summary>Attaches bandwidth limits to a backend; null or non-positive values mean unlimited.</summary>
    public static void SetLimits(object backend, long? uploadBytesPerSecond, long? downloadBytesPerSecond, TransferLimits? library = null)
    {
        Limits.AddOrUpdate(backend, new TransferLimits(
            Bucket(uploadBytesPerSecond),
            Bucket(downloadBytesPerSecond),
            library?.TotalUpload,
            library?.TotalDownload));
    }

    /// <summary>Swaps the library-wide budgets attached to a backend, keeping its own limits.</summary>
    public static void SetLibraryLimits(object backend, TransferLimits library) =>
        Limits.AddOrUpdate(backend, LimitsFor(backend) with
        {
            TotalUpload = library.TotalUpload,
            TotalDownload = library.TotalDownload
        });

    /// <summary>Creates the library-wide budgets that every connection shares.</summary>
    public static TransferLimits LibraryLimits(long? totalUploadBytesPerSecond, long? totalDownloadBytesPerSecond) =>
        new(null, null, Bucket(totalUploadBytesPerSecond), Bucket(totalDownloadBytesPerSecond));

    public static TransferLimits LimitsFor(object backend) => Limits.TryGetValue(backend, out var limits) ? limits : TransferLimits.None;

    private static readonly ConditionalWeakTable<object, StrongBox<int>> SessionLimits = new();

    /// <summary>Records how many sessions a session-pooled backend (FTP, SFTP) may open at once.</summary>
    public static void SetSessionLimit(object backend, int maxSessions) => SessionLimits.AddOrUpdate(backend, new StrongBox<int>(maxSessions));

    /// <summary>The session limit of a pooled backend, or null for backends without one.</summary>
    public static int? SessionLimitFor(object backend) => SessionLimits.TryGetValue(backend, out var limit) ? limit.Value : null;

    /// <summary>Whether an upload call must go through <see cref="UploadAsync"/> first.</summary>
    public static bool Applies(object backend, StorageUploadOptions? options) =>
        options?.PipelineApplied != true &&
        (options?.ConflictPolicy is not null || options?.Progress is not null || LimitsFor(backend).LimitsUploads || NeedsStaging(options) ||
         NeedsConditionStaging(backend, options));

    /// <summary>
    /// Whether an upload's <see cref="StorageUploadOptions.Condition"/> must be checked by the library: the
    /// destination cannot enforce it itself, so the content is staged and the condition checked right before the
    /// staged file replaces the destination.
    /// </summary>
    public static bool NeedsConditionStaging(object backend, StorageUploadOptions? options) =>
        options?.Condition is { IsEmpty: false } &&
        backend is IStorageService service && !service.Capabilities.Supports(StorageFeature.ConditionalUpdate);

    /// <summary>Whether an upload must go through a staging object: verification, an exact length, or a resume.</summary>
    public static bool NeedsStaging(StorageUploadOptions? options) =>
        options is not null &&
        (options.Verify || options.ExpectedSha256 is not null || options.ExpectedLength is not null ||
         options.ConflictPolicy == StorageConflictPolicy.Resume);

    public static async Task<Result<StorageItem>> UploadAsync(
        IStorageService destination,
        string path,
        Stream source,
        StorageUploadOptions? options,
        CancellationToken cancellationToken)
    {
        options ??= new StorageUploadOptions();
        var limits = LimitsFor(destination);
        Stream stream = source;
        if (options.Progress is not null || limits.LimitsUploads)
        {
            long? total = null;
            if (source.CanSeek)
            {
                try { total = Math.Max(0, source.Length - source.Position); }
                catch (NotSupportedException) { }
            }
            stream = new MeteredStream(source, options.Progress, total, path, leaveOpen: true, limits.Upload, limits.TotalUpload);
        }
        var inner = options with { Progress = null, PipelineApplied = true };
        try
        {
            if (NeedsStaging(inner) || NeedsConditionStaging(destination, inner))
                return await StagedUploadAsync(destination, path, stream, inner, cancellationToken, unmetered: source).ConfigureAwait(false);
            return inner.ConflictPolicy is not null
                ? await StorageConflictResolver.UploadAsync(destination, path, stream, inner, cancellationToken).ConfigureAwait(false)
                : await destination.UploadAsync(path, stream, inner, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!ReferenceEquals(stream, source)) await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Uploads through a staging object: the content is counted, hashed, and confirmed there, and replaces the
    /// destination only when complete. Resume keeps the staging object when the upload fails, keyed by the
    /// destination, the length, and <see cref="StorageUploadOptions.SourceLastModified"/>, so retrying the same
    /// upload appends only what is missing.
    /// </summary>
    internal static async Task<Result<StorageItem>> StagedUploadAsync(
        IStorageService destination,
        string path,
        Stream source,
        StorageUploadOptions options,
        CancellationToken cancellationToken,
        Stream? unmetered = null)
    {
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StorageItem>.Failure(validation.Error!);
        var normalized = StoragePath.Normalize(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        path = normalized.Value!;
        var resume = options.ConflictPolicy == StorageConflictPolicy.Resume;
        var overwrite = options.Overwrite;
        var start = source.CanSeek ? source.Position : 0;
        long? remaining = source.CanSeek ? source.Length - start : null;

        if (options.ConflictPolicy is { } policy && !resume)
        {
            var decision = await StorageConflictResolver.ResolveAsync(
                destination, path, policy, options.Overwrite, remaining, options.SourceLastModified, cancellationToken).ConfigureAwait(false);
            if (decision.IsFailure) return Result<StorageItem>.Failure(decision.Error!);
            if (decision.Value.Skip) return Result<StorageItem>.Success(decision.Value.Existing!);
            path = decision.Value.Path;
            overwrite = decision.Value.Overwrite;
        }
        if (resume)
        {
            if (!source.CanSeek)
                return Result<StorageItem>.Failure(StorageErrors.Unsupported("Resuming an upload needs a seekable source stream."));
            // Staged bytes are reused only for the same content; a length alone, or a name alone (an edited file
            // of the same length keeps its path), would let different content continue an old prefix.
            if (options.SourceLastModified is null && !(options.SourceIdentity is not null && options.SourceIdentityIsContentVersion))
                return Result<StorageItem>.Failure(StorageErrors.InvalidContent(
                    "Resuming an upload needs SourceLastModified, or a SourceIdentity marked SourceIdentityIsContentVersion, so that changed content is never appended to an old prefix."));
            overwrite = true;
            var existing = await destination.GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
            // The check reads the caller's stream directly: hashing is not an upload, so neither speed limits nor
            // progress apply to it.
            if (existing.IsSuccess && existing.Value!.ItemType == StorageItemType.File && existing.Value.Size == remaining &&
                await HoldsContentAsync(destination, path, unmetered is { CanSeek: true } ? unmetered : source, start, cancellationToken).ConfigureAwait(false))
                return Result<StorageItem>.Success(existing.Value);
        }
        if (!overwrite)
        {
            var exists = await destination.ExistsAsync(path, cancellationToken).ConfigureAwait(false);
            if (exists.IsFailure) return Result<StorageItem>.Failure(exists.Error!);
            if (exists.Value) return Result<StorageItem>.Failure(StorageErrors.Conflict($"The destination '{path}' already exists."));
        }
        if (options.Condition is { IsEmpty: false } condition)
        {
            var check = await StagedWriter.CheckConditionAsync(destination, path, condition, cancellationToken).ConfigureAwait(false);
            if (check.IsFailure) return Result<StorageItem>.Failure(check.Error!);
        }

        var written = await StagedWriter.WriteAsync(
            destination,
            new StagedWriteRequest
            {
                Path = path,
                Upload = options with { Progress = null },
                ExpectedLength = options.ExpectedLength ?? (resume ? remaining : null),
                Verify = options.Verify,
                ExpectedSha256 = options.ExpectedSha256,
                ResumeKey = resume ? StagedWriter.SourceKey(options.SourceIdentity, remaining, options.SourceLastModified, null, null) : null
            },
            (offset, _) =>
            {
                if (source.CanSeek) source.Position = start + offset;
                return Task.FromResult(Result<Stream>.Success(new NonClosingStream(source)));
            },
            cancellationToken).ConfigureAwait(false);
        if (written.Cancelled) cancellationToken.ThrowIfCancellationRequested();
        if (!written.IsSuccess)
        {
            // The provider's details (retry delay, HTTP status, TLS reason) stay; the staging state is added.
            var details = written.StagingLeft is { } left ? $"stagingPath={left};bytesStaged={written.BytesStaged}" : string.Empty;
            return Result<StorageItem>.Failure(StagedWriter.AppendDetails(written.Error!, details));
        }
        try
        {
            if (written.Lease is { } lease)
            {
                // Promoted only while this upload still holds its part file; one taken over is someone else's.
                var owned = await lease.StillOwnedAsync(cancellationToken).ConfigureAwait(false);
                if (owned.IsFailure) return Result<StorageItem>.Failure(owned.Error!);
                if (!owned.Value)
                    return Result<StorageItem>.Failure(StorageErrors.Conflict(
                        $"The staged data for '{path}' was taken over by another transfer, so it was not committed."));
            }
            PromoteOutcome promoted;
            try
            {
                // The condition goes to the provider's move, which enforces it atomically where it can.
                promoted = await StagedWriter.PromoteCoreAsync(
                    destination, written.Content!.StagingPath, path, overwrite, options.Condition, options.CreateParents, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A resumed upload keeps its complete part file, so retrying it only promotes.
                if (!written.Resumable)
                    await StagedWriter.DeleteAsync(destination, written.Content!.StagingPath).ConfigureAwait(false);
                throw;
            }
            if (promoted.Result.IsFailure)
            {
                if (written.Resumable)
                    return Result<StorageItem>.Failure(promoted.Result.Error!);
                var removed = await StagedWriter.DeleteAsync(destination, written.Content.StagingPath).ConfigureAwait(false);
                return Result<StorageItem>.Failure(removed.IsFailure
                    ? StagedWriter.AppendDetails(promoted.Result.Error!, $"{StorageErrorInfo.LeftBehindKey}={written.Content.StagingPath}")
                    : promoted.Result.Error!);
            }
            // Committed: report the result even if the caller cancels now, and what the provider left behind.
            var confirmed = await StagedWriter.ConfirmPromotedAsync(destination, path, written.Content, CancellationToken.None).ConfigureAwait(false);
            return await StagedWriter.ReportLeftBehindAsync(destination, path, confirmed, promoted.LeftBehind).ConfigureAwait(false);
        }
        finally
        {
            if (written.Lease is { } held)
                await held.ReleaseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether the destination already holds exactly the source's content: its SHA-256 (the server's, or else
    /// read back) equals the source's. Size alone is not trusted.
    /// </summary>
    private static async Task<bool> HoldsContentAsync(IStorageService destination, string path, Stream source, long start, CancellationToken cancellationToken)
    {
        var server = await destination.ComputeChecksumAsync(path, StorageChecksumAlgorithm.Sha256, mode: StorageChecksumMode.PreferServer, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (server.IsFailure) return false;
        source.Position = start;
        var local = await System.Security.Cryptography.SHA256.HashDataAsync(new NonClosingStream(source), cancellationToken).ConfigureAwait(false);
        source.Position = start;
        return string.Equals(Convert.ToHexStringLower(local), server.Value!.HexValue, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Hands a caller's stream to a reader that disposes what it is given.</summary>
    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Wraps a successful download in progress reporting and speed limits when requested.</summary>
    /// <summary>
    /// Wraps a download for progress and speed limits. With a progress sink and no known length, the item's
    /// size is looked up once so reports carry <see cref="StorageTransferProgress.TotalBytes"/>.
    /// </summary>
    public static async Task<Result<Stream>> MeterAsync(
        IStorageBackend backend,
        string path,
        Result<Stream> download,
        StorageDownloadOptions? options,
        CancellationToken cancellationToken)
    {
        if (download.IsFailure) return download;
        var limits = LimitsFor(backend);
        if (options?.Progress is null && !limits.LimitsDownloads) return download;
        var total = options?.Length;
        if (total is null && options?.Progress is not null)
        {
            var stream = download.Value!;
            if (stream.CanSeek)
            {
                total = stream.Length - stream.Position;
            }
            else
            {
                var info = await backend.GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
                if (info.IsSuccess && info.Value!.Size is { } size)
                    total = Math.Max(0, size - (options.Offset));
            }
        }
        return Result<Stream>.Success(new MeteredStream(
            download.Value!,
            options?.Progress,
            total,
            path,
            leaveOpen: false,
            limits.Download,
            limits.TotalDownload));
    }

    private static TokenBucket? Bucket(long? bytesPerSecond) => bytesPerSecond is > 0 ? new TokenBucket(bytesPerSecond.Value) : null;
}
