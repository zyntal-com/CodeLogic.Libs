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

    /// <summary>The deterministic staging path for a resumable write.</summary>
    public static string ResumableStagingPath(string destinationPath, string resumeKey)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{destinationPath}\n{resumeKey}")));
        return Combine(Parent(destinationPath), $"{ResumablePrefix}{digest[..32]}.tmp");
    }

    /// <summary>A resume key describing a source's identity; a changed source gets a different key.</summary>
    public static string SourceKey(string? sourcePath, long? length, DateTimeOffset? modified, string? eTag, string? versionId) =>
        string.Join('|', sourcePath ?? string.Empty, length?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            modified?.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, eTag ?? string.Empty, versionId ?? string.Empty);

    /// <summary>
    /// Writes through staging. Cancellation is reported, not thrown: the staging object is removed, or kept
    /// with its size for a resumable write, and <see cref="StagedWriteResult.Cancelled"/> is set.
    /// </summary>
    public static async Task<StagedWriteResult> WriteAsync(
        IStorageService destination,
        StagedWriteRequest request,
        StagedSourceOpener open,
        CancellationToken cancellationToken)
    {
        var parent = Parent(request.Path);
        var staging = request.StagingPath ?? (request.ResumeKey is { } key ? ResumableStagingPath(request.Path, key) : null);
        PartLease? lease = null;
        StagedWriteResult? outcome = null;
        if (staging is not null)
        {
            // One writer per part file, in this process and across processes (a lock marker beside it). A second
            // transfer of the same source to the same destination (two queued jobs for one file, or two workers)
            // writes a private staging object instead of appending into the bytes the first one is writing; it is
            // not resumable, but neither corrupts the other.
            Result<PartLease?> acquired;
            try { acquired = await PartLease.AcquireAsync(destination, staging, request.Upload.CreateParents, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return CancelledResult(null, 0); }
            if (acquired.IsFailure)
                return new StagedWriteResult(null, acquired.Error, null, 0);
            lease = acquired.Value;
            if (lease is null)
            {
                staging = null;
                request = request with { ResumeKey = null, StagingPath = null };
            }
        }
        try
        {
            if (staging is null)
            {
                Result<string> allocated;
                try { allocated = await AllocateStagingPathAsync(destination, parent, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return CancelledResult(null, 0); }
                if (allocated.IsFailure) return new StagedWriteResult(null, allocated.Error, null, 0);
                staging = allocated.Value!;
            }
            StagedWriteResult result;
            try
            {
                result = await WriteToStagingAsync(destination, request, open, staging, parent, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (!request.Resumable)
                {
                    var deleted = await DeleteAsync(destination, staging).ConfigureAwait(false);
                    return CancelledResult(deleted.IsFailure ? staging : null, 0);
                }
                var kept = await TryGetInfoAsync(destination, staging).ConfigureAwait(false);
                result = kept.IsSuccess ? CancelledResult(staging, kept.Value!.Size ?? 0) : CancelledResult(null, 0);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A throwing provider, source, or progress sink: the staging object is cleaned up (or kept, for a
                // resumable write) exactly as for a failure it reported.
                result = await FailAsync(destination, request, staging, StorageErrors.FromException(error, "Write transfer staging object"), contentIsWrong: false).ConfigureAwait(false);
            }
            // A successful write hands its hold on the part file to the caller, who releases it after the promote.
            outcome = result with { Resumable = request.Resumable, Lease = result.IsSuccess ? lease : null };
            return outcome;
        }
        finally
        {
            if (lease is not null && outcome?.Lease is null)
                await lease.ReleaseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether a resumable part file is being written by a transfer, in this process or (by its lock marker)
    /// in another one. A marker that cannot be read counts as in use.
    /// </summary>
    internal static Task<bool> IsPartInUseAsync(IStorageService destination, string staging) => PartLease.InUseAsync(destination, staging);

    private static StagedWriteResult CancelledResult(string? stagingLeft, long bytes) =>
        new(null, StorageErrors.Cancelled("The write was cancelled."), stagingLeft, bytes) { Cancelled = true };

    private static async Task<Result<StorageItem>> TryGetInfoAsync(IStorageService destination, string path)
    {
        try { return await destination.GetInfoAsync(path, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) { return Result<StorageItem>.Failure(StorageErrors.FromException(error, "Read transfer staging object")); }
    }

    private static async Task<StagedWriteResult> WriteToStagingAsync(
        IStorageService destination,
        StagedWriteRequest request,
        StagedSourceOpener open,
        string staging,
        string parent,
        CancellationToken cancellationToken)
    {
        var verify = request.Verify || request.ExpectedSha256 is not null;
        long offset = 0;
        using var hash = verify ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        if (request.Resumable)
        {
            var resumed = await ResumableOffsetAsync(destination, staging, request.ExpectedLength, cancellationToken).ConfigureAwait(false);
            if (resumed.IsFailure)
                return resumed.Error!.Code == StorageErrors.ConflictCode
                    ? new StagedWriteResult(null, resumed.Error, null, 0)
                    : await FailAsync(destination, request, staging, resumed.Error!, contentIsWrong: false).ConfigureAwait(false);
            offset = resumed.Value;
        }

        // Everything is staged already: the source is not opened at its end, which servers answer with
        // "range not satisfiable", so a fully staged resume can still complete.
        var complete = offset > 0 && request.ExpectedLength == offset;
        Result<Stream> opened;
        if (offset > 0 && hash is not null)
        {
            // Verification must not take the staged prefix on trust: the source's own first bytes are read and
            // hashed, and must match what is staged, before only the rest is appended.
            opened = await OpenVerifiedTailAsync(destination, staging, offset, hash, open, cancellationToken).ConfigureAwait(false);
            if (opened.IsSuccess && opened.Value is null)
            {
                await DeleteAsync(destination, staging).ConfigureAwait(false);
                offset = 0;
                complete = false;
                hash.GetHashAndReset();
                opened = await open(0, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (complete)
        {
            opened = Result<Stream>.Success(Stream.Null);
        }
        else
        {
            opened = await open(offset, cancellationToken).ConfigureAwait(false);
        }
        if (opened.IsFailure)
            return await FailAsync(destination, request, staging, opened.Error!, contentIsWrong: false).ConfigureAwait(false);

        var remaining = request.ExpectedLength is { } expected ? expected - offset : (long?)null;
        Result<long> written;
        GuardedReadStream guard;
        var source = opened.Value!;
        await using (source.ConfigureAwait(false))
        {
            guard = new GuardedReadStream(source, hash, remaining);
            var stagingUpload = request.Upload with
            {
                Overwrite = true,
                ConflictPolicy = null,
                Condition = null,
                ExpectedLength = null,
                Verify = false,
                ExpectedSha256 = null,
                CreateParents = request.Upload.CreateParents
            };
            // A resumable write appends straight into its staging object where the provider can, so the bytes
            // that arrived before a failure stay there; a normal upload would stage them away and discard them.
            var direct = request.Resumable && destination is IStorageAppendService && destination.Capabilities.Supports(StorageFeature.Append);
            if (direct && offset == 0 && parent.Length > 0 && request.Upload.CreateParents)
            {
                // Appending does not create missing folders everywhere (SFTP), unlike an upload.
                var created = await destination.CreateDirectoryAsync(parent, cancellationToken).ConfigureAwait(false);
                if (created.IsFailure)
                    return await FailAsync(destination, request, staging, created.Error!, contentIsWrong: false).ConfigureAwait(false);
            }
            try
            {
                if (complete)
                {
                    // Nothing to append; a source that still has bytes is longer than expected.
                    try { _ = await guard.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false); }
                    catch (SourceTooLongException) { }
                    written = Result<long>.Success(0);
                }
                else
                {
                    written = offset == 0 && !direct
                        ? await RelayAsync(guard, destination, staging, stagingUpload, cancellationToken).ConfigureAwait(false)
                        : await AppendAsync(destination, staging, guard, request.Upload, request.ExpectedLength, request.Path, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Cleaned up (or kept, for resume) by WriteAsync.
                throw;
            }
            catch (Exception) when (guard.Exceeded)
            {
                // The provider let the guard's "too long" escape; it is reported as the conflict it is.
                written = Result<long>.Failure(StorageErrors.Conflict("The source delivered more bytes than expected."));
            }
        }

        if (guard.Exceeded)
            return await FailAsync(destination, request, staging, StorageErrors.Conflict(
                $"The source delivered more than the expected {request.ExpectedLength} bytes.",
                $"expectedLength={request.ExpectedLength}"), contentIsWrong: true).ConfigureAwait(false);
        if (written.IsFailure)
            return await FailAsync(destination, request, staging, written.Error!, contentIsWrong: false).ConfigureAwait(false);

        var total = offset + written.Value;
        if (request.ExpectedLength is { } length && total != length)
            // A short source leaves a good prefix a resume can continue; a longer one tripped the guard above.
            return await FailAsync(destination, request, staging, StorageErrors.Conflict(
                $"The source delivered {total} bytes; {length} were expected.",
                $"expectedLength={length};actualLength={total}"), contentIsWrong: total > length).ConfigureAwait(false);

        if (request.Resumable)
        {
            // A part file is shared by name: a writer in another process appending to it at the same time would
            // leave more (or other) bytes than this writer accounted for. Such a file is never promoted.
            var stagedInfo = await destination.GetInfoAsync(staging, cancellationToken).ConfigureAwait(false);
            if (stagedInfo.IsFailure)
                return await FailAsync(destination, request, staging, stagedInfo.Error!, contentIsWrong: false).ConfigureAwait(false);
            if (stagedInfo.Value!.Size is { } stagedSize && stagedSize != total)
                return await FailAsync(destination, request, staging, StorageErrors.Conflict(
                    "The staged data changed while it was written; another transfer may be writing the same part file.",
                    $"expectedLength={total};actualLength={stagedSize}"), contentIsWrong: true).ConfigureAwait(false);
        }

        string? digest = null;
        string? verifiedBy = null;
        if (hash is not null)
        {
            digest = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (request.ExpectedSha256 is { } expectedDigest && !string.Equals(expectedDigest, digest, StringComparison.OrdinalIgnoreCase))
                return await FailAsync(destination, request, staging, StorageErrors.Conflict(
                    "The content does not have the expected SHA-256.",
                    $"expectedSha256={expectedDigest.ToLowerInvariant()};actualSha256={digest}"), contentIsWrong: true).ConfigureAwait(false);
            var confirmed = await ConfirmAsync(destination, staging, digest, total, cancellationToken).ConfigureAwait(false);
            if (confirmed.IsFailure)
                // Only a mismatch proves the staged bytes wrong; a confirm that could not run keeps them.
                return await FailAsync(destination, request, staging, confirmed.Error!, contentIsWrong: confirmed.Error!.Code == StorageErrors.ConflictCode).ConfigureAwait(false);
            verifiedBy = confirmed.Value;
        }

        return new StagedWriteResult(new StagedContent(staging, written.Value, offset, digest, verifiedBy), null, null, total);
    }
}
