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
    /// Replaces or creates the destination with the staged object. A create (<paramref name="overwrite"/>
    /// false) is atomic where the destination declares <see cref="StorageFeature.ConditionalCreate"/>; a
    /// version condition is re-checked immediately before the move.
    /// </summary>
    public static async Task<(Result Result, StorageConditionEnforcement Enforcement)> PromoteAsync(
        IStorageService destination,
        string stagingPath,
        string path,
        bool overwrite,
        StorageMutationCondition? condition,
        bool createParents,
        CancellationToken cancellationToken)
    {
        var outcome = await PromoteCoreAsync(destination, stagingPath, path, overwrite, condition, createParents, cancellationToken).ConfigureAwait(false);
        return (outcome.Result, outcome.Enforcement);
    }

    /// <summary>
    /// Promotes the staged object and says what happened. A version condition is checked first and then passed
    /// to the provider's move, which enforces it in the same request where it can (and answers
    /// <c>storage.unsupported</c> where it cannot, in which case the checked-before move is used). A provider
    /// error that says the destination was committed (<see cref="StorageErrorInfo.DestinationStateKey"/>
    /// <c>=complete</c>) is a success with the internal objects it left behind, never a failure to roll back.
    /// </summary>
    public static async Task<PromoteOutcome> PromoteCoreAsync(
        IStorageService destination,
        string stagingPath,
        string path,
        bool overwrite,
        StorageMutationCondition? condition,
        bool createParents,
        CancellationToken cancellationToken)
    {
        var enforcement = StorageConditionEnforcement.None;
        var options = new StorageTransferOptions { Overwrite = overwrite, CreateParents = createParents };
        if (condition is { IsEmpty: false })
        {
            var check = await CheckConditionAsync(destination, path, condition, cancellationToken).ConfigureAwait(false);
            if (check.IsFailure) return new PromoteOutcome(check, StorageConditionEnforcement.CheckedBeforeCommit, Touched: false, []);
            enforcement = await StorageConditionEnforcements.ForAsync(destination, StorageConditionKind.MatchVersion, serverSideCopy: true, cancellationToken).ConfigureAwait(false);
            options = options with { Overwrite = true, DestinationCondition = condition };
        }
        else if (!overwrite)
        {
            enforcement = await StorageConditionEnforcements.ForAsync(destination, StorageConditionKind.CreateOnly, serverSideCopy: true, cancellationToken).ConfigureAwait(false);
        }
        var moved = await destination.MoveAsync(stagingPath, path, options, cancellationToken).ConfigureAwait(false);
        if (moved.IsFailure && options.DestinationCondition is not null && moved.Error!.Code == StorageErrors.UnsupportedCode)
        {
            // The provider cannot enforce the condition in its move: it was checked just before, which is all
            // this connection offers.
            enforcement = StorageConditionEnforcement.CheckedBeforeCommit;
            options = options with { DestinationCondition = null };
            moved = await destination.MoveAsync(stagingPath, path, options, cancellationToken).ConfigureAwait(false);
        }
        if (moved.IsFailure && StorageErrorInfo.DestinationCommitted(moved.Error))
            return new PromoteOutcome(Result.Success(), enforcement, Touched: true, LeftBehind(moved.Error));
        // A conditional move the provider refused (a 412, or a create-only move onto a name that exists) moved
        // nothing: the destination is exactly as the refusal found it, even if someone else deleted it meanwhile.
        var refused = moved.IsFailure && moved.Error!.Code == StorageErrors.ConflictCode &&
            (options.DestinationCondition is not null || !options.Overwrite);
        return new PromoteOutcome(moved, enforcement, Touched: !refused, []);
    }

    /// <summary>Every <see cref="StorageErrorInfo.LeftBehindKey"/> entry in an error's details.</summary>
    internal static IReadOnlyList<string> LeftBehind(Error? error)
    {
        if (error?.Details is not { Length: > 0 } details) return [];
        var prefix = StorageErrorInfo.LeftBehindKey + "=";
        return [.. details.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.StartsWith(prefix, StringComparison.Ordinal) && part.Length > prefix.Length)
            .Select(part => part[prefix.Length..])];
    }

    /// <summary>
    /// Completes a committed staged upload or streamed write whose provider reported internal objects left behind
    /// (its own backup or staging copy). Those still there are reported: as a <c>storage.partial_failure</c> that
    /// carries <c>destinationState=complete</c> and one <c>leftBehind</c> entry each, since the result of an upload
    /// has no other place for them. None still there (or none reported) is the confirmed result as it is.
    /// </summary>
    internal static async Task<Result<StorageItem>> ReportLeftBehindAsync(IStorageService destination, string path, Result<StorageItem> confirmed, IReadOnlyList<string> leftBehind)
    {
        var remaining = new List<string>();
        foreach (var left in leftBehind)
        {
            Result<bool> exists;
            try { exists = await destination.ExistsAsync(left, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) { exists = Result<bool>.Failure(StorageErrors.FromException(error, "Read transfer leftover")); }
            if (exists.IsFailure || exists.Value)
                remaining.Add(left);
        }
        if (remaining.Count == 0)
            return confirmed;
        var details = string.Join(';', remaining.Select(left => $"{StorageErrorInfo.LeftBehindKey}={left}"));
        return Result<StorageItem>.Failure(confirmed.IsFailure
            ? AppendDetails(confirmed.Error!, details)
            : StorageErrors.PartialFailure(
                $"'{path}' was written, but the provider left internal objects behind.",
                $"{StorageErrorInfo.DestinationStateKey}=complete;{details}"));
    }

    /// <summary>Appends <c>key=value</c> details to an error, keeping what the provider put there.</summary>
    internal static Error AppendDetails(Error error, string details) =>
        string.IsNullOrEmpty(details) ? error
        : error.WithDetails(string.IsNullOrEmpty(error.Details) ? details : $"{error.Details};{details}");

    /// <summary>
    /// Confirms a promoted destination: it has the staged length and, where the server keeps a SHA-256, the
    /// digest that was verified. A rename does not change content, so nothing is read back. A mismatch is a
    /// <c>storage.conflict</c> carrying <c>destinationState=complete</c>: the destination was committed and
    /// must be reported, not rolled back (it may be another writer's newer content). A read-back that fails is
    /// retried a few times while the error is transient; if it still fails, the error carries
    /// <c>destinationState=complete</c> too, since the content was committed before it.
    /// </summary>
    public static async Task<Result<StorageItem>> ConfirmPromotedAsync(IStorageService destination, string path, StagedContent content, CancellationToken cancellationToken)
    {
        Result<StorageItem> info;
        for (var attempt = 1; ; attempt++)
        {
            try { info = await destination.GetInfoAsync(path, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException) { info = Result<StorageItem>.Failure(StorageErrors.FromException(error, "Confirm transfer destination")); }
            if (info.IsSuccess || attempt >= ReadBackAttempts || !StorageErrorInfo.IsTransient(info.Error)) break;
            await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken).ConfigureAwait(false);
        }
        if (info.IsFailure)
            return Result<StorageItem>.Failure(StorageErrorInfo.DestinationCommitted(info.Error) ? info.Error!
                : AppendDetails(info.Error!, $"{StorageErrorInfo.DestinationStateKey}=complete"));
        var total = content.Bytes + content.BytesResumed;
        if (info.Value!.Size is { } size && size != total)
            return Result<StorageItem>.Failure(StorageErrors.Conflict(
                $"The destination '{path}' does not hold what was committed: it is {size} bytes, not {total}.",
                $"expectedLength={total};actualLength={size};{StorageErrorInfo.DestinationStateKey}=complete"));
        if (content.Sha256 is { } digest)
        {
            Result<StorageChecksum> server;
            try { server = await destination.GetServerChecksumAsync(path, StorageChecksumAlgorithm.Sha256, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException) { server = Result<StorageChecksum>.Failure(StorageErrors.FromException(error, "Confirm transfer destination")); }
            if (server.IsSuccess && !string.Equals(server.Value!.HexValue, digest, StringComparison.OrdinalIgnoreCase))
                return Result<StorageItem>.Failure(StorageErrors.Conflict(
                    $"The destination '{path}' does not hold what was committed.",
                    $"expectedSha256={digest};actualSha256={server.Value.HexValue.ToLowerInvariant()};{StorageErrorInfo.DestinationStateKey}=complete"));
        }
        return Result<StorageItem>.Success(info.Value with { Sha256 = content.Sha256 });
    }

    /// <summary>Succeeds when the destination exists and still has the expected ETag and version.</summary>
    public static async Task<Result> CheckConditionAsync(
        IStorageService destination,
        string path,
        StorageMutationCondition condition,
        CancellationToken cancellationToken)
    {
        var current = await destination.GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
        if (current.IsFailure)
        {
            return current.Error!.Code == StorageErrors.NotFoundCode
                ? Result.Failure(StorageErrors.Conflict($"The destination '{path}' no longer exists, so it is not the expected version."))
                : Result.Failure(current.Error);
        }
        if (condition.ExpectedETag is { } eTag && !SameETag(eTag, current.Value!.ETag))
            return Result.Failure(StorageErrors.Conflict($"The destination '{path}' changed: its ETag is no longer the expected one.",
                $"expectedETag={eTag};actualETag={current.Value.ETag}"));
        if (condition.ExpectedVersionId is { } version && !string.Equals(version, current.Value!.VersionId, StringComparison.Ordinal))
            return Result.Failure(StorageErrors.Conflict($"The destination '{path}' changed: its version is no longer the expected one.",
                $"expectedVersionId={version};actualVersionId={current.Value.VersionId}"));
        return Result.Success();
    }

    /// <summary>ETags compare without quotes or a weak prefix, as providers report them inconsistently.</summary>
    public static bool SameETag(string? expected, string? actual) =>
        expected is not null && actual is not null &&
        string.Equals(TrimETag(expected), TrimETag(actual), StringComparison.Ordinal);

    private static string TrimETag(string value) =>
        (value.StartsWith("W/", StringComparison.Ordinal) ? value[2..] : value).Trim('"');

    /// <summary>
    /// Deletes a staging object, ignoring one that is already gone. Never recursive: a staging object is a file,
    /// and a path that turns out to be a folder is not the library's to remove.
    /// </summary>
    public static async Task<Result> DeleteAsync(IStorageService destination, string stagingPath)
    {
        try
        {
            return await destination.DeleteAsync(
                stagingPath,
                new StorageDeleteOptions { IgnoreMissing = true },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return Result.Failure(StorageErrors.FromException(error, "Delete transfer staging object"));
        }
    }

    public static async Task<Result<string>> AllocateStagingPathAsync(
        IStorageService destination,
        string parent,
        CancellationToken cancellationToken,
        string namePrefix = ".cl-storage-transfer-")
    {
        for (var attempt = 0; attempt < StagingNameAttempts; attempt++)
        {
            var candidate = Combine(parent, $"{namePrefix}{Guid.NewGuid():N}.tmp");
            var exists = await destination.ExistsAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (exists.IsFailure)
                return Result<string>.Failure(exists.Error!);
            if (!exists.Value)
                return Result<string>.Success(candidate);
        }
        return Result<string>.Failure(StorageErrors.Conflict("Unable to allocate a unique destination staging path."));
    }

    private static async Task<StagedWriteResult> FailAsync(
        IStorageService destination,
        StagedWriteRequest request,
        string staging,
        Error error,
        bool contentIsWrong)
    {
        // Wrong content cannot be resumed; a transport failure leaves a resumable prefix.
        if (request.Resumable && !contentIsWrong)
        {
            var info = await TryGetInfoAsync(destination, staging).ConfigureAwait(false);
            if (info.IsSuccess)
                return new StagedWriteResult(null, error, staging, info.Value!.Size ?? 0);
            return new StagedWriteResult(null, error, null, 0);
        }
        var deleted = await DeleteAsync(destination, staging).ConfigureAwait(false);
        return new StagedWriteResult(null, error, deleted.IsFailure ? staging : null, 0) { CleanupError = deleted.Error };
    }
}
