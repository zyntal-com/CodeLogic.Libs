using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CL.Storage.Providers;
using CodeLogic.Core.Results;

namespace CL.Storage.Queue;

public sealed partial class StorageTransferQueue
{

    /// <summary>
    /// Applies requests that arrived after the attempt decided its outcome (or while it was stepping aside) to
    /// the job as it is now, as if they had been made then; the job is held so it does not start in between.
    /// Every request is answered and the hold released, whatever fails.
    /// </summary>
    private async Task SettleAsync(Entry entry)
    {
        Pending[] late = [];
        try
        {
            lock (_gate)
            {
                late = [.. entry.Requests.OrderByDescending(pending => pending.Request)];
                entry.Requests.Clear();
            }
            foreach (var pending in late)
            {
                Result answer;
                try { answer = await ControlAsync(entry.Record.Id, pending.Request, CancellationToken.None, settling: true).ConfigureAwait(false); }
                catch (Exception error) { answer = Result.Failure(StorageErrors.FromException(error, "Changing a queued job")); }
                pending.Done.TrySetResult(answer);
            }
        }
        finally
        {
            foreach (var pending in late)
                pending.Done.TrySetResult(Result.Failure(StorageErrors.Unavailable($"Job '{entry.Record.Id}' could not be changed.")));
            lock (_gate) entry.HoldCount--;
        }
    }

    /// <summary>
    /// Gives up a job this worker could not claim or start, taking the store's record. After a store failure,
    /// or a claim refused although the record did not change, the job waits (locally) before it is tried again,
    /// so the store is not hammered. A job whose claims or saves keep failing (<paramref name="saveFailed"/>) waits
    /// longer each time, and fails after <see cref="MaxStoreFailures"/> in a row.
    /// </summary>
    private async Task StepAsideAsync(Entry entry, StorageTransferJobRecord claimed, bool storeFailed = false, bool refused = false, bool saveFailed = false)
    {
        var (read, fresh) = await TryGetAsync(claimed.Id).ConfigureAwait(false);
        // A record rewritten by a newer schema is left for that version: the job leaves this queue's view.
        var record = read ? Readable(fresh) : claimed;
        Error? gaveUp = null;
        if (record is not null && saveFailed)
        {
            int failures;
            lock (_gate) failures = ++entry.StoreFailures;
            if (failures >= MaxStoreFailures)
            {
                gaveUp = StoreGaveUp(failures);
                var failed = record with { State = StorageTransferState.Failed, FinishedAt = DateTimeOffset.UtcNow, Failure = StorageTransferFailure.From(gaveUp) };
                var (stored, _) = await SaveControlAsync(failed, CancellationToken.None).ConfigureAwait(false);
                // When the store cannot record even that, the job stops in this queue's view only.
                record = stored ?? failed;
            }
            else
            {
                lock (_gate) entry.NotBefore = DateTimeOffset.UtcNow + StoreFailureDelay(failures);
            }
        }
        else if (record is not null && (storeFailed || !read || (refused && record.Revision == claimed.Revision)))
        {
            lock (_gate) entry.NotBefore = DateTimeOffset.UtcNow + StoreRetryDelay;
        }
        var settle = false;
        try
        {
            settle = EndAttempt(entry, record, null, null);
            if (gaveUp is not null) await PublishOutcomeAsync(record, gaveUp).ConfigureAwait(false);
        }
        finally
        {
            if (settle) await SettleAsync(entry).ConfigureAwait(false);
        }
        UpdateIdle();
        Pump();
    }

    private TimeSpan StoreRetryDelay => _options.RetryBaseDelay > TimeSpan.FromSeconds(1) ? _options.RetryBaseDelay : TimeSpan.FromSeconds(1);

    /// <summary>The wait after the <paramref name="failures"/>th store failure in a row: doubling from the retry base delay (at least 100 ms), up to the retry maximum.</summary>
    private TimeSpan StoreFailureDelay(int failures)
    {
        var floor = TimeSpan.FromMilliseconds(100);
        var first = _options.RetryBaseDelay > floor ? _options.RetryBaseDelay : floor;
        var ceiling = _options.RetryMaxDelay > floor ? _options.RetryMaxDelay : floor;
        var delay = first.TotalMilliseconds * Math.Pow(2, Math.Min(failures - 1, 30));
        return delay >= ceiling.TotalMilliseconds ? ceiling : TimeSpan.FromMilliseconds(delay);
    }

    private static Error StoreGaveUp(int failures) =>
        StorageErrors.Unavailable($"The job store failed to record this job {failures} times in a row, so it was stopped. Retry it when the store works again.");

    /// <summary>Runs one attempt of a job.</summary>
    private async Task<(Result Result, StorageTransferReport? Report)> ExecuteAsync(
        StorageTransferJobSpec spec,
        StorageResumeToken? resume,
        IProgress<StorageTransferProgress> progress,
        Func<StorageTransferPhase, CancellationToken, Task> phase,
        CancellationToken cancellationToken)
    {
        switch (spec.Kind)
        {
            case StorageTransferKind.Copy or StorageTransferKind.Move:
            {
                var options = (spec.TransferOptions ?? new StorageTransferOptions()) with { Progress = progress, PhaseChanged = phase };
                // A token continues staged data and replaces the destination, so it is replayed only for a job
                // that allows that; any other job starts its staging again.
                if (resume is not null && ResumeAllowed(options))
                    options = options with { ResumeToken = resume };
                var report = spec.Kind == StorageTransferKind.Copy
                    ? await _library.CopyAsync(spec.SourceConnectionId!, spec.SourcePath, spec.DestinationConnectionId!, spec.DestinationPath, options, cancellationToken).ConfigureAwait(false)
                    : await _library.MoveAsync(spec.SourceConnectionId!, spec.SourcePath, spec.DestinationConnectionId!, spec.DestinationPath, options, cancellationToken).ConfigureAwait(false);
                return (report.ToResult(), report);
            }
            case StorageTransferKind.UploadFile:
            {
                // The queue cannot see when an upload commits (a provider may write in place), so from here
                // on a restart treats the destination as possibly written.
                await phase(StorageTransferPhase.Committing, cancellationToken).ConfigureAwait(false);
                var uploaded = await _library.GetStorage(spec.DestinationConnectionId!)
                    .UploadFileAsync(spec.DestinationPath, spec.SourcePath, (spec.UploadOptions ?? new StorageUploadOptions()) with { Progress = progress }, cancellationToken).ConfigureAwait(false);
                return (uploaded.IsSuccess ? Result.Success() : Result.Failure(uploaded.Error!), null);
            }
            case StorageTransferKind.DownloadFile:
            {
                // The same for downloads: a resumed download appends to the local file in place.
                await phase(StorageTransferPhase.Committing, cancellationToken).ConfigureAwait(false);
                var downloaded = await _library.GetStorage(spec.SourceConnectionId!)
                    .DownloadToFileAsync(spec.SourcePath, spec.DestinationPath, (spec.DownloadOptions ?? new StorageDownloadOptions()) with { Progress = progress },
                        conflictPolicy: spec.DownloadConflictPolicy, cancellationToken: cancellationToken).ConfigureAwait(false);
                return (downloaded.IsSuccess ? Result.Success() : Result.Failure(downloaded.Error!), null);
            }
            case StorageTransferKind.UploadDirectory:
            {
                var options = (spec.TransferOptions ?? new StorageTransferOptions()) with { Progress = progress, PhaseChanged = phase };
                var uploaded = await _library.UploadDirectoryAsync(spec.SourcePath, spec.DestinationConnectionId!, spec.DestinationPath, options, cancellationToken).ConfigureAwait(false);
                return (uploaded.IsSuccess ? Result.Success() : Result.Failure(uploaded.Error!), null);
            }
            default:
            {
                var options = (spec.TransferOptions ?? new StorageTransferOptions()) with { Progress = progress, PhaseChanged = phase };
                var downloaded = await _library.DownloadDirectoryAsync(spec.SourceConnectionId!, spec.SourcePath, spec.DestinationPath, options, cancellationToken).ConfigureAwait(false);
                return (downloaded.IsSuccess ? Result.Success() : Result.Failure(downloaded.Error!), null);
            }
        }
    }

    /// <summary>Whether a job's options let a stored resume token replace its destination.</summary>
    internal static bool ResumeAllowed(StorageTransferOptions options) =>
        options.ConflictPolicy == StorageConflictPolicy.Resume ||
        (options.Overwrite && options.ConflictPolicy is null or StorageConflictPolicy.Overwrite);

    /// <summary>
    /// Decides a job's next record after an attempt. A transfer that succeeded is Completed, and one that
    /// committed its destination but did not finish (a move whose source is still there) needs reconciliation,
    /// even if it was being paused, cancelled, or shut down as it got there: re-running it would repeat work
    /// already done. An attempt stopped by the job store is tried again without using up a retry, until
    /// <see cref="MaxStoreFailures"/> in a row make the job fail (<c>GaveUp</c>).
    /// </summary>
    private (StorageTransferJobRecord Next, bool Transient, bool GaveUp) Decide(
        Entry entry,
        Result result,
        StorageTransferReport? report,
        StorageTransferPhase phase,
        LeaseHolder holder,
        ControlRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        StorageTransferJobRecord current;
        lock (_gate) current = entry.Record;
        var record = current with
        {
            Checkpoint = new StorageTransferCheckpoint(phase, report?.ResumeToken ?? (result.IsFailure ? current.Checkpoint.ResumeToken : null)),
            Failure = StorageTransferFailure.From(result.Error)
        };
        if (result.IsSuccess)
            return (record with { State = StorageTransferState.Completed, FinishedAt = now, Failure = null, Checkpoint = new StorageTransferCheckpoint(phase) }, false, false);

        var error = result.Error!;
        if (report?.Outcome == StorageTransferOutcome.NeedsReconciliation || error.Code == StorageErrors.PartialFailureCode || StorageErrorInfo.DestinationCommitted(error))
            return (record with { State = StorageTransferState.NeedsReconciliation }, false, false);
        if (request == ControlRequest.Pause)
            return (record with { State = StorageTransferState.Paused, Failure = null }, false, false);
        if (request == ControlRequest.Cancel)
            return (record with { State = StorageTransferState.Cancelled, FinishedAt = now, Failure = null }, false, false);
        if (_shutdown.IsCancellationRequested)
            return (Recover(record) with { Failure = null }, false, false);
        if (holder.StoreFailed)
        {
            // The job store failed, not the transfer: try again later without using up a retry, but not for ever.
            int failures;
            lock (_gate) failures = ++entry.StoreFailures;
            if (failures >= MaxStoreFailures)
                return (record with { State = StorageTransferState.Failed, FinishedAt = now, Failure = StorageTransferFailure.From(StoreGaveUp(failures)) }, false, true);
            return (record with
            {
                State = StorageTransferState.Queued,
                NextAttemptAt = now + StoreFailureDelay(failures),
                Failure = StorageTransferFailure.From(StorageErrors.Unavailable("The transfer was stopped because the job store failed."))
            }, false, false);
        }
        if (BlockReasonFor(error) is { } reason)
            return (record with { State = StorageTransferState.Blocked, BlockReason = reason }, false, false);
        if (StorageErrorInfo.IsTransient(error) && record.RetriesLeft > 0)
        {
            return (record with
            {
                State = StorageTransferState.Queued,
                RetriesLeft = record.RetriesLeft - 1,
                NextAttemptAt = now + Backoff(record.Attempts, error)
            }, true, false);
        }
        return (record with { State = StorageTransferState.Failed, FinishedAt = now }, StorageErrorInfo.IsTransient(error), false);
    }

    /// <summary>Trust and credential failures need a person, not a retry.</summary>
    internal static StorageTransferBlockReason? BlockReasonFor(Error error)
    {
        if (error.Code == StorageErrors.HostKeyRejectedCode) return StorageTransferBlockReason.Trust;
        if (error.Code == StorageErrors.AuthenticationFailedCode) return StorageTransferBlockReason.Credential;
        if (error.Code != StorageErrors.TlsFailureCode) return null;
        StorageErrorInfo.TryGetDetail(error, StorageErrorInfo.TlsReasonKey, out var reason);
        return reason switch
        {
            TlsDiagnosis.ServerCertificateRejected => StorageTransferBlockReason.Trust,
            TlsDiagnosis.ClientCertificateRejected => StorageTransferBlockReason.Credential,
            _ => null
        };
    }

    /// <summary>Exponential backoff with ±50% jitter, clamped, and never shorter than a server's Retry-After (up to 30 days).</summary>
    private TimeSpan Backoff(int attempt, Error error)
    {
        var exponential = _options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, Math.Min(attempt - 1, 20)));
        var jittered = exponential * (0.5 + Random.Shared.NextDouble());
        var delay = TimeSpan.FromMilliseconds(Math.Min(jittered, _options.RetryMaxDelay.TotalMilliseconds));
        if (!StorageErrorInfo.TryGetRetryAfter(error, out var requested) || requested <= delay) return delay;
        return requested > LongestRetryAfter ? LongestRetryAfter : requested;
    }
}
